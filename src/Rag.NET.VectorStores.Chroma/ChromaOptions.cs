namespace Rag.NET.Chroma;

/// <summary>Options for <see cref="ChromaVectorStore"/>, validated eagerly in <c>UseChroma</c>.</summary>
public sealed class ChromaOptions
{
    /// <summary>Chroma HTTP endpoint, e.g. <c>http://localhost:8000</c>. Required.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Chroma collection holding the chunks. Required; Chroma names are 3–512 characters
    /// of letters, digits, <c>.</c>, <c>_</c>, or <c>-</c>, starting and ending with a
    /// letter or digit, without consecutive dots, and not an IPv4 address (validation
    /// matches Chroma's documented rules; the server remains authoritative).
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>Chroma tenant. The server default matches the local Docker image.</summary>
    public string Tenant { get; set; } = "default_tenant";

    /// <summary>Chroma database. The server default matches the local Docker image.</summary>
    public string Database { get; set; } = "default_database";

    /// <summary>
    /// Optional static token, sent as <c>Authorization: Bearer</c>. Leave null for
    /// anonymous access (the local Docker default).
    /// </summary>
    public string? ApiKey { get; set; }
}
