using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class LateChunkingStrategyTests
{
    private static DocumentSection Section(string text, string docId = "doc1") =>
        new() { DocumentId = new DocumentId(docId), Text = text };

    private static async IAsyncEnumerable<DocumentSection> ToAsync(IEnumerable<DocumentSection> sections)
    {
        foreach (var s in sections) yield return s;
        await Task.CompletedTask;
    }

    private static LateChunkingStrategy MakeSut(ITokenEmbeddingGenerator generator, LateChunkingOptions options) =>
        new(generator, options, NullLogger<LateChunkingStrategy>.Instance);

    /// <summary>
    /// Deterministic fake: tokenizes by whitespace words (offsets = each word's char span in
    /// the input), per-token vectors chosen by the test via <c>vectorForToken</c>.
    /// </summary>
    private sealed class FakeTokenEmbeddingGenerator(
        int dimension = 2,
        Func<int, float[]>? vectorForToken = null,
        Exception? failure = null) : ITokenEmbeddingGenerator
    {
        public int MaxTokens => 8192;

        public ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            if (failure is not null) throw failure;

            var offsets = Tokenize(text);
            var embeddings = new float[offsets.Count * dimension];
            for (var t = 0; t < offsets.Count; t++)
            {
                var vector = vectorForToken?.Invoke(t) ?? DefaultVector(t);
                for (var d = 0; d < dimension; d++)
                    embeddings[(t * dimension) + d] = vector[d];
            }

            return ValueTask.FromResult(new TokenEmbeddingResult
            {
                Embeddings = embeddings,
                Dimension = dimension,
                TokenOffsets = offsets,
            });
        }

        private float[] DefaultVector(int token)
        {
            var vector = new float[dimension];
            vector[token % dimension] = 1f;
            return vector;
        }

        private static List<(int Start, int End)> Tokenize(string text)
        {
            var offsets = new List<(int Start, int End)>();
            var i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                offsets.Add((start, i));
            }

            return offsets;
        }
    }

    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var options = new LateChunkingOptions();

        Assert.Equal(256, options.WindowSizeTokens);
        Assert.Equal(32, options.OverlapTokens);
        Assert.Null(options.Generator);
    }

    [Theory]
    [InlineData(0, 0)]   // window not positive
    [InlineData(4, -1)]  // overlap negative
    [InlineData(4, 4)]   // overlap == window
    [InlineData(4, 5)]   // overlap > window
    public void InvalidOptions_ThrowAtConstruction(int window, int overlap)
    {
        var options = new LateChunkingOptions { WindowSizeTokens = window, OverlapTokens = overlap };

        Assert.Throws<ArgumentOutOfRangeException>(() => MakeSut(new FakeTokenEmbeddingGenerator(), options));
    }

    [Fact]
    public async Task Windows_MapBackToText()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(),
            new LateChunkingOptions { WindowSizeTokens = 4, OverlapTokens = 1 });

        var chunks = await sut.ChunkAsync(Section(text), new ChunkingOptions(), ct).ToListAsync(ct);

        // 10 words, window 4, overlap 1 → step 3 → token windows [0..3], [3..6], [6..9].
        Assert.Equal(3, chunks.Count);
        Assert.Equal("alpha bravo charlie delta", chunks[0].Text);
        Assert.Equal("delta echo foxtrot golf", chunks[1].Text);
        Assert.Equal("golf hotel india juliet", chunks[2].Text);
        Assert.All(chunks, c => Assert.Equal(c.Text, text[c.StartPosition..c.EndPosition]));
    }

    [Fact]
    public async Task MeanPooling_IsCorrect_AndL2Normalized()
    {
        var ct = TestContext.Current.CancellationToken;
        float[][] vectors = [[1f, 0f], [0f, 1f]];
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(dimension: 2, vectorForToken: t => vectors[t]),
            new LateChunkingOptions { WindowSizeTokens = 2, OverlapTokens = 0 });

        var chunks = await sut.ChunkAsync(Section("alpha bravo"), new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        Assert.NotNull(chunk.Embedding);
        var embedding = chunk.Embedding.Value.ToArray();
        Assert.Equal(2, embedding.Length);
        // Mean of (1,0) and (0,1) is (0.5, 0.5); L2-normalized → (1/√2, 1/√2).
        Assert.Equal(0.70710678f, embedding[0], 1e-4f);
        Assert.Equal(0.70710678f, embedding[1], 1e-4f);
    }

    [Fact]
    public async Task GeneratorFails_FallsBackToUnembeddedTokenWindows()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(failure: new InvalidOperationException("model exploded")),
            new LateChunkingOptions { WindowSizeTokens = 4, OverlapTokens = 1 });

        var chunks = await sut.ChunkAsync(Section(text), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.Null(c.Embedding);
            Assert.False(string.IsNullOrWhiteSpace(c.Text));
        });
    }

    [Fact]
    public async Task DimensionMismatch_FallsBackToUnembeddedTokenWindows()
    {
        var ct = TestContext.Current.CancellationToken;
        // 3 tokens x dimension 2 requires 6 floats; a broken generator reporting a mismatched
        // matrix must be treated as a failure → fallback chunks without embeddings.
        var sut = MakeSut(new BrokenGenerator(), new LateChunkingOptions { WindowSizeTokens = 4, OverlapTokens = 1 });

        var chunks = await sut.ChunkAsync(Section("alpha bravo charlie"), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Null(c.Embedding));
    }

    private sealed class BrokenGenerator : ITokenEmbeddingGenerator
    {
        public int MaxTokens => 8192;

        public ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TokenEmbeddingResult
            {
                Embeddings = new float[4], // wrong: 3 tokens x dim 2 = 6 expected
                Dimension = 2,
                TokenOffsets = [(0, 5), (6, 11), (12, 19)],
            });
    }

    [Fact]
    public async Task EmptySection_YieldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(new FakeTokenEmbeddingGenerator(), new LateChunkingOptions());

        var empty = await sut.ChunkAsync(Section(""), new ChunkingOptions(), ct).ToListAsync(ct);
        var whitespace = await sut.ChunkAsync(Section("   \n\t "), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Empty(empty);
        Assert.Empty(whitespace);
    }

    [Fact]
    public async Task ChunkIndexes_AreSequential_AcrossSections()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(),
            new LateChunkingOptions { WindowSizeTokens = 2, OverlapTokens = 0 });

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([
                Section("alpha bravo charlie delta"),
                Section("echo foxtrot golf hotel"),
            ]),
            new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.True(chunks.Count > 2, $"expected chunks from both sections, got {chunks.Count}");
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledException()
    {
        var sut = MakeSut(new FakeTokenEmbeddingGenerator(), new LateChunkingOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.ChunkAsync(Section("alpha bravo"), new ChunkingOptions(), cts.Token).ToListAsync(cts.Token));
    }

    [Fact]
    public async Task GeneratorCancellation_IsNotSwallowedByFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(failure: new OperationCanceledException()),
            new LateChunkingOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.ChunkAsync(Section("alpha bravo"), new ChunkingOptions(), ct).ToListAsync(ct));
    }

    [Fact]
    public async Task PrecomputedEmbedding_NeverEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(dimension: 3),
            new LateChunkingOptions { WindowSizeTokens = 4, OverlapTokens = 1 });

        var chunks = await sut.ChunkAsync(Section(text), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            // Part B contract: empty means absent — happy-path chunks must carry a real vector.
            Assert.NotNull(c.Embedding);
            Assert.Equal(3, c.Embedding.Value.Length);
        });
    }

    [Fact]
    public async Task ZeroVectors_LeaveEmbeddingNull_RatherThanZeroVector()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(
            new FakeTokenEmbeddingGenerator(dimension: 2, vectorForToken: _ => [0f, 0f]),
            new LateChunkingOptions { WindowSizeTokens = 2, OverlapTokens = 0 });

        var chunks = await sut.ChunkAsync(Section("alpha bravo"), new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        Assert.Null(chunk.Embedding);
    }
}
