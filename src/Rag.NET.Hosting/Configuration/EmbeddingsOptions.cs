namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Endpoint, key, model, and vector width for the OpenAI-compatible embedding generator.
/// </summary>
public sealed class EmbeddingsOptions
{
    /// <summary>The OpenAI-compatible base URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The API key; see <see cref="ChatClientOptions.ApiKey"/>'s remarks.</summary>
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
    /// 768). Refusing an absent or non-positive value at startup is follow-on work in this same
    /// phase; today an unset or wrong value surfaces as a store failure at first ingest.
    /// </summary>
    public int VectorDimensions { get; set; }
}
