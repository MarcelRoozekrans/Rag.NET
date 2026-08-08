namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Chooses and configures the vector store the pipeline is wired to: <c>InMemory</c> (the
/// default), <c>Qdrant</c>, or <c>PgVector</c> — the bounded set this hosting package supports.
/// Anything else is served by referencing <c>Rag.NET.Mcp</c> or <c>Rag.NET</c> directly and
/// registering your own store.
/// </summary>
public sealed class VectorStoreOptions
{
    /// <summary>
    /// The store kind: <c>InMemory</c>, <c>Qdrant</c>, or <c>PgVector</c>, compared
    /// case-insensitively. Defaults to <c>InMemory</c>, whose data does not survive a restart.
    /// </summary>
    public string Kind { get; set; } = "InMemory";

    /// <summary>Settings for the <c>Qdrant</c> kind; ignored otherwise.</summary>
    public QdrantVectorStoreOptions Qdrant { get; set; } = new();

    /// <summary>Settings for the <c>PgVector</c> kind; ignored otherwise.</summary>
    public PgVectorStoreOptions PgVector { get; set; } = new();
}
