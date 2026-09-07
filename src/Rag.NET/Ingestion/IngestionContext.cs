using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Ingestion;

/// <summary>
/// Mutable per-call context for the ingestion pipeline.
/// Contains only runtime inputs, accumulated state, and an extension bag.
/// Services live on the behaviors, not here.
/// </summary>
public sealed class IngestionContext
{
    // ── Runtime inputs ────────────────────────────────────────────────────
    public required Stream Stream                   { get; init; }
    public required DocumentMetadata Metadata       { get; init; }
    public IngestionOptions? Options                { get; init; }
    public IProgress<IngestionProgress>? Progress  { get; init; }

    // ── Accumulated state (populated by behaviors in order) ───────────────
#pragma warning disable MA0016 // CollectionsMarshal.AsSpan requires concrete List<T>
    public List<DocumentSection> Sections          { get; } = [];
    public List<TextChunk> Chunks                  { get; } = [];
    public List<EmbeddedChunk> EmbeddedChunks      { get; } = [];

    /// <summary>
    /// Sparse (SPLADE) vectors parallel to <see cref="EmbeddedChunks"/> (same index).
    /// Set by <c>SparseEmbeddingBehavior</c> when a sparse generator is registered and the
    /// store is sparse-capable; <see langword="null"/> otherwise. Consumed by
    /// <c>StorageBehavior</c>.
    /// </summary>
    public List<SparseVector>? SparseVectors       { get; set; }
#pragma warning restore MA0016

    // ── Counter delegate — facade provides this so StorageBehavior
    //    assigns unique BM25 doc IDs across concurrent ingest calls ─────────
    public required Func<int> GetNextBm25DocId     { get; init; }

    /// <summary>
    /// Document ids <b>other than</b> <see cref="Metadata"/>'s whose previous append-only entries
    /// must be purged before this ingest's chunks are indexed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because one ingest can write chunks belonging to another document.</b> Under
    /// <c>RaptorTreeScope.Corpus</c> the RAPTOR behaviour appends the whole corpus tree to the
    /// ingesting document's <see cref="EmbeddedChunks"/>, with each summary carrying
    /// <c>raptor://corpus-tree</c> as its document id. <c>StorageBehavior</c> purged only
    /// <see cref="Metadata"/>'s id — never the corpus id — so every rebuild appended another full
    /// copy of the tree's postings to the BM25 index and the term statistics grew without bound
    /// (issue #336). The vector store was spared because it upserts on
    /// <c>(DocumentId, ChunkIndex)</c>; BM25 appends.
    /// </para>
    /// <para>
    /// <b>A behaviour adds an id here only when it is about to overwrite that document's chunks
    /// wholesale.</b> Purging an id whose chunks this ingest does not then re-add would delete
    /// another document's postings and put nothing back.
    /// </para>
    /// </remarks>
    public ISet<string> AdditionalAppendOnlyPurgeIds { get; } = new HashSet<string>(StringComparer.Ordinal);

    // ── Extension bag — custom behaviors store/read state here ───────────
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
