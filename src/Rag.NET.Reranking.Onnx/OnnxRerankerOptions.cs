namespace Rag.NET.Reranking.Onnx;

public sealed class OnnxRerankerOptions
{
    /// <summary>
    /// Path to the ONNX cross-encoder model file.
    /// </summary>
    public required string ModelPath { get; set; }

    /// <summary>
    /// Maximum token sequence length for the cross-encoder input.
    /// Query + passage pairs exceeding this are truncated.
    /// </summary>
    public int MaxLength { get; set; } = 512;

    /// <summary>
    /// Path to the BERT vocabulary file (vocab.txt), for BERT-family cross-encoders such as
    /// ms-marco-MiniLM. Each line is a token; the line index is the token ID.
    /// Set exactly one of <see cref="VocabPath"/> and <see cref="TokenizerPath"/>.
    /// </summary>
    public string VocabPath { get; set; } = "";

    /// <summary>
    /// Path to the SentencePiece model (sentencepiece.bpe.model), for XLM-RoBERTa cross-encoders
    /// such as bge-reranker-v2-m3. Those checkpoints ship this file instead of a vocab.txt.
    /// Set exactly one of <see cref="VocabPath"/> and <see cref="TokenizerPath"/>; a null,
    /// empty or whitespace value counts as unset.
    /// </summary>
    public string? TokenizerPath { get; set; }
}
