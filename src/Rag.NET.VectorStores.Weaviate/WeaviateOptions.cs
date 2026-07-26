namespace Rag.NET.Weaviate;

/// <summary>Options for <see cref="WeaviateVectorStore"/>, validated eagerly in <c>UseWeaviate</c>.</summary>
public sealed class WeaviateOptions
{
    /// <summary>Weaviate HTTP endpoint, e.g. <c>http://localhost:8080</c>. Required.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Weaviate class holding the chunks. Required; must be a valid Weaviate class name:
    /// a capital letter followed by letters, digits, or underscores (the name doubles as a
    /// GraphQL field, so GraphQL naming rules apply).
    /// </summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>
    /// Dense embedding dimensions. Weaviate itself infers dimensions from the first stored
    /// vector (<c>vectorizer: none</c>), so this is validated for sanity but not sent.
    /// </summary>
    public int VectorDimensions { get; set; } = 1536;

    /// <summary>
    /// Optional API key, sent as <c>Authorization: Bearer</c>. Leave null for anonymous
    /// access (the local Docker default).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional tenant. When set, the class is created with multi-tenancy enabled, the
    /// tenant is created on initialization, and every read/write carries it.
    /// </summary>
    public string? Tenant { get; set; }
}
