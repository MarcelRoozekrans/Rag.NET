using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class ExtractiveCompressorTests
{
    private static SearchResult MakeResult(string text, string docId = "d", int idx = 0) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = idx },
            Score = 0.5,
        };

    private static IEmbeddingGenerator<string, Embedding<float>> DeterministicEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(ci =>
            {
                var inputs = ci.Arg<IEnumerable<string>>().ToList();
                var embeddings = inputs.Select(s =>
                {
                    var topic = s.Contains("cats", StringComparison.OrdinalIgnoreCase) ? 1f :
                                s.Contains("rockets", StringComparison.OrdinalIgnoreCase) ? -1f : 0f;
                    return new Embedding<float>(new[] { topic, 0f, 0f });
                }).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            });
        return embedder;
    }

    [Fact]
    public async Task CompressAsync_TopNMode_KeepsHighestSimilaritySentences()
    {
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var chunk = "Cats purr loudly. Rockets go to Mars. Cats sleep often. Rockets have engines. Cats like fish.";
        var sources = new List<SearchResult> { MakeResult(chunk) };

        var result = await sut.CompressAsync(sources, "tell me about cats", TestContext.Current.CancellationToken);

        var compressed = result[0].CompressedText;
        Assert.NotNull(compressed);
        Assert.Contains("Cats", compressed, StringComparison.Ordinal);
        Assert.DoesNotContain("Rockets", compressed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressAsync_TokenBudgetMode_StopsAtBudget()
    {
        var opts = new ContextualCompressionOptions
        {
            KeepTopSentences = null,
            MaxTokensPerChunk = 10,
        };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var chunk = "Cats purr. Rockets go. Cats sleep. Rockets fly. Cats eat.";
        var sources = new List<SearchResult> { MakeResult(chunk) };

        var result = await sut.CompressAsync(sources, "tell me about cats", TestContext.Current.CancellationToken);

        var compressed = result[0].CompressedText;
        Assert.NotNull(compressed);
        Assert.Contains("Cats", compressed, StringComparison.Ordinal);
        Assert.DoesNotContain("Rockets", compressed, StringComparison.Ordinal);

        // Verify the token budget is actually respected (not merely mimicking TopN behaviour).
        // Budget is 10; the "always admit top" rule may push one extra short sentence in.
        // A ceiling of 15 still catches a regression where the budget is silently ignored.
        var tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
        var tokenCount = tokenizer.CountTokens(compressed!);
        Assert.True(tokenCount <= 15, $"Expected <=15 tokens, got {tokenCount}. Output: {compressed}");
    }

    [Fact]
    public async Task CompressAsync_EmbeddingFailure_ReturnsOriginalWithNullCompressedText()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(_ =>
                Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new InvalidOperationException("embedder down")));
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(embedder, opts, NullLogger<ExtractiveCompressor>.Instance);
        var sources = new List<SearchResult> { MakeResult("Cats purr. Rockets fly.") };

        var result = await sut.CompressAsync(sources, "cats", TestContext.Current.CancellationToken);

        Assert.Null(result[0].CompressedText);
        Assert.Equal("Cats purr. Rockets fly.", result[0].Chunk.Text);
    }

    [Fact]
    public async Task CompressAsync_EmptyChunk_ReturnsNullCompressedText()
    {
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var sources = new List<SearchResult> { MakeResult("   ") };

        var result = await sut.CompressAsync(sources, "cats", TestContext.Current.CancellationToken);

        Assert.Null(result[0].CompressedText);
    }

    [Fact]
    public async Task CompressAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var sources = new List<SearchResult> { MakeResult("Cats purr.") };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.CompressAsync(sources, "cats", cts.Token));
    }
}
