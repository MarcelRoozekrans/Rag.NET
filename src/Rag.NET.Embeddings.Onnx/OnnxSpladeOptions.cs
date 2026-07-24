namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// Options for <see cref="OnnxSpladeEncoder"/>: a local SPLADE-style ONNX model (BERT encoder
/// with MLM head, e.g. an ONNX export of <c>naver/splade-cocondenser-ensembledistil</c>) plus
/// its BERT/WordPiece vocabulary.
/// </summary>
public sealed class OnnxSpladeOptions
{
    /// <summary>
    /// Path to the SPLADE ONNX model file. The model must accept <c>input_ids</c>;
    /// <c>attention_mask</c> and <c>token_type_ids</c> are supplied only when the model
    /// declares them. It must output MLM logits as <c>[1, sequence, vocabulary]</c> — see
    /// <see cref="OutputName"/>.
    /// </summary>
    public required string ModelPath { get; set; }

    /// <summary>
    /// Path to the BERT/WordPiece vocabulary file (vocab.txt). Each line is a token; the line
    /// index is the token ID — the same format <see cref="OnnxTokenEmbeddingOptions"/> uses.
    /// </summary>
    public required string TokenizerVocabPath { get; set; }

    /// <summary>
    /// Maximum tokens per model pass (the model's sequence limit, INCLUDING the two
    /// [CLS]/[SEP] positions each pass adds). SPLADE models are short-context: 512 by
    /// default. Longer inputs are windowed internally and merged by element-wise max.
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// Maximum number of non-zero terms kept in the produced <c>SparseVector</c>: after
    /// pooling, only the <see cref="TopTerms"/> largest weights survive. Bounds both vector
    /// size and index fan-out. Must be at least 1.
    /// </summary>
    public int TopTerms { get; set; } = 256;

    /// <summary>
    /// Name of the model output holding the MLM logits <c>[1, sequence, vocabulary]</c>.
    /// When the model does not declare an output with this name but has exactly one output,
    /// that single output is used; otherwise construction fails listing the model's actual
    /// outputs. The shape is validated on every pass.
    /// </summary>
    public string OutputName { get; set; } = "logits";
}
