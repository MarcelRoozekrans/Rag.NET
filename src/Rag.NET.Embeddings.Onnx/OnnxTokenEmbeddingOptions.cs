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
    /// <remarks>
    /// <para>
    /// <b>256 to match <see cref="OnnxEmbeddingOptions.MaxTokens"/></b>, which is the limit the
    /// pooled generator in this package already applies to the same model. The two read the same
    /// model file, so a per-pass limit larger than that one is a limit the model does not have.
    /// </para>
    /// <para>
    /// <b>It defaulted to 8192 until 2026-09-03, and that was a defect rather than a generous
    /// ceiling.</b> This value is what decides whether the windowing this summary promises happens
    /// at all: at 8192 it never did, and <c>all-MiniLM-L6-v2</c> was handed sequences it cannot
    /// embed, throwing at the position-embedding node. Because <c>LateChunkingStrategy</c> catches
    /// generator failures and falls back to unembedded chunks, and <c>EmbeddingBehavior</c> then
    /// backfills those with ordinary embeddings, the visible result was not an error — it was late
    /// chunking silently not applying to any document long enough to need it. Measured on SciFact
    /// before the fix: 1,401 of 9,506 units carried no late-chunked embedding.
    /// </para>
    /// <para>
    /// <b>Raise it deliberately for a longer-context model</b>, and lower
    /// <see cref="WindowOverlapTokens"/> with it if the windows get small. A value above the
    /// model's real limit does not fail loudly here; it fails inside ONNX Runtime, and callers who
    /// swallow that failure see ordinary embeddings rather than an exception.
    /// </para>
    /// </remarks>
    public int MaxTokens { get; set; } = 256;

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
