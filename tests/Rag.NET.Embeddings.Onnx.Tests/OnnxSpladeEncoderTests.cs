using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Exercises <see cref="OnnxSpladeEncoder.GenerateAsync"/> through the internal window-runner
/// seam (no ONNX model needed — only a tiny temp WordPiece vocab), plus constructor
/// validation.
/// </summary>
public sealed class OnnxSpladeEncoderTests : IDisposable
{
    private readonly string _vocabPath;

    public OnnxSpladeEncoderTests()
    {
        _vocabPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".vocab.txt");
        File.WriteAllLines(_vocabPath,
            ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "alpha", "bravo", "charlie", "delta"]);
    }

    public void Dispose() => File.Delete(_vocabPath);

    private OnnxSpladeEncoder CreateSut(
        OnnxSpladeEncoder.WindowRunner runner, int maxTokens = 512, int topTerms = 256) =>
        new(new OnnxSpladeOptions
        {
            ModelPath = "unused/model.onnx", // never touched: the seam bypasses the session
            TokenizerVocabPath = _vocabPath,
            MaxTokens = maxTokens,
            TopTerms = topTerms,
        }, runner);

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task GenerateAsync_NoTokens_ReturnsEmptyVector_WithoutRunningModel(string text)
    {
        var calls = 0;
        var sut = CreateSut((ids, start, end) =>
        {
            calls++;
            return (new float[(end - start) * 2], 2);
        });

        var result = await sut.GenerateAsync(text, TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task GenerateAsync_MultiWindow_MergesByElementWiseMax()
    {
        // MaxTokens 4 → content budget 2, overlap 0 → windows (0,2), (2,4) over the 4 word
        // tokens. vocab = 3:
        //   window (0,2): every row [2, 1, -1] → pooled [ln 3, ln 2, 0]
        //   window (2,4): every row [0, 3, -1] → pooled [0, ln 4, 0]
        // merged max: [ln 3, ln 4, 0] → pruned: indices [0, 1], values [ln 3, ln 4].
        var sut = CreateSut((ids, start, end) =>
        {
            var rows = end - start;
            var logits = new float[rows * 3];
            for (var r = 0; r < rows; r++)
            {
                logits[r * 3] = start == 0 ? 2f : 0f;
                logits[(r * 3) + 1] = start == 0 ? 1f : 3f;
                logits[(r * 3) + 2] = -1f;
            }
            return (logits, 3);
        }, maxTokens: 4);

        var result = await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal([0, 1], result.Indices.ToArray());
        Assert.Equal(MathF.Log(3f), result.Values.Span[0], precision: 5);
        Assert.Equal(MathF.Log(4f), result.Values.Span[1], precision: 5);
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
    public async Task GenerateAsync_TopTermsPrunesToLargestWeights()
    {
        var sut = CreateSut((ids, start, end) =>
        {
            var rows = end - start;
            var logits = new float[rows * 3];
            for (var r = 0; r < rows; r++)
            {
                logits[r * 3] = 1f;
                logits[(r * 3) + 1] = 5f;
                logits[(r * 3) + 2] = 3f;
            }
            return (logits, 3);
        }, topTerms: 1);

        var result = await sut.GenerateAsync("alpha bravo", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Count);
        Assert.Equal([1], result.Indices.ToArray());
        Assert.Equal(MathF.Log(6f), result.Values.Span[0], precision: 5);
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
        }, maxTokens: 4);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GenerateAsync("alpha bravo charlie delta", cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GenerateAsync_VocabularySizeChangesBetweenWindows_Throws()
    {
        var sut = CreateSut((ids, start, end) =>
        {
            var vocab = start == 0 ? 2 : 3; // second window disagrees
            return (new float[(end - start) * vocab], vocab);
        }, maxTokens: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken));

        Assert.Contains("vocabulary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Constructor validation ───────────────────────────────────────────────

    [Fact]
    public void Ctor_MissingModelFile_Throws()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => new OnnxSpladeEncoder(new OnnxSpladeOptions
        {
            ModelPath = "nonexistent/model.onnx",
            TokenizerVocabPath = _vocabPath,
        }));

        Assert.Contains("model.onnx", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_MissingVocabFile_Throws()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => new OnnxSpladeEncoder(new OnnxSpladeOptions
        {
            ModelPath = "unused/model.onnx",
            TokenizerVocabPath = "nonexistent/vocab.txt",
        }, (ids, start, end) => (new float[end - start], 1)));

        Assert.Contains("vocab.txt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_NonPositiveTopTerms_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSut((ids, start, end) => (new float[end - start], 1), topTerms: 0));
    }

    [Fact]
    public void Ctor_MaxTokensNotAboveSpecialTokenBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSut((ids, start, end) => (new float[end - start], 1), maxTokens: 2));
    }
}
