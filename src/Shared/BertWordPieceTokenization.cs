using System.Buffers;
using Microsoft.ML.Tokenizers;

namespace Rag.NET.Onnx.Bert;

/// <summary>
/// The BERT/WordPiece tokenizer construction and input normalization shared by every ONNX model in
/// Rag.NET that eats BERT token ids — the encoders in <c>Rag.NET.Embeddings.Onnx</c> and the
/// cross-encoder in <c>Rag.NET.Reranking.Onnx</c>.
/// <para>
/// This file lives in <c>src/Shared</c> and is <b>linked</b> into each of those projects rather
/// than published as a library, the same arrangement <c>GraphErrorMapping</c> uses. Each is its own
/// assembly and NuGet package, and the only package they shed in common —
/// <c>Rag.NET.Abstractions</c> — deliberately references neither Microsoft.ML.Tokenizers nor ONNX
/// Runtime, so it cannot name <see cref="BertTokenizer"/>. The alternative, a project reference
/// from the reranking package to the embedding package, would make every consumer of a
/// cross-encoder drag in a dense embedder, three ONNX encoders and their model-loading plumbing to
/// get a tokenizer. Linking keeps one definition and adds a package dependency on nothing but
/// Microsoft.ML.Tokenizers, which is the library actually being used.
/// </para>
/// </summary>
internal static class BertWordPieceTokenization
{
    /// <summary>
    /// The characters BERT's reference implementation treats as whitespace but
    /// <see cref="BertTokenizer"/>'s normalizer deletes as control characters.
    /// </summary>
    private static readonly SearchValues<char> DeletedWhitespace = SearchValues.Create("\n\r\t");

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
    /// Tokenizes <paramref name="text"/> into content tokens (no special tokens), after
    /// substituting a space for the whitespace the normalizer would otherwise delete.
    /// <paramref name="encodedText"/> is the string the tokenizer actually saw — the same string
    /// every offset in the result indexes.
    /// </summary>
    /// <remarks>
    /// Every model in Rag.NET goes through here rather than calling
    /// <see cref="BertTokenizer.EncodeToTokens(string, out string?, bool, bool)"/> directly,
    /// because deleting a newline merges the words either side of it into one the document never
    /// contained (<c>"alpha\n\nbeta gamma"</c> tokenized as <c>alphabet | ##a | gamma</c>). That
    /// corrupts the token stream, so it is not specific to the one encoder that also reads
    /// offsets: encoders that discard the offsets never saw an error, but embedded the merged word
    /// all the same.
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
    /// <c>ITokenEmbeddingGenerator</c> promises. Trimming, collapsing runs of whitespace or
    /// normalizing any other character would all change the length and reintroduce exactly the
    /// defect this exists to fix.
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
}
