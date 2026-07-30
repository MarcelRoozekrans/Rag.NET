using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Pins how the BERT/WordPiece plumbing treats <c>\n</c>, <c>\t</c> and <c>\r</c>, and pins the
/// two normalizations it still refuses.
/// </summary>
/// <remarks>
/// <para>
/// Measured against the real <c>all-MiniLM-L6-v2</c> vocabulary before the fix:
/// <c>"alpha\n\nbeta gamma"</c> normalized to <c>"alphabeta gamma"</c> and tokenized as
/// <c>alphabet | ##a | gamma</c>. BERT's reference implementation substitutes a space for a
/// newline; this tokenizer deletes it as a control character, so the words either side merge
/// into one the document never contained. That is wrong output rather than a refusal, which is
/// why restoring only the offsets would not have been enough.
/// </para>
/// <para>
/// Runs through <see cref="OnnxTokenEmbeddingGenerator"/>'s window-runner seam, so only the
/// vocabulary is needed — no ONNX model. The generator is the seam because it is where the
/// promise lives: <c>TokenOffsets</c> are spans into the ORIGINAL text, so a length-preserving
/// substitution is the only kind that can be applied.
/// </para>
/// <para>
/// A real vocabulary is required: the merging is only visible against one that actually
/// contains <c>alphabet</c>, and the toy vocab the other suites build would tokenize every case
/// here to <c>[UNK]</c>.
/// </para>
/// </remarks>
public sealed class WhitespaceNormalizationTests
{
    private const string SkipReason =
        "Set RAGNET_ONNX_EMBED_VOCAB to an existing WordPiece vocab.txt (e.g. all-MiniLM-L6-v2's) " +
        "to run the whitespace normalization pins.";

    /// <summary>
    /// A newline between two words must not merge them: <c>alpha</c> and <c>beta</c> are separate
    /// words, and <c>alphabet</c> appears nowhere in the input.
    /// </summary>
    [Fact]
    public async Task DoubleNewline_DoesNotMergeTheWordsEitherSideIntoOneTheTextNeverContained()
    {
        var vocabPath = ResolveVocabPath();
        Assert.SkipWhen(vocabPath is null, SkipReason);

        var (tokens, _) = await EncodeAsync(vocabPath!, "alpha\n\nbeta gamma");

        Assert.DoesNotContain(tokens, t => t.StartsWith("alphabet", StringComparison.Ordinal));
        Assert.Equal(["alpha", "beta", "gamma"], tokens);
    }

    /// <summary>
    /// Every whitespace separator leaves the text length unchanged under normalization, so the
    /// offset guard passes and each offset slices the ORIGINAL text back to its own token.
    /// </summary>
    [Theory]
    [InlineData("alpha\nbeta")]
    [InlineData("alpha\tbeta")]
    [InlineData("alpha\r\nbeta")]
    [InlineData("alpha beta\n")]
    public async Task WhitespaceSeparators_KeepTokensSeparate_AndOffsetsIndexTheOriginalText(string text)
    {
        var vocabPath = ResolveVocabPath();
        Assert.SkipWhen(vocabPath is null, SkipReason);

        var (tokens, offsets) = await EncodeAsync(vocabPath!, text);

        Assert.Equal(["alpha", "beta"], tokens);
        Assert.Equal(tokens.Count, offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            var (start, end) = offsets[i];
            Assert.InRange(start, 0, text.Length);
            Assert.InRange(end, start, text.Length);
            Assert.Equal(tokens[i], text[start..end]);
        }
    }

    /// <summary>
    /// The two documented limits. CJK normalization inserts spaces around each ideograph (8 → 14)
    /// and NFD normalization strips combining marks (10 → 9); neither is length-preserving, so no
    /// substitution can map their offsets back and the guard must keep refusing them. A test
    /// asserting these WORK would assert the opposite of the design.
    /// </summary>
    [Theory]
    [InlineData("日本語 text")]
    [InlineData("café test")] // NFD: 'e' + COMBINING ACUTE ACCENT, written as an escape so it survives editors
    public async Task LengthChangingNormalization_IsStillRefused(string text)
    {
        var vocabPath = ResolveVocabPath();
        Assert.SkipWhen(vocabPath is null, SkipReason);

        using var generator = CreateGenerator(vocabPath!, capture: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await generator.GenerateAsync(text, TestContext.Current.CancellationToken));

        Assert.Contains("length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The substitution is one character for one. Length-preserving is the whole point: an offset
    /// into the substituted string is only a valid offset into the original while the two are the
    /// same length. Needs no vocabulary — it is a pure string operation.
    /// </summary>
    [Theory]
    [InlineData("alpha\n\nbeta gamma")]
    [InlineData("alpha\nbeta")]
    [InlineData("alpha\tbeta")]
    [InlineData("alpha\r\nbeta")]
    [InlineData("alpha beta\n")]
    [InlineData("nothing here needs substituting")]
    [InlineData("")]
    public void SubstituteWhitespace_ReplacesOnlyTheDeletedWhitespace_AndKeepsTheLength(string text)
    {
        var substituted = BertOnnxPlumbing.SubstituteWhitespace(text);

        Assert.Equal(text.Length, substituted.Length);
        for (var i = 0; i < text.Length; i++)
        {
            Assert.Equal(text[i] is '\n' or '\r' or '\t' ? ' ' : text[i], substituted[i]);
        }
    }

    /// <summary>
    /// Not a trim and not a collapse: either would change the length and reintroduce the bug.
    /// </summary>
    [Fact]
    public void SubstituteWhitespace_DoesNotTrimEdgesOrCollapseRuns()
    {
        Assert.Equal("  alpha  beta  ", BertOnnxPlumbing.SubstituteWhitespace("\r\nalpha\t\nbeta \n"));
    }

    private static string? ResolveVocabPath()
    {
        var vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_VOCAB");
        return string.IsNullOrEmpty(vocabPath) || !File.Exists(vocabPath) ? null : vocabPath;
    }

    /// <summary>
    /// Runs <paramref name="text"/> through the generator and returns the content tokens (decoded
    /// from the ids the model would have seen) alongside the offsets it reports.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Tokens, IReadOnlyList<(int Start, int End)> Offsets)> EncodeAsync(
        string vocabPath, string text)
    {
        int[]? capturedIds = null;
        using var generator = CreateGenerator(vocabPath, ids => capturedIds = ids);

        var result = await generator.GenerateAsync(text, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedIds);
        var tokenizer = BertOnnxPlumbing.CreateTokenizer(vocabPath);
        var tokens = new string[capturedIds.Length];
        for (var i = 0; i < capturedIds.Length; i++)
        {
            tokens[i] = tokenizer.Decode([capturedIds[i]]);
        }

        return (tokens, result.TokenOffsets);
    }

    /// <summary>
    /// A generator whose window runner records the ids and returns a zero matrix — no ONNX model
    /// is loaded, so only the vocabulary file is needed.
    /// </summary>
    private static OnnxTokenEmbeddingGenerator CreateGenerator(string vocabPath, Action<int[]>? capture) =>
        new(
            new OnnxTokenEmbeddingOptions
            {
                ModelPath = "unused/model.onnx", // never touched: the seam bypasses the session
                TokenizerVocabPath = vocabPath,
            },
            (ids, start, end) =>
            {
                capture?.Invoke(ids);
                return (new float[(end - start) * 2], 2);
            });
}
