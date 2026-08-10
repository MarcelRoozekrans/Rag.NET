namespace Rag.NET.Models;

/// <summary>
/// A single piece of chunked document text, produced by an <c>IChunkingStrategy</c> and, once
/// embedded, wrapped in an <see cref="EmbeddedChunk"/> for storage.
/// </summary>
public sealed record TextChunk
{
    /// <summary>The chunk's text content, as produced by the chunking strategy.</summary>
    public required string Text { get; init; }

    /// <summary>The document this chunk was produced from.</summary>
    public required DocumentId DocumentId { get; init; }

    /// <summary>
    /// This chunk's 0-based position among the chunks produced for its document. Combined with
    /// <see cref="DocumentId"/> to derive a stable per-chunk identity for storage (see
    /// <c>DeterministicChunkId</c>) and parent-chunk lookups, so it must be unique within a
    /// document.
    /// </summary>
    public required int ChunkIndex { get; init; }

    /// <summary>
    /// Character offset of this chunk's start within the source <c>DocumentSection.Text</c> it was
    /// chunked from — not an offset into the whole document. Sections are chunked independently,
    /// so a chunk from a later section can have a smaller <see cref="StartPosition"/> than one
    /// from an earlier section. The exact span a strategy records can differ from the chunk's own
    /// <see cref="Text"/>: proposition chunking, for instance, records the source passage the
    /// proposition was extracted from rather than the (rewritten) proposition text.
    /// </summary>
    public int StartPosition { get; init; }

    /// <summary>
    /// Character offset of this chunk's end within the source <c>DocumentSection.Text</c>,
    /// exclusive — the same section-relative, not document-relative, coordinate space as
    /// <see cref="StartPosition"/>.
    /// </summary>
    public int EndPosition { get; init; }

    /// <summary>
    /// Optional precomputed embedding (e.g. from late chunking). When set,
    /// <c>EmbeddingBehavior</c> uses it verbatim instead of re-embedding the chunk text.
    /// </summary>
    /// <remarks>
    /// Assign <see langword="null"/> explicitly when no embedding is available. Beware the
    /// implicit <c>float[]</c> conversion: a <see langword="null"/> array converts to an
    /// empty, non-null <see cref="ReadOnlyMemory{T}"/>. Empty embeddings are therefore
    /// treated as absent and the chunk is embedded normally.
    /// </remarks>
    public ReadOnlyMemory<float>? Embedding { get; init; }

    /// <summary>
    /// Per-chunk tags: connector-supplied tags merged with framework-written keys (document id,
    /// file name, timestamps, and so on — see <see cref="ReservedMetadataKeys"/>) and, when
    /// per-section chunking is used, ancestor-heading breadcrumbs. Used for
    /// <c>RetrievalOptions.MetadataFilter</c> matching, RBAC/trust retrieval guards, and
    /// time-weighted re-scoring. Never <see langword="null"/>; absent metadata is an empty
    /// dictionary, not a missing one.
    /// </summary>
    /// <remarks>
    /// Values carry their type (<see cref="MetadataValue.Kind"/>), and every store persists and
    /// filters on that type — <c>Metadata["page"] = 3</c> is a number a store can compare
    /// numerically, not the string <c>"3"</c>. Writing plain strings/numbers/bools/dates works
    /// unchanged via <see cref="MetadataValue"/>'s implicit conversions.
    /// </remarks>
    public IDictionary<string, MetadataValue> Metadata { get; init; } = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
}
