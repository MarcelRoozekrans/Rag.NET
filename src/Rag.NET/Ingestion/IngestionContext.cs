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
    public List<DocumentSection> Sections          { get; } = [];
    public List<TextChunk> Chunks                  { get; } = [];
    public List<EmbeddedChunk> EmbeddedChunks      { get; } = [];

    // ── Counter delegate — facade provides this so StorageBehavior
    //    assigns unique BM25 doc IDs across concurrent ingest calls ─────────
    public required Func<int> GetNextBm25DocId     { get; init; }

    // ── Extension bag — custom behaviors store/read state here ───────────
    public Dictionary<string, object?> Extensions  { get; } = new();
}
