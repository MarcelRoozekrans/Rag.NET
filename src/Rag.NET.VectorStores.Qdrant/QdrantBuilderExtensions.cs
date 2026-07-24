using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Qdrant;

public static class QdrantBuilderExtensions
{
    /// <param name="builder">The RAG builder.</param>
    /// <param name="host">Qdrant host.</param>
    /// <param name="port">Qdrant gRPC port.</param>
    /// <param name="collectionName">Collection to store chunks in.</param>
    /// <param name="vectorDimensions">Dense embedding dimensions.</param>
    /// <param name="enableSparseVectors">
    /// When <see langword="true"/>, registers <see cref="QdrantSparseVectorStore"/> instead:
    /// the collection is created with a named sparse vector ("splade") and the store serves
    /// <c>ISparseSearchable</c> — pair with <c>UseSpladeEncoder</c> for SPLADE hybrid
    /// retrieval. Point ids become deterministic per <c>(DocumentId, ChunkIndex)</c>.
    /// <c>InitializeAsync</c> fails fast on an existing collection created without sparse
    /// support — delete the collection and re-ingest to enable it.
    /// </param>
    public static TBuilder UseQdrant<TBuilder>(
        this TBuilder builder,
        string host,
        int port,
        string collectionName,
        int vectorDimensions = 1536,
        bool enableSparseVectors = false)
        where TBuilder : IRagBuilder
    {
        QdrantVectorStore store = enableSparseVectors
            ? new QdrantSparseVectorStore(host, port, collectionName, vectorDimensions)
            : new QdrantVectorStore(host, port, collectionName, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
