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
    /// Prepares the backend for use — creating the index or collection with the schema this store
    /// needs, if it is not already there. <b>Idempotent and cheap once already initialised</b>, so
    /// callers may invoke it defensively rather than tracking initialisation state themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other storage abstraction in the library already declares this member —
    /// <see cref="IBm25Index"/>, <see cref="ICostLedger"/>, <see cref="IEmbeddingVersionStore"/>,
    /// <see cref="IParentChunkStore"/> and <see cref="IRagDataManager"/> — with the same signature
    /// and the same idempotence contract. <see cref="IVectorStore"/> was the one that did not, so
    /// the method existed on the concrete stores where no interface could reach it (#353).
    /// </para>
    /// <para>
    /// <b>Calling it is optional.</b> Every store in this library initialises itself on first use,
    /// so a caller who never calls this still gets a working first ingest. It remains public for
    /// callers who want the cost paid at a moment of their choosing — provisioning at startup
    /// rather than inside the first request — and for re-creating a collection after
    /// <see cref="ICollectionManageable.DeleteCollectionAsync"/>.
    /// </para>
    /// <para>
    /// <b><see cref="StoreAsync"/> and <see cref="SearchAsync"/> initialise;
    /// <see cref="DeleteByDocumentIdAsync"/> does not.</b> Those two cannot do their job without the
    /// collection, whereas a delete against a collection that does not exist has nothing to delete —
    /// provisioning one to satisfy it would be waste, and on pgvector an inline index build under a
    /// write-blocking lock, set off by a delete.
    /// </para>
    /// <para>
    /// <b>The default implementation does nothing</b>, which is why adding this member is not a
    /// breaking change: an existing external <see cref="IVectorStore"/> that provisions its own
    /// backend keeps behaving exactly as it did. Implementations that create a collection should
    /// override it.
    /// </para>
    /// </remarks>
    Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
