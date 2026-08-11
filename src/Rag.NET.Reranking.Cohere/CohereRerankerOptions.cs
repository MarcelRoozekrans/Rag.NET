namespace Rag.NET.Reranking.Cohere;

/// <summary>
/// Configuration options for <see cref="CohereReranker"/>.
/// </summary>
public sealed class CohereRerankerOptions
{
    /// <summary>
    /// Cohere API key. Required.
    /// </summary>
    public required string ApiKey { get; set; }

    /// <summary>
    /// Reranking model. Default: <c>rerank-english-v3.0</c> (English-only, fast).
    /// Switch to <c>rerank-v3.5</c> for multilingual workloads.
    /// </summary>
    public string Model { get; set; } = "rerank-english-v3.0";

    /// <summary>
    /// Caps how many reranked results this reranker returns. <see langword="null"/> — the default —
    /// returns every candidate it was given, ranked, and lets the caller decide how many to keep.
    /// <para>
    /// <b>It used to default to 5, which silently truncated the pipeline's own request.</b>
    /// <c>RerankingBehavior</c> fetches <c>TopK * 3</c> candidates and cuts the reranked list to
    /// <c>TopK</c>; with this capped at 5, a caller asking for <c>TopK = 20</c> received 5 chunks
    /// and the behaviour's <c>Take(20)</c> did nothing. Nothing was logged, and the ONNX reranker
    /// returns all candidates — so swapping rerankers changed how many chunks an answer was built
    /// from, with no configuration change (issue #94).
    /// </para>
    /// <para>
    /// Set it only to bound Cohere's response size deliberately. A value below the caller's
    /// <c>TopK</c> still truncates — that is what it is for — but it is now an explicit choice
    /// rather than a default.
    /// </para>
    /// </summary>
    public int? TopN { get; set; }

    /// <summary>
    /// Whether to ask Cohere to echo back document text in the response. Default: <see langword="false"/>.
    /// </summary>
    public bool ReturnDocuments { get; set; }

    /// <summary>
    /// Maximum documents per API call. Cohere's hard limit is 1,000. Default: 1000.
    /// When the document list exceeds this, calls are batched sequentially and merged.
    /// </summary>
    public int MaxDocumentsPerBatch { get; set; } = 1000;

    /// <summary>
    /// Optional API endpoint override. Useful for testing with a local stub server.
    /// When <see langword="null"/>, the Cohere SDK uses its default endpoint.
    /// </summary>
    public string? Endpoint { get; set; }
}
