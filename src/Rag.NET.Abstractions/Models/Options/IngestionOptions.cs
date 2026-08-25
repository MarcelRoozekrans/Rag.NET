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

    /// <summary>
    /// When <see langword="true"/>, <c>IngestFromProviderAsync</c> stops as soon as one entry
    /// fails, instead of attempting every remaining entry. Defaults to <see langword="false"/>,
    /// which is the behaviour it has always had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the case #355 reports — a missing index, so every entry throws for the same reason —
    /// this turns a run that does thousands of doomed round trips into one that stops at the first.
    /// It is off by default because the opposite case is just as real: one malformed document in a
    /// crawl of five thousand should not abandon the other four thousand nine hundred and
    /// ninety-nine.
    /// </para>
    /// <para>
    /// <b>It suppresses <see cref="Rag.NET.Models.CleanupMode.Full"/> cleanup for that run.</b>
    /// Cleanup deletes documents the provider no longer lists, which it decides by comparing what
    /// the store knows against what this run saw. A run that stopped early did not see the rest of
    /// the provider's entries, so cleanup would read them as disappeared and delete them. The
    /// result reports that suppression as an error rather than performing it silently.
    /// </para>
    /// <para>
    /// Entries already in flight when the failure is seen still finish, so with
    /// <see cref="MaxDegreeOfParallelism"/> above 1 the result can carry more than one failure.
    /// "Stop on first error" bounds the work started, not the work already running.
    /// </para>
    /// </remarks>
    public bool StopOnFirstError { get; init; }

    /// <summary>Chunks per embedding batch within a single document. Default 100.</summary>
    [GreaterThan(0)] public int EmbedBatchSize { get; init; } = 100;

    /// <summary>Maximum embedding batches in flight concurrently per document. Default 2.</summary>
    [GreaterThan(0)] public int MaxConcurrentEmbeddingBatches { get; init; } = 2;
}
