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
    /// An empty or unset value also resolves to <c>InMemory</c>; any other value that does not
    /// match one of the three exactly is rejected by
    /// <c>AddRagNetPipelineFromConfiguration</c> at startup rather than silently falling back to
    /// <c>InMemory</c> — <c>InMemory</c> is reached only by asking for it.
    /// </summary>
    public string Kind { get; set; } = "InMemory";

    /// <summary>Settings for the <c>Qdrant</c> kind; ignored otherwise.</summary>
    public QdrantVectorStoreOptions Qdrant { get; set; } = new();

    /// <summary>Settings for the <c>PgVector</c> kind; ignored otherwise.</summary>
    public PgVectorStoreOptions PgVector { get; set; } = new();
}
