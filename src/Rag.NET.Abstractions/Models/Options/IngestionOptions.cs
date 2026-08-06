using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>Per-call tuning for <c>IIngestor.IngestAsync</c>.</summary>
[Validate]
public sealed class IngestionOptions
{
    /// <summary>
    /// When <see langword="true"/>, purges this document's existing vectors, BM25 entries, and
    /// sidecar record up front, before the new content is parsed or chunked — so a document whose
    /// new content fails to parse still ends up with nothing stale left behind. Does not purge
    /// parent chunks (see the parent-document chunking strategy), which are upserted rather than
    /// duplicated regardless of this flag. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Maximum number of documents to ingest concurrently when using
    /// <c>IngestFromProviderAsync</c>. Default is 1 (sequential); -1 means unbounded
    /// (the <see cref="System.Threading.Tasks.ParallelOptions"/> convention).
    /// 0 and values below -1 are rejected. Deliberately not attribute-validated:
    /// -1 is legitimate, so <c>IngestFromProviderAsync</c> validates it manually.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>Chunks per embedding batch within a single document. Default 100.</summary>
    [GreaterThan(0)] public int EmbedBatchSize { get; init; } = 100;

    /// <summary>Maximum embedding batches in flight concurrently per document. Default 2.</summary>
    [GreaterThan(0)] public int MaxConcurrentEmbeddingBatches { get; init; } = 2;
}
