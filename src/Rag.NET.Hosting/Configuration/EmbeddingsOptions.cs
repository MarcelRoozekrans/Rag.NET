namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Endpoint, key, model, and vector width for the OpenAI-compatible embedding generator.
/// </summary>
public sealed class EmbeddingsOptions
{
    /// <summary>The OpenAI-compatible base URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The API key; see <see cref="ChatClientOptions.ApiKey"/>'s remarks — the same loopback rule
    /// applies here, against this class's own <see cref="Endpoint"/>.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The embedding model id, e.g. <c>text-embedding-3-small</c> or <c>nomic-embed-text</c>.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The dense vector width the configured model actually produces — every vector store must
    /// agree with it. Left at 0 (unset) rather than a plausible-looking default such as 1536, so
    /// an omitted value stays visibly wrong instead of silently matching one model
    /// (<c>text-embedding-3-small</c>) while mismatching another (<c>nomic-embed-text</c>'s
    /// 768). <c>AddRagNetPipelineFromConfiguration</c> refuses an absent or non-positive value at
    /// startup; it cannot refuse a wrong-but-positive one — that is only knowable by embedding
    /// something, which startup must not do, so a genuine mismatch still surfaces as a vector-store
    /// failure at first ingest.
    /// </summary>
    public int VectorDimensions { get; set; }
}
