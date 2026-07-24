namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// Options for <see cref="OnnxTokenEmbeddingGenerator"/>: a local ONNX embedding model that
/// exposes token-level hidden states (e.g. a jina-embeddings-v2-style export) plus its
/// BERT/WordPiece vocabulary.
/// </summary>
public sealed class OnnxTokenEmbeddingOptions
{
    /// <summary>
    /// Path to the ONNX embedding model file. The model must accept <c>input_ids</c>;
    /// <c>attention_mask</c> and <c>token_type_ids</c> are supplied only when the model
    /// declares them (exports without them work). It must output token-level hidden states as
    /// <c>[1, sequence, dimension]</c> — see <see cref="OutputName"/>.
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

    /// <summary>
    /// Name of the model output holding the token-level hidden states
    /// <c>[1, sequence, dimension]</c>. When the model does not declare an output with this
    /// name but has exactly one output, that single output is used; otherwise construction
    /// fails listing the model's actual outputs. The output shape is validated on every pass —
    /// a pooled <c>[1, dimension]</c> export is rejected with a clear error instead of
    /// producing garbage embeddings.
    /// </summary>
    public string OutputName { get; set; } = "last_hidden_state";
}
