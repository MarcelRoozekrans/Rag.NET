using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What <see cref="BeirHarness.RetrieveScoredRunsAsync"/> hands the comparison-control row: the
/// scored rankings destined for the run file, together with the timings measured <b>while
/// producing them</b> — in-process, by the same code that retrieved, never by a stopwatch around
/// a process. Raw samples only, the shape <see cref="EntrantTimings"/> carries; percentiles are
/// computed once, by <see cref="LatencyStatistics"/>, for every entrant.
/// </summary>
/// <param name="Runs">Rankings by query id, best first — the shape <see cref="TrecRunFile.Write"/> takes.</param>
/// <param name="IndexingSeconds">
/// Wall-clock seconds around embed-through-cache and store only — index construction with
/// embedding already paid for when the cache was warm, which is what the sidecar's cache counts
/// exist to say.
/// </param>
/// <param name="QueryLatenciesMilliseconds">
/// One raw latency per judged query, keyed by query id, spanning the row's retrieval only —
/// pooling and the self-exclusion are harness protocol and stay outside every span.
/// </param>
/// <param name="UnitCount">How many units were indexed — the run's denominator for the indexing time.</param>
/// <param name="MaxUnitsPerDocument">
/// The largest number of units any one document produced — the factor retrieval depth was
/// multiplied by, without which a latency difference that is purely a depth difference reads as a
/// speed difference.
/// </param>
public sealed record TimedScoredRuns(
    IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> Runs,
    double IndexingSeconds,
    IReadOnlyDictionary<string, double> QueryLatenciesMilliseconds,
    int UnitCount,
    int MaxUnitsPerDocument);
