using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

[Validate]
public sealed class IngestionOptions
{
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
