namespace Rag.NET.Reranking.Onnx;

/// <summary>
/// Turns one query/passage pair into the three tensors a cross-encoder ONNX model is fed.
/// </summary>
internal interface IPairTokenizer
{
    /// <summary>
    /// Encodes the pair into ids, attention mask and segment ids, truncated to
    /// <paramref name="maxLength"/> positions in total.
    /// </summary>
    (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(
        string query, string passage, int maxLength);
}
