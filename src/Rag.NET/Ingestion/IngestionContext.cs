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
#pragma warning restore MA0016

    // ── Counter delegate — facade provides this so StorageBehavior
    //    assigns unique BM25 doc IDs across concurrent ingest calls ─────────
    public required Func<int> GetNextBm25DocId     { get; init; }

    // ── Extension bag — custom behaviors store/read state here ───────────
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
