using Microsoft.ML.Tokenizers;
using Rag.NET.Onnx.Bert;

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
/// <para>
/// The tokenizer construction and input normalization moved out again, to
/// <see cref="BertWordPieceTokenization"/> in <c>src/Shared</c>, when the ONNX cross-encoder in
/// <c>Rag.NET.Reranking.Onnx</c> turned out to need the identical WordPiece setup. The three
/// members below stay as forwarders so every encoder in this assembly keeps ONE entry point for
/// BERT/ONNX plumbing — the rest of which (output resolution, shape validation) the reranker has
/// no use for and which therefore does not belong in the shared file.
/// </para>
/// </remarks>
internal static class BertOnnxPlumbing
{
    /// <inheritdoc cref="BertWordPieceTokenization.EncodeToTokens"/>
    internal static IReadOnlyList<EncodedToken> EncodeToTokens(
        BertTokenizer tokenizer, string text, out string encodedText, out string? normalizedText) =>
        BertWordPieceTokenization.EncodeToTokens(tokenizer, text, out encodedText, out normalizedText);

    /// <inheritdoc cref="BertWordPieceTokenization.SubstituteWhitespace"/>
    internal static string SubstituteWhitespace(string text) =>
        BertWordPieceTokenization.SubstituteWhitespace(text);

    /// <inheritdoc cref="BertWordPieceTokenization.CreateTokenizer"/>
    internal static BertTokenizer CreateTokenizer(string vocabPath) =>
        BertWordPieceTokenization.CreateTokenizer(vocabPath);

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
