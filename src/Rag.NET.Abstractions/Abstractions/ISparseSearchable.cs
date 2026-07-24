using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability interface for <see cref="IVectorStore"/> implementations that can store
/// and search learned sparse vectors (e.g. SPLADE) alongside dense embeddings — the same
/// pattern as <see cref="IHybridSearchable"/>. Callers discover the capability with an
/// <c>is ISparseSearchable</c> check on the registered store and degrade to dense-only
/// behavior when it is absent.
/// </summary>
public interface ISparseSearchable
{
    /// <summary>
    /// Stores sparse vectors for the given chunks. Implementations key by
    /// <c>(DocumentId, ChunkIndex)</c>: re-storing the same chunk replaces its sparse vector
    /// rather than duplicating it. Chunks with an empty sparse vector are skipped.
    /// </summary>
    Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches by sparse-vector similarity (dot product of matching term weights), honoring
    /// <see cref="SearchOptions.TopK"/>, <see cref="SearchOptions.MinScore"/>, and
    /// <see cref="SearchOptions.MetadataFilter"/>.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default);
}
