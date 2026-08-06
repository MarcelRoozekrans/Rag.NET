using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// The minimal contract a vector database backend must implement to store and search embedded
/// chunks. Implementations that also support sparse/keyword search additionally implement
/// <see cref="IHybridSearchable"/>; the retrieval pipeline detects that capability rather than
/// requiring it here.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Upserts chunks and their embeddings. Implementations that key records by
    /// <c>(DocumentId, ChunkIndex)</c> replace an existing record on re-store rather than
    /// duplicating it; whether re-storing a document also removes chunks it no longer has (a
    /// shrinking document) is implementation-specific.
    /// </summary>
    Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the chunks nearest <paramref name="queryEmbedding"/>, most similar first, subject
    /// to <paramref name="options"/>'s <c>TopK</c>, <c>MinScore</c>, and <c>MetadataFilter</c>.
    /// A round trip to the store; does not itself call an embedding model.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every chunk belonging to the given document. A no-op, not an error, when the
    /// document has no chunks in the store.
    /// </summary>
    Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default);
}
