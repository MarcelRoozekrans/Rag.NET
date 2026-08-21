namespace Rag.NET.Raptor.Store;

/// <summary>
/// Stores leaf chunks with their embedding vectors so that RAPTOR can cluster over the whole
/// corpus rather than one document at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the vector store cannot do this.</b> <c>IVectorStore</c> is <c>StoreAsync</c>,
/// <c>SearchAsync</c> and <c>DeleteByDocumentIdAsync</c> — nothing enumerates. <c>IChunkLookup</c>
/// is by key, so a caller would already need every identity, and it returns <c>TextChunk</c>
/// without the embedding that clustering actually runs on. It is also implemented by only two
/// stores (#318).
/// </para>
/// <para>
/// Written only when <c>RaptorOptions.TreeScope</c> is <c>Corpus</c>. Under <c>PerDocument</c> the
/// behaviour already holds every chunk it needs in the ingestion context, so nothing is stored and
/// nothing is paid for.
/// </para>
/// </remarks>
public interface IRaptorLeafStore : IAsyncDisposable
{
    /// <summary>Creates or migrates any backing storage the store needs.</summary>
    /// <param name="cancellationToken">Cancels the initialisation.</param>
    /// <returns>A task that completes when the store is ready to use.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds leaves, upserting on <c>(DocumentId, ChunkIndex)</c> — re-ingesting a document
    /// replaces its rows rather than duplicating them.
    /// </summary>
    /// <param name="leaves">The leaves to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the leaves are durable.</returns>
    Task AddLeavesAsync(IReadOnlyList<RaptorLeaf> leaves, CancellationToken cancellationToken = default);

    /// <summary>Returns every stored leaf. This is the corpus that clustering runs over.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every leaf, in unspecified order.</returns>
    Task<IReadOnlyList<RaptorLeaf>> GetAllLeavesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns how many leaves are stored, without loading them.</summary>
    /// <remarks>
    /// Exists so the growth debounce can decide whether to rebuild without paying for a full load.
    /// <c>CommunityDetectionBehavior</c> records the absence of exactly this on <c>IGraphStore</c>
    /// as a cost it chose not to remove; there is no reason to repeat that here.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of stored leaves.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every leaf stored for a document.</summary>
    /// <param name="documentId">The document whose leaves are removed.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when the rows are gone.</returns>
    Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}
