using Microsoft.Extensions.Logging;
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
            return (new float[2], 2);
        });

        var result = await sut.GenerateAsync(text, TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task GenerateAsync_MultiWindow_MergesByElementWiseMax()
    {
        // MaxTokens 4 → content budget 2, overlap 0 → windows (0,2), (2,4) over the 4 word
        // tokens. The runner returns POOLED per-vocab weights (vocab = 3):
        //   window (0,2): [2, 1, 0]
        //   window (2,4): [0, 3, 0]
        // merged element-wise max: [2, 3, 0] → pruned: indices [0, 1], values [2, 3].
        // (Pooling math itself — ReLU/log1p/max over tokens — is covered by SpladePoolingTests.)
        var sut = CreateSut((ids, start, end) =>
            start == 0 ? (new float[] { 2f, 1f, 0f }, 3) : (new float[] { 0f, 3f, 0f }, 3),
            maxTokens: 4);

        var result = await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal([0, 1], result.Indices.ToArray());
        Assert.Equal(2f, result.Values.Span[0]);
        Assert.Equal(3f, result.Values.Span[1]);
    }

    [Fact]
    public async Task GenerateAsync_PassesVocabTokenIdsToTheRunner()
    {
        int[]? seenIds = null;
        var sut = CreateSut((ids, start, end) =>
        {
            seenIds = ids;
            return (new float[2], 2);
        });

        await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken);

        // Line index == token id in the temp vocab; no [CLS]/[SEP] in the content ids.
        Assert.NotNull(seenIds);
        Assert.Equal([5, 6, 7, 8], seenIds);
    }

    [Fact]
    public async Task GenerateAsync_TopTermsPrunesToLargestWeights()
    {
        var sut = CreateSut((ids, start, end) => (new float[] { 1f, 5f, 3f }, 3), topTerms: 1);

        var result = await sut.GenerateAsync("alpha bravo", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Count);
        Assert.Equal([1], result.Indices.ToArray());
        Assert.Equal(5f, result.Values.Span[0]);
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
            return (new float[2], 2);
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
            return (new float[vocab], vocab);
        }, maxTokens: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync("alpha bravo charlie delta", TestContext.Current.CancellationToken));

        Assert.Contains("vocabulary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_WeightsLengthMismatchesVocabularySize_Throws()
    {
        var sut = CreateSut((ids, start, end) => (new float[5], 3));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync("alpha bravo", TestContext.Current.CancellationToken));

        Assert.Contains("pooled weights", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Model/tokenizer vocabulary cross-check ───────────────────────────────

    private sealed class CapturingLogger : ILogger<OnnxSpladeEncoder>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task GenerateAsync_ModelVocabularyDiffersFromTokenizerVocab_WarnsOnce()
    {
        // The temp vocab file has 9 lines; the fake model reports vocabulary size 3.
        var logger = new CapturingLogger();
        var sut = new OnnxSpladeEncoder(
            new OnnxSpladeOptions { ModelPath = "unused/model.onnx", TokenizerVocabPath = _vocabPath },
            (ids, start, end) => (new float[3], 3),
            logger);

        await sut.GenerateAsync("alpha bravo", TestContext.Current.CancellationToken);
        await sut.GenerateAsync("charlie delta", TestContext.Current.CancellationToken);

        var warning = Assert.Single(logger.Warnings); // once per instance, not per call
        Assert.Contains("3", warning, StringComparison.Ordinal);
        Assert.Contains("9", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_MatchingVocabularySizes_NoWarning()
    {
        var logger = new CapturingLogger();
        var sut = new OnnxSpladeEncoder(
            new OnnxSpladeOptions { ModelPath = "unused/model.onnx", TokenizerVocabPath = _vocabPath },
            (ids, start, end) => (new float[9], 9),
            logger);

        await sut.GenerateAsync("alpha bravo", TestContext.Current.CancellationToken);

        Assert.Empty(logger.Warnings);
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
