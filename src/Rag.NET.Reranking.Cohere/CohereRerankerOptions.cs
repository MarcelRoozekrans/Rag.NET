namespace Rag.NET.Reranking.Cohere;

/// <summary>
/// Configuration options for <see cref="CohereReranker"/>.
/// </summary>
public sealed class CohereRerankerOptions
{
    /// <summary>
    /// Cohere API key. Required.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Reranking model. Default: <c>rerank-english-v3.0</c> (English-only, fast).
    /// Switch to <c>rerank-v3.5</c> for multilingual workloads.
    /// </summary>
    public string Model { get; init; } = "rerank-english-v3.0";

    /// <summary>
    /// Number of top results to return. Default: 5.
    /// </summary>
    public int TopN { get; init; } = 5;

    /// <summary>
    /// Whether to ask Cohere to echo back document text in the response. Default: <see langword="false"/>.
    /// </summary>
    public bool ReturnDocuments { get; init; }

    /// <summary>
    /// Maximum documents per API call. Cohere's hard limit is 1,000. Default: 1000.
    /// When <paramref name="results"/> exceeds this, calls are batched sequentially and merged.
    /// </summary>
    public int MaxDocumentsPerBatch { get; init; } = 1000;

    /// <summary>
    /// Optional API endpoint override. Useful for testing with a local stub server.
    /// When <see langword="null"/>, the Cohere SDK uses its default endpoint.
    /// </summary>
    public string? Endpoint { get; init; }
}
