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
}
