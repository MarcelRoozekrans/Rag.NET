using System.Buffers;
using Microsoft.ML.Tokenizers;

namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// Setup and validation shared by every BERT/ONNX encoder in this assembly
/// (<see cref="OnnxTokenEmbeddingGenerator"/>, <see cref="OnnxSpladeEncoder"/>,
/// <see cref="OnnxEmbeddingGenerator"/>): file presence checks, tokenizer construction and use,
/// model output resolution and the rank-3 output shape predicate.
/// </summary>
/// <remarks>
/// <para>
/// Extracted when the third encoder landed. The parts that differ between encoders are the
/// REMEDY sentences — each names its own options type — so those are passed in rather than
/// generalised away, which keeps every error message exactly as specific as it was.
/// </para>
/// <para>
/// The shape check stays split into <see cref="IsRank3WithSequenceLength"/> plus a
/// caller-owned throw: the message interpolates the expected sequence length, so a single
/// combined helper would build that string on every successful model pass.
/// </para>
/// <para>
/// Deliberately NOT shared: the per-pass tensor feed. The two token-level encoders feed one
/// unpadded sequence per pass while <see cref="OnnxEmbeddingGenerator"/> feeds a padded batch,
/// and neither of the existing feeds is covered by a test that runs without a model file — so
/// folding them together would be a refactor with nothing to catch a mistake in it.
/// </para>
/// </remarks>
internal static class BertOnnxPlumbing
{
    /// <summary>
    /// The characters BERT's reference implementation treats as whitespace but
    /// <see cref="BertTokenizer"/>'s normalizer deletes as control characters.
    /// </summary>
    private static readonly SearchValues<char> DeletedWhitespace = SearchValues.Create("\n\r\t");

    /// <summary>
    /// Tokenizes <paramref name="text"/> into content tokens (no special tokens), after
    /// substituting a space for the whitespace the normalizer would otherwise delete.
    /// <paramref name="encodedText"/> is the string the tokenizer actually saw — the same string
    /// every offset in the result indexes.
    /// </summary>
    /// <remarks>
    /// Every encoder in this assembly goes through here rather than calling
    /// <see cref="BertTokenizer.EncodeToTokens(string, out string?, bool, bool)"/> directly,
    /// because deleting a newline merges the words either side of it into one the document never
    /// contained (<c>"alpha\n\nbeta gamma"</c> tokenized as <c>alphabet | ##a | gamma</c>). That
    /// corrupts the token stream, so it is not specific to the one encoder that also reads
    /// offsets: <see cref="OnnxSpladeEncoder"/> and <see cref="OnnxEmbeddingGenerator"/> discard
    /// the offsets and so never saw an error, but embedded the merged word all the same.
    /// </remarks>
    internal static IReadOnlyList<EncodedToken> EncodeToTokens(
        BertTokenizer tokenizer, string text, out string encodedText, out string? normalizedText)
    {
        encodedText = SubstituteWhitespace(text);
        return tokenizer.EncodeToTokens(encodedText, out normalizedText);
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every <c>\n</c>, <c>\r</c> and <c>\t</c> replaced by a
    /// single space, and nothing else changed.
    /// </summary>
    /// <remarks>
    /// One character in, one character out, deliberately: the result is the same length as the
    /// input, so a token offset into it is also a valid offset into the original — which is what
    /// <see cref="Rag.NET.Abstractions.ITokenEmbeddingGenerator"/> promises. Trimming, collapsing
    /// runs of whitespace or normalizing any other character would all change the length and
    /// reintroduce exactly the defect this exists to fix.
    /// <para>
    /// Written with index arithmetic rather than the <c>ReadOnlySpan</c> extension methods
    /// (<c>ContainsAny</c>, <c>IndexOfAny</c>), which take the span by value and trip the EPS06
    /// hidden-struct-copy analyzer — an error in this repo. Same reason as
    /// <c>FileNameSanitizer</c>.
    /// </para>
    /// </remarks>
    internal static string SubstituteWhitespace(string text)
    {
        if (IndexOfDeletedWhitespace(text) < 0)
            return text;

        return string.Create(text.Length, text, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var character = source[i];
                destination[i] = DeletedWhitespace.Contains(character) ? ' ' : character;
            }
        });
    }

    /// <summary>
    /// The first index of a character the normalizer would delete, or -1. Lets the common
    /// single-line case return its input untouched.
    /// </summary>
    private static int IndexOfDeletedWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (DeletedWhitespace.Contains(text[i]))
                return i;
        }

        return -1;
    }

    /// <summary>Throws <see cref="FileNotFoundException"/> when the ONNX model file is absent.</summary>
    internal static void EnsureModelFileExists(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model file not found: {modelPath}", modelPath);
    }

    /// <summary>Throws <see cref="FileNotFoundException"/> when the vocabulary file is absent.</summary>
    internal static void EnsureVocabularyFileExists(string vocabPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"BERT vocabulary file not found: {vocabPath}", vocabPath);
    }

    /// <summary>
    /// Creates the WordPiece tokenizer for <paramref name="vocabPath"/>.
    /// </summary>
    /// <remarks>
    /// Explicit <see cref="BertOptions"/>: passing null options makes
    /// <see cref="BertTokenizer.Create(string, BertOptions)"/> skip the Bert normalizer and basic
    /// tokenization entirely (verified against 0.22.0), which would break uncased-vocab matching
    /// (uppercase words all become [UNK]).
    /// </remarks>
    internal static BertTokenizer CreateTokenizer(string vocabPath) =>
        BertTokenizer.Create(vocabPath, new BertOptions());

    /// <summary>
    /// Resolves the model output to read: the preferred name when the model declares it, else
    /// the model's single output, else fails listing the model's actual outputs followed by
    /// <paramref name="remedy"/>.
    /// </summary>
    internal static string ResolveOutputName(
        IReadOnlyList<string> modelOutputs, string preferredName, string remedy)
    {
        for (var i = 0; i < modelOutputs.Count; i++)
        {
            if (string.Equals(modelOutputs[i], preferredName, StringComparison.Ordinal))
                return preferredName;
        }

        if (modelOutputs.Count == 1)
            return modelOutputs[0];

        throw new InvalidOperationException(
            $"Model does not declare an output named '{preferredName}'; its outputs are: {string.Join(", ", modelOutputs)}. " +
            remedy);
    }

    /// <summary>
    /// Whether <paramref name="dimensions"/> is a token-level rank-3 shape whose token axis is
    /// exactly <paramref name="expectedSequenceLength"/>. A rank-2 shape indicates a pooled
    /// export, which carries no per-token rows.
    /// </summary>
    internal static bool IsRank3WithSequenceLength(ReadOnlySpan<int> dimensions, int expectedSequenceLength) =>
        dimensions.Length == 3 && dimensions[1] == expectedSequenceLength;

    /// <summary>Formats a tensor shape as comma-separated dimensions, for error messages.</summary>
    internal static string FormatShape(ReadOnlySpan<int> dimensions)
    {
        var parts = new string[dimensions.Length];
        for (var i = 0; i < dimensions.Length; i++)
            parts[i] = dimensions[i].ToString(System.Globalization.CultureInfo.InvariantCulture);

        return string.Join(", ", parts);
    }
}
