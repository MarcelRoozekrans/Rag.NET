using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Exercises <see cref="OnnxTokenEmbeddingGenerator.GenerateAsync"/> through the internal
/// window-runner seam (no ONNX model needed — only a tiny temp WordPiece vocab), plus the
/// static output-resolution/validation error paths.
/// </summary>
public sealed class OnnxTokenEmbeddingGeneratorGenerateAsyncTests : IDisposable
{
    private readonly string _vocabPath;

    public OnnxTokenEmbeddingGeneratorGenerateAsyncTests()
    {
        _vocabPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".vocab.txt");
        File.WriteAllLines(_vocabPath,
            ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "alpha", "bravo", "charlie", "delta"]);
    }

    public void Dispose() => File.Delete(_vocabPath);

    private OnnxTokenEmbeddingGenerator CreateSut(
        OnnxTokenEmbeddingGenerator.WindowRunner runner, int maxTokens = 8192, int overlap = 64) =>
        new(new OnnxTokenEmbeddingOptions
        {
            ModelPath = "unused/model.onnx", // never touched: the seam bypasses the session
            TokenizerVocabPath = _vocabPath,
            MaxTokens = maxTokens,
            WindowOverlapTokens = overlap,
        }, runner);

    private static OnnxTokenEmbeddingGenerator.WindowRunner TaggedRunner(int dimension, Action? onCall = null) =>
        (ids, start, end) =>
        {
            onCall?.Invoke();
            var matrix = new float[(end - start) * dimension];
            Array.Fill(matrix, start); // rows carry their window's start as a provenance tag
            return (matrix, dimension);
        };

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task GenerateAsync_NoTokens_ReturnsEmptySentinel_WithoutRunningModel(string text)
    {
        var calls = 0;
        var sut = CreateSut(TaggedRunner(dimension: 2, onCall: () => calls++));

        var result = await sut.GenerateAsync(text, TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.True(result.Embeddings.IsEmpty);
        Assert.Equal(1, result.Dimension);
        Assert.Empty(result.TokenOffsets);
    }

    [Fact]
    public async Task GenerateAsync_MultiWindow_StitchesWithLaterWindowWinningOverlap()
    {
        // MaxTokens 4 → content budget 2, overlap 1 → step 1: windows (0,2), (1,3), (2,4)
        // over the 4 word tokens. Later window wins: row tags must be [0, 1, 2, 2].
        var sut = CreateSut(TaggedRunner(dimension: 2), maxTokens: 4, overlap: 1);

        var result = await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Dimension);
        Assert.Equal(4, result.TokenOffsets.Count);
        Assert.Equal([(0, 5), (6, 11), (12, 19), (20, 25)], result.TokenOffsets);
        Assert.Equal(8, result.Embeddings.Length);

        float[] expectedRowTags = [0f, 1f, 2f, 2f];
        var matrix = result.Embeddings.ToArray();
        for (var token = 0; token < 4; token++)
        {
            Assert.Equal(expectedRowTags[token], matrix[token * 2]);
            Assert.Equal(expectedRowTags[token], matrix[(token * 2) + 1]);
        }
    }

    [Fact]
    public async Task GenerateAsync_PassesVocabTokenIdsToTheRunner()
    {
        int[]? seenIds = null;
        var sut = CreateSut((ids, start, end) =>
        {
            seenIds = ids;
            return (new float[(end - start) * 2], 2);
        });

        await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken);

        // Line index == token id in the temp vocab; no [CLS]/[SEP] in the content ids.
        Assert.NotNull(seenIds);
        Assert.Equal([5, 6, 7, 8], seenIds);
    }

    [Fact]
    public async Task GenerateAsync_CancellationBetweenWindows_StopsFurtherPasses()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var sut = CreateSut((ids, start, end) =>
        {
            calls++;
            cts.Cancel(); // cancel during the first pass — the loop must stop before pass 2
            return (new float[(end - start) * 2], 2);
        }, maxTokens: 4, overlap: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GenerateAsync("alpha bravo charlie delta", cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GenerateAsync_DimensionChangesBetweenWindows_Throws()
    {
        var sut = CreateSut((ids, start, end) =>
        {
            var dimension = start == 0 ? 2 : 3; // second window disagrees
            return (new float[(end - start) * dimension], dimension);
        }, maxTokens: 4, overlap: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken));

        Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_NormalizationChangesTextLength_ThrowsClearError()
    {
        // BERT normalization strips the control character, so token offsets would index a
        // 10-char normalized string while the input has 11 chars — must fail loudly instead
        // of returning misaligned offsets. The model must never run.
        var calls = 0;
        var sut = CreateSut(TaggedRunner(dimension: 2, onCall: () => calls++));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync("alpha\u0001bravo", TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
        Assert.Contains("normalization changed the text length", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11", ex.Message, StringComparison.Ordinal);
        Assert.Contains("10", ex.Message, StringComparison.Ordinal);
    }

    // ── Output resolution / shape validation (Important-fix error paths) ─────

    [Fact]
    public void ResolveOutputName_PreferredDeclared_ReturnsPreferred()
    {
        var name = OnnxTokenEmbeddingGenerator.ResolveOutputName(
            ["logits", "last_hidden_state"], "last_hidden_state");

        Assert.Equal("last_hidden_state", name);
    }

    [Fact]
    public void ResolveOutputName_PreferredMissingButSingleOutput_FallsBackToIt()
    {
        var name = OnnxTokenEmbeddingGenerator.ResolveOutputName(["token_embeddings"], "last_hidden_state");

        Assert.Equal("token_embeddings", name);
    }

    [Fact]
    public void ResolveOutputName_PreferredMissingAmongMultiple_ThrowsListingActualOutputs()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxTokenEmbeddingGenerator.ResolveOutputName(
                ["logits", "pooler_output"], "last_hidden_state"));

        Assert.Contains("last_hidden_state", ex.Message, StringComparison.Ordinal);
        Assert.Contains("logits", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pooler_output", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OutputName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOutputShape_TokenLevelShape_Passes()
    {
        OnnxTokenEmbeddingGenerator.ValidateOutputShape([1, 12, 384], expectedSequenceLength: 12, "last_hidden_state");
    }

    [Fact]
    public void ValidateOutputShape_PooledExport_ThrowsNamingShapeAndOutput()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxTokenEmbeddingGenerator.ValidateOutputShape([1, 384], expectedSequenceLength: 12, "sentence_embedding"));

        Assert.Contains("sentence_embedding", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[1, 384]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pooled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOutputShape_WrongSequenceLength_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxTokenEmbeddingGenerator.ValidateOutputShape([1, 10, 384], expectedSequenceLength: 12, "last_hidden_state"));

        Assert.Contains("[1, 10, 384]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("12", ex.Message, StringComparison.Ordinal);
    }
}
