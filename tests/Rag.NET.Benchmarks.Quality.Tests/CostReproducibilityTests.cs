using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="CostReproducibility"/> — the gate that would have caught the 23x defect: with
/// one embedding-cache disk read per text inside the timed span, identical SciFact/LangChain runs
/// measured 55.2 s cold and 2.4 s hot on OS page-cache state alone, and a single-run figure
/// published either number as "the" cost. The gate refuses a single run outright, hard-fails a
/// spread past the bar naming both values, and — the part that is the real protection — returns
/// the spread even when it passes, so the published figure carries its own run-to-run variation.
/// </summary>
public sealed class CostReproducibilityTests : IDisposable
{
    private readonly string _directory;

    public CostReproducibilityTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ragnet-repro-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string RunPath => Path.Combine(_directory, "entrant.run");

    /// <summary>One repeat run's timings, everything but the measured numbers held constant.</summary>
    private static EntrantTimings Run(
        double indexingSeconds, params (string QueryId, double LatencyMilliseconds)[] latencies)
    {
        var byQuery = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (queryId, latency) in latencies)
        {
            byQuery[queryId] = latency;
        }

        return new EntrantTimings(
            RunTag: "tag",
            IndexingSeconds: indexingSeconds,
            QueryLatenciesMilliseconds: byQuery,
            EmbeddingCacheHits: 7,
            EmbeddingCacheMisses: 0,
            UnitCount: 5183,
            MaxUnitsPerDocument: 3);
    }

    // ----------------------------------------------------------------- passing, spread surfaced

    [Fact]
    public void From_TwoNearIdenticalRuns_PassAndReportTheSpread()
    {
        // The healthy case: back-to-back repeats with the jitter actually observed on this
        // machine (2.1 s / 2.9 s indexing). The spread is returned, not swallowed — min, max and
        // ratio per figure — because the honest published figure is the range, not either number.
        var first = Run(2.1, ("q-1", 10), ("q-2", 20), ("q-3", 30), ("q-4", 40));
        var second = Run(2.9, ("q-1", 12), ("q-2", 22), ("q-3", 33), ("q-4", 44));

        var spread = CostReproducibility.From([first, second]);

        Assert.Equal("tag", spread.RunTag, StringComparer.Ordinal);
        Assert.Equal(2, spread.RunCount);
        Assert.Equal(2.1, spread.IndexingSeconds.Minimum);
        Assert.Equal(2.9, spread.IndexingSeconds.Maximum);
        Assert.Equal(2.9 / 2.1, spread.IndexingSeconds.Ratio);
        // Nearest-rank p50 of four samples is the second: 20 and 22.
        Assert.Equal(20, spread.P50Milliseconds.Minimum);
        Assert.Equal(22, spread.P50Milliseconds.Maximum);
        // p99 of four samples is the fourth — the maximum, as LatencyStatistics pins.
        Assert.Equal(40, spread.P99Milliseconds.Minimum);
        Assert.Equal(44, spread.P99Milliseconds.Maximum);
    }

    [Fact]
    public void From_APureP99Excursion_PassesAndReportsIt()
    {
        // p99 is reported, never gated. At these sample counts it is read off one to three tail
        // samples (the maximum below n = 100), and two healthy LlamaIndex runs on the same corpus
        // measured 1032.7 ms vs 393.7 ms — legitimate tail noise already at the level a
        // defect-catching bar would need. A structural defect inflates every sample and so moves
        // the gated p50 and indexing figures; a p99-only excursion is one slow query.
        var steady = Run(2.4, ("q-1", 10), ("q-2", 10), ("q-3", 10), ("q-4", 394));
        var spiky = Run(2.4, ("q-1", 10), ("q-2", 10), ("q-3", 10), ("q-4", 3200));

        var spread = CostReproducibility.From([steady, spiky]);

        Assert.Equal(394, spread.P99Milliseconds.Minimum);
        Assert.Equal(3200, spread.P99Milliseconds.Maximum);
        Assert.Equal(3200 / 394.0, spread.P99Milliseconds.Ratio);
        // And the gated figures did not move: p50 stayed 10 against 10.
        Assert.Equal(1, spread.P50Milliseconds.Ratio);
    }

    // ------------------------------------------------------------------------- the hard fail

    [Fact]
    public void From_A23xIndexingDisagreement_FailsNamingBothValues()
    {
        // The defect this gate exists for, verbatim: 55.2 s cold page cache, 2.4 s hot, same
        // code, same corpus. The message must name both numbers — the point is a failure a
        // reader can act on, not a boolean.
        var cold = Run(55.2, ("q-1", 10));
        var hot = Run(2.4, ("q-1", 10));

        var ex = Assert.Throws<InvalidDataException>(() => CostReproducibility.From([cold, hot]));

        Assert.Contains("55.2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_A23xP50Disagreement_FailsNamingBothValues()
    {
        // The same defect class landing in the per-query spans instead: a disk read inside the
        // retrieval span moves every sample, so it moves the median.
        var hot = Run(2.4, ("q-1", 4.2));
        var cold = Run(2.4, ("q-1", 96.6));

        var ex = Assert.Throws<InvalidDataException>(() => CostReproducibility.From([hot, cold]));

        Assert.Contains("4.2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("96.6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_AZeroSecondIndexingRunBesideARealOne_Fails()
    {
        // Min = 0 makes the ratio infinite, which fails any finite bar — a span that measured
        // nothing beside a span that measured something is structurally wrong, not fast.
        var empty = Run(0, ("q-1", 10));
        var real = Run(2.4, ("q-1", 10));

        var ex = Assert.Throws<InvalidDataException>(() => CostReproducibility.From([empty, real]));

        Assert.Contains("2.4", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- what cannot be compared

    [Fact]
    public void From_ASingleRun_CannotProduceAFigure()
    {
        // The rule the 23x defect proved: a single run cannot show whether its own number is
        // about the library or about the machine's state, so no figure exists at n = 1.
        var ex = Assert.Throws<ArgumentException>(
            () => CostReproducibility.From([Run(2.4, ("q-1", 10))]));

        Assert.Contains("single run", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_NoRunsAtAll_CannotProduceAFigure()
    {
        _ = Assert.Throws<ArgumentException>(() => CostReproducibility.From([]));
    }

    [Fact]
    public void From_RunsFromDifferentEntrants_CannotBeCompared_NamingBothTags()
    {
        // Comparing one library's repeat against another's is not a reproducibility check, it is
        // the cross-entrant comparison — and passing it off as repeats would launder a
        // disagreement between libraries into a "reproducible" spread.
        var langchain = Run(2.4, ("q-1", 10)) with { RunTag = "langchain-core-1.5.3" };
        var haystack = Run(2.6, ("q-1", 11)) with { RunTag = "haystack-ai-3.0.0" };

        var ex = Assert.Throws<ArgumentException>(
            () => CostReproducibility.From([langchain, haystack]));

        Assert.Contains("langchain-core-1.5.3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("haystack-ai-3.0.0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_RunsOverDifferentQuerySets_CannotBeCompared_NamingADifferingId()
    {
        // Same tag, different judged queries: repeats of the same entrant on different datasets
        // or splits. The query-id set is the dataset's fingerprint in the sidecar, so a mismatch
        // there is the one signal that these are not repeats of the same measurement.
        var scifact = Run(2.4, ("q-1", 10));
        var fiqa = Run(2.6, ("q-9", 11));

        var ex = Assert.Throws<ArgumentException>(() => CostReproducibility.From([scifact, fiqa]));

        Assert.Contains("q-9", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_ANonFiniteIndexingTime_IsRefused()
    {
        // The sidecar reader would never produce one, but hand-built timings can, and a NaN
        // ratio compares false against the bar — it would pass the gate by not comparing.
        var broken = Run(double.NaN, ("q-1", 10));
        var real = Run(2.4, ("q-1", 10));

        _ = Assert.Throws<ArgumentException>(() => CostReproducibility.From([broken, real]));
    }

    // ------------------------------------------------------------------- from the sidecars

    [Fact]
    public void FromSidecars_ReadsEveryRepeatBesideTheRunFile()
    {
        // The disk-facing path: repeats written under the run-index convention, each one put
        // through the full ReadPaired check before any comparison happens.
        WriteRunFile("tag", "q-1", "q-2");
        TimingsSidecar.Write(RunPath, Run(2.1, ("q-1", 10), ("q-2", 20)), runIndex: 1);
        TimingsSidecar.Write(RunPath, Run(2.9, ("q-1", 12), ("q-2", 22)), runIndex: 2);

        var spread = CostReproducibility.FromSidecars(RunPath, runCount: 2);

        Assert.Equal(2, spread.RunCount);
        Assert.Equal(2.1, spread.IndexingSeconds.Minimum);
        Assert.Equal(2.9, spread.IndexingSeconds.Maximum);
    }

    [Fact]
    public void FromSidecars_ASingleRun_CannotProduceAFigure()
    {
        WriteRunFile("tag", "q-1");
        TimingsSidecar.Write(RunPath, Run(2.4, ("q-1", 10)));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => CostReproducibility.FromSidecars(RunPath, runCount: 1));
    }

    [Fact]
    public void FromSidecars_AMissingRepeatRefusesNamingTheIndexedPath()
    {
        // Asking for three repeats when two exist is a failure, not a two-run comparison: the
        // refuse-on-miss rule, inherited from ReadPaired repeat by repeat.
        WriteRunFile("tag", "q-1");
        TimingsSidecar.Write(RunPath, Run(2.1, ("q-1", 10)), runIndex: 1);
        TimingsSidecar.Write(RunPath, Run(2.9, ("q-1", 12)), runIndex: 2);

        var ex = Assert.Throws<FileNotFoundException>(
            () => CostReproducibility.FromSidecars(RunPath, runCount: 3));

        Assert.Contains(RunPath + ".timings.3.json", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A run file tagged <paramref name="runTag"/> ranking one document per query.</summary>
    private void WriteRunFile(string runTag, params string[] queryIds)
    {
        var runs = new Dictionary<string, IReadOnlyList<ScoredDocument>>(StringComparer.Ordinal);
        foreach (var queryId in queryIds)
        {
            runs[queryId] = [new ScoredDocument("doc-" + queryId, 0.9)];
        }

        TrecRunFile.Write(RunPath, runs, runTag);
    }
}
