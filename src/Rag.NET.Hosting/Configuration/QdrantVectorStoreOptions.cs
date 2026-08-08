namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Settings for <c>UseQdrant</c>, bound from <c>RagNet:VectorStore:Qdrant</c>. Mirrors
/// <c>QdrantBuilderExtensions.UseQdrant</c>'s parameters exactly: Qdrant takes a host, a port,
/// and a collection name, not a connection string.
/// </summary>
public sealed class QdrantVectorStoreOptions
{
    /// <summary>The Qdrant host.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The Qdrant gRPC port. Defaults to Qdrant's standard gRPC port, 6334.</summary>
    public int Port { get; set; } = 6334;

    /// <summary>The collection to store chunks in.</summary>
    public string CollectionName { get; set; } = string.Empty;
}
