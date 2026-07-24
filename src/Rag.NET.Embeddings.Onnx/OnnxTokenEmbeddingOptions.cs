namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// Options for <see cref="OnnxTokenEmbeddingGenerator"/>: a local ONNX embedding model that
/// exposes token-level hidden states (e.g. a jina-embeddings-v2-style export) plus its
/// BERT/WordPiece vocabulary.
/// </summary>
public sealed class OnnxTokenEmbeddingOptions
{
    /// <summary>
    /// Path to the ONNX embedding model file. The model must accept <c>input_ids</c>,
    /// <c>attention_mask</c> and <c>token_type_ids</c> and output the last hidden state as
    /// <c>[1, sequence, dimension]</c>.
    /// </summary>
    public required string ModelPath { get; set; }

    /// <summary>
    /// Path to the BERT/WordPiece vocabulary file (vocab.txt). Each line is a token; the line
    /// index is the token ID — the same format <c>Rag.NET.Reranking.Onnx</c> uses.
    /// </summary>
    public required string TokenizerVocabPath { get; set; }

    /// <summary>
    /// Maximum tokens per model pass (the model's sequence limit, INCLUDING the two [CLS]/[SEP]
    /// positions each pass adds). Inputs with more tokens are windowed internally and stitched
    /// back together — this is a per-pass size, not an input limit.
    /// </summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>
    /// Token overlap between consecutive internal windows when an input exceeds
    /// <see cref="MaxTokens"/>, so tokens near window edges keep some bidirectional context.
    /// Must be non-negative and smaller than <see cref="MaxTokens"/>.
    /// </summary>
    public int WindowOverlapTokens { get; set; } = 64;
}
