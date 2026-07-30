namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// Options for <see cref="OnnxEmbeddingGenerator"/>: a local ONNX sentence-embedding model
/// (e.g. an <c>all-MiniLM-L6-v2</c> export) plus its BERT/WordPiece vocabulary.
/// </summary>
public sealed class OnnxEmbeddingOptions
{
    /// <summary>
    /// Path to the ONNX embedding model file. The model must accept <c>input_ids</c>;
    /// <c>attention_mask</c> and <c>token_type_ids</c> are supplied only when the model declares
    /// them. It must output TOKEN-LEVEL hidden states as <c>[batch, sequence, dimension]</c> —
    /// see <see cref="OutputName"/>. A pooled export is rejected, because this generator's whole
    /// contract is that pooling excludes padding, and a pooled output's pooling cannot be verified.
    /// </summary>
    public required string ModelPath { get; set; }

    /// <summary>
    /// Path to the BERT/WordPiece vocabulary file (vocab.txt). Each line is a token; the line
    /// index is the token ID — the same format <see cref="OnnxTokenEmbeddingOptions"/> uses.
    /// </summary>
    public required string TokenizerVocabPath { get; set; }

    /// <summary>
    /// Maximum tokens per sequence, INCLUDING the two [CLS]/[SEP] positions. Text beyond the
    /// budget is TRUNCATED — unlike <see cref="OnnxTokenEmbeddingOptions.MaxTokens"/>, which
    /// windows and stitches, because a single pooled vector per text is the contract here and
    /// stitching windows into one vector is a different (and unvalidated) quantity.
    /// </summary>
    /// <remarks>
    /// The default matches <c>all-MiniLM-L6-v2</c>'s <c>max_seq_length: 256</c>, which is the
    /// model the published BEIR figures this generator is measured against were produced with.
    /// Raise it for models with a longer limit (BERT-family exports usually accept 512); a value
    /// ABOVE the model's positional limit fails inside ONNX Runtime rather than here.
    /// </remarks>
    public int MaxTokens { get; set; } = 256;

    /// <summary>
    /// Maximum texts per model pass. Sequences in a pass are padded to the longest one and the
    /// padding is excluded from pooling, so batching cannot change a text's vector — see
    /// <see cref="OnnxEmbeddingGenerator"/>. Forced to 1 when the model does not declare
    /// <c>attention_mask</c>, since without it the model itself would attend to the padding.
    /// </summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>
    /// Name of the model output holding the token-level hidden states
    /// <c>[batch, sequence, dimension]</c>. When the model does not declare an output with this
    /// name but has exactly one output, that single output is used; otherwise construction fails
    /// listing the model's actual outputs.
    /// </summary>
    public string OutputName { get; set; } = "last_hidden_state";

    /// <summary>
    /// Model identity reported through <c>EmbeddingGeneratorMetadata.DefaultModelId</c>, which is
    /// what the ingestion path stamps on stored vectors so re-indexing can detect a model change.
    /// When null or empty, the identity is derived from <see cref="ModelPath"/>'s file name
    /// without its extension.
    /// </summary>
    /// <remarks>
    /// SET THIS when the file is called something generic like <c>model.onnx</c>: the derived
    /// identity would then be <c>onnx/model</c> for every model, so swapping models would not
    /// register as a change and stale vectors would never be re-indexed.
    /// </remarks>
    public string? ModelId { get; set; }
}
