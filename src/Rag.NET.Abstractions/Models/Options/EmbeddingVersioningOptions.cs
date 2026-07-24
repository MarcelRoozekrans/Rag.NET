namespace Rag.NET.Models.Options;

/// <summary>Options for embedding version stamping and stale re-indexing.</summary>
public sealed class EmbeddingVersioningOptions
{
    /// <summary>
    /// Explicit embedding model identity override. Takes precedence over the identity
    /// derived from the generator's <c>EmbeddingGeneratorMetadata</c>. Set this for
    /// adapters that expose no metadata — without either source, versioning is disabled
    /// (never guessed).
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>Path to the SQLite database file. Created if it does not exist.</summary>
    public string DatabasePath { get; set; } = "rag-embedding-versions.db";
}
