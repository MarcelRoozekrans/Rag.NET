using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Covers <see cref="OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength"/>, which had
/// no test anywhere before Phase 3.13.
/// </summary>
/// <remarks>
/// <para>
/// That absence is how the newline defect survived: this guard silently disabled late chunking for
/// every multi-line document for months, and nothing called it. The unit suites exercise the
/// <c>WindowRunner</c> seam, which never reaches the tokenizer, and the one integration test that
/// would have caught it could not run without a model file.
/// </para>
/// <para>
/// Both directions are pinned because they are different problems: normalization GROWS CJK text
/// (measured 8 → 14, a space inserted either side of every ideograph) and SHRINKS NFD-decomposed
/// text (measured 14 → 11, combining marks stripped). The message has to say which, so a reader can
/// tell a Japanese corpus from a macOS-normalized filename.
/// </para>
/// </remarks>
public sealed class NormalizationGuardTests
{
    private const string SkipReason =
        "Set RAGNET_ONNX_EMBED_VOCAB to an existing WordPiece vocab.txt (e.g. all-MiniLM-L6-v2's) " +
        "to run the guard's real-tokenizer cases.";

    /// <summary>Position-preserving normalization is the case the guard must let through.</summary>
    [Fact]
    public void EqualLengths_AreAccepted()
    {
        OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength("alpha beta".Length, "alpha beta");
    }

    /// <summary>
    /// The tokenizer returns a null normalized text even when it normalized (0.22.0), so a null
    /// means "nothing to compare", not "the length changed". Refusing it would refuse everything.
    /// </summary>
    [Fact]
    public void NullNormalizedText_IsAccepted()
    {
        OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength(10, normalized: null);
    }

    /// <summary>A shorter normalization is the NFD direction, and the message must say so.</summary>
    [Fact]
    public void ShorterNormalization_ThrowsNamingNfd()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength(14, new string('x', 11)));

        Assert.Contains("shrank", ex.Message, StringComparison.Ordinal);
        Assert.Contains("14", ex.Message, StringComparison.Ordinal);
        Assert.Contains("11", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NFD", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CJK", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A longer normalization is the CJK direction, and the message must say so.</summary>
    [Fact]
    public void LongerNormalization_ThrowsNamingCjk()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength(8, new string('x', 14)));

        Assert.Contains("grew", ex.Message, StringComparison.Ordinal);
        Assert.Contains("8", ex.Message, StringComparison.Ordinal);
        Assert.Contains("14", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CJK", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NFD", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message must rule out the cause Task 2 removed. <c>\n</c>, <c>\t</c> and <c>\r</c> are
    /// substituted with a space before the guard runs, so a reader who goes looking for a line
    /// break is looking in the wrong place — while the rarer control characters are still deleted
    /// and still a live cause, which is why the advice is qualified rather than dropped.
    /// </summary>
    [Fact]
    public void TheMessage_RulesOutTheWhitespaceThatIsNowSubstituted()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength(10, new string('x', 9)));

        Assert.Contains("cannot be the cause", ex.Message, StringComparison.Ordinal);
        Assert.Contains("substituted with a space", ex.Message, StringComparison.Ordinal);
        Assert.Contains("control character", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Through the real tokenizer: CJK grows, and the refusal names CJK. This is the limit the
    /// design keeps rather than fixes — the probe showed token offsets going out of bounds on
    /// <c>"日本語 text"</c>, so refusing is correct, not cautious.
    /// </summary>
    [Fact]
    public async Task Cjk_ThroughTheRealTokenizer_IsRefusedNamingCjk()
    {
        var message = await RefusalMessageAsync("日本語 text");

        Assert.Contains("grew", message, StringComparison.Ordinal);
        Assert.Contains("CJK", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Through the real tokenizer: NFD shrinks, and the refusal names NFD and the NFC remedy —
    /// the one length-changing cause a caller can actually do something about.
    /// </summary>
    [Fact]
    public async Task Nfd_ThroughTheRealTokenizer_IsRefusedNamingNfd()
    {
        // 'e' + COMBINING ACUTE ACCENT, as an escape so no editor can silently recompose it.
        var message = await RefusalMessageAsync("cafe\u0301 test");

        Assert.Contains("shrank", message, StringComparison.Ordinal);
        Assert.Contains("NFD", message, StringComparison.Ordinal);
        Assert.Contains("NFC", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <paramref name="text"/> through the generator's window-runner seam (vocabulary only,
    /// no ONNX model) and returns the message it refused with.
    /// </summary>
    private static async Task<string> RefusalMessageAsync(string text)
    {
        var vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_VOCAB");
        Assert.SkipWhen(string.IsNullOrEmpty(vocabPath) || !File.Exists(vocabPath), SkipReason);

        using var generator = new OnnxTokenEmbeddingGenerator(
            new OnnxTokenEmbeddingOptions
            {
                ModelPath = "unused/model.onnx", // never touched: the seam bypasses the session
                TokenizerVocabPath = vocabPath!,
            },
            (ids, start, end) => (new float[(end - start) * 2], 2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await generator.GenerateAsync(text, TestContext.Current.CancellationToken));

        return ex.Message;
    }
}
