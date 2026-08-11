using System.Globalization;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Runs <see cref="CostReproducibility"/> over every measured cell of the Phase 5.1 cost matrix and
/// dumps the two tables §6 authorised: latency <b>cross-ecosystem</b>, indexing <b>per ecosystem</b>.
/// <para>
/// <b>It is a test rather than a console project, for the same reason the identity dump is.</b> The
/// one thing a published cost figure must guarantee is that it came through the real gate. A
/// separate tool would reimplement the comparison — the min/max, the ratio, the run-tag and
/// query-set pairing checks — and a second implementation can disagree with the first while both
/// look right. Calling <see cref="CostReproducibility.FromSidecars"/> from the test project that
/// wrote the sidecars cannot drift from it.
/// </para>
/// <para>
/// <b>Why the two tables are shaped differently.</b> The per-query spans bracket the same work on
/// both sides of the run-file boundary with pooling excluded, so latency is genuinely comparable
/// across ecosystems. Indexing is not: the Python entrants' spans include each library's own
/// chunker while the .NET rows receive their units pre-built from
/// <c>BeirHarness.OneChunkPerDocument</c>, and the Python harness times a second, warmed build
/// after an untimed rehearsal. Both asymmetries push the same way, and neither is a library
/// difference — so a cross-ecosystem indexing row would publish a protocol artefact as a result.
/// </para>
/// <para>
/// <b>A missing cell fails; it does not quietly shrink the table.</b> Every row that cannot be
/// gated is collected and reported together, so one absent entrant neither hides the other eleven
/// nor lets a partial matrix print as if it were the whole thing — the failure mode this
/// repository keeps finding, where a guard covering half the data reads as coverage.
/// </para>
/// </summary>
public sealed class CostMatrixDumpTests
{
    /// <summary>The environment variable naming how many repeat runs the sweep wrote.</summary>
    public const string RunCountVariable = "RAGNET_COST_MATRIX_RUNS";

    private const string SkipReason =
        "Set RAGNET_COST_MATRIX_RUNS to the number of repeat runs a completed cost sweep wrote " +
        "(at least 2) to gate them and dump the publishable tables. See docs/reference/ci.md.";

    /// <summary>
    /// The measured cells. FiQA is .NET-only deliberately: no Python entrant has ever run it, so
    /// its Python vector cache is cold, and a cold cache does not merely cost time — it would pay
    /// 57,638 documents of embedding that no other row paid, inside or beside the timed span.
    /// </summary>
    private static readonly (string Dataset, string Entrant)[] Matrix =
    [
        ("scifact", "ragnet-control"),
        ("scifact", "semantic-kernel"),
        ("scifact", "langchain"),
        ("scifact", "llamaindex"),
        ("scifact", "haystack"),
        ("arguana", "ragnet-control"),
        ("arguana", "semantic-kernel"),
        ("arguana", "langchain"),
        ("arguana", "llamaindex"),
        ("arguana", "haystack"),
        ("fiqa", "ragnet-control"),
        ("fiqa", "semantic-kernel"),
    ];

    /// <summary>The entrants that run in this repository's own runtime, for the indexing split.</summary>
    private static readonly string[] DotNetEntrants = ["ragnet-control", "semantic-kernel"];

    private readonly ITestOutputHelper _output;

    public CostMatrixDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpsTheGatedCostMatrix_ForPublication()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            BeirHarness.SkipReason);
        var runCount = ReadRunCount();
        Assert.SkipWhen(runCount is null, SkipReason);

        var gated = new List<GatedCell>();
        var failures = new List<string>();
        foreach (var (dataset, entrant) in Matrix)
        {
            var runFilePath = RunFilePath(cacheDirectory, dataset, entrant);
            try
            {
                gated.Add(new GatedCell(
                    dataset, entrant, CostReproducibility.FromSidecars(runFilePath, runCount.Value)));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                failures.Add($"{dataset}/{entrant}: {ex.Message}");
            }
        }

        WriteLatencyTable(gated);
        WriteIndexingTables(gated);

        Assert.True(
            failures.Count == 0,
            $"{failures.Count.ToString(CultureInfo.InvariantCulture)} of " +
            $"{Matrix.Length.ToString(CultureInfo.InvariantCulture)} cells did not come through " +
            "the gate, so the tables above are not the matrix and must not be published as it:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", failures));
    }

    /// <summary>
    /// How many repeats to gate, or null when the dump was not asked for. A value below 2 fails
    /// rather than skipping: the operator asked for a dump and would otherwise get a green run
    /// that produced no table, which reads exactly like a completed one.
    /// </summary>
    private static int? ReadRunCount()
    {
        var raw = Environment.GetEnvironmentVariable(RunCountVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runCount)
            || runCount < 2)
        {
            throw new InvalidOperationException(
                $"{RunCountVariable} is '{raw}', which is not an integer of at least 2. No cost " +
                "figure may be published from a single run — the 23x embedding-cache defect " +
                "measured 55.2 s and 2.4 s for identical work — so there is no such thing as a " +
                "one-run dump to fall back to.");
        }

        return runCount;
    }

    private static string RunFilePath(string cacheDirectory, string dataset, string entrant) =>
        Path.Combine(cacheDirectory, "runs", dataset + "-" + entrant + ".trec");

    /// <summary>Publishable milliseconds: two decimals, invariant, so the tables sort and diff.</summary>
    private static string Milliseconds(MeasurementSpread spread) =>
        Range(spread, "0.0", " ms");

    private static string Seconds(MeasurementSpread spread) => Range(spread, "0.00", " s");

    private static string Range(MeasurementSpread spread, string format, string unit) =>
        spread.Minimum.ToString(format, CultureInfo.InvariantCulture) + "–" +
        spread.Maximum.ToString(format, CultureInfo.InvariantCulture) + unit +
        " (×" + spread.Ratio.ToString("0.00", CultureInfo.InvariantCulture) + ")";

    private void WriteLatencyTable(IReadOnlyList<GatedCell> gated)
    {
        _output.WriteLine("### Query latency — cross-ecosystem");
        _output.WriteLine(string.Empty);
        _output.WriteLine("| Dataset | Entrant | p50 | p99 (reported, never gated) |");
        _output.WriteLine("|---|---|---|---|");
        foreach (var cell in gated)
        {
            _output.WriteLine(
                $"| {cell.Dataset} | `{cell.Spread.RunTag}` | " +
                $"{Milliseconds(cell.Spread.P50Milliseconds)} | " +
                $"{Milliseconds(cell.Spread.P99Milliseconds)} |");
        }

        _output.WriteLine(string.Empty);
    }

    private void WriteIndexingTables(IReadOnlyList<GatedCell> gated)
    {
        WriteIndexingTable(".NET", gated.Where(cell => IsDotNet(cell.Entrant)).ToList());
        WriteIndexingTable("Python", gated.Where(cell => !IsDotNet(cell.Entrant)).ToList());
    }

    private static bool IsDotNet(string entrant)
    {
        foreach (var candidate in DotNetEntrants)
        {
            if (string.Equals(candidate, entrant, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void WriteIndexingTable(string ecosystem, IReadOnlyList<GatedCell> cells)
    {
        _output.WriteLine(
            $"### Index construction — {ecosystem} only, not comparable across ecosystems");
        _output.WriteLine(string.Empty);
        _output.WriteLine("| Dataset | Entrant | Indexing |");
        _output.WriteLine("|---|---|---|");
        foreach (var cell in cells)
        {
            _output.WriteLine(
                $"| {cell.Dataset} | `{cell.Spread.RunTag}` | {Seconds(cell.Spread.IndexingSeconds)} |");
        }

        _output.WriteLine(string.Empty);
    }

    private sealed record GatedCell(string Dataset, string Entrant, CostReproducibility Spread);
}
