using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that the two rows of <c>docs/reference/benchmarks.md</c> which measure the same work
/// publish the same allocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these two rows and no others.</b> <c>AirtableBenchmarks</c> does not build its own
/// provider: <c>AirtableBenchmarks.cs</c> calls
/// <c>ConnectorIngestionBenchmarks.CreateAirtableProvider(count: 20, attachmentsPerRecord: 0)</c>,
/// which is the same factory, with the same arguments, that the Shared Ingestion table's
/// <c>Airtable</c> row reaches through its own <c>[Params]</c> switch. The two benchmark bodies are
/// character-identical apart from the field name. So <c>Airtable — FullTraversal</c> and
/// <c>Airtable</c> are not merely similar: they enumerate the same twenty records through the same
/// mock, and any figure that separates them is describing the harness rather than the code.
/// </para>
/// <para>
/// <b>This guard exists because that invariant was published broken for four months (#207).</b>
/// The commit that introduced <c>[IterationSetup]</c> to the mocked connectors, <c>b202d5ec</c>,
/// published <c>Airtable — FullTraversal</c> at 26.0 μs / 48.42 KB beside <c>Airtable</c> at
/// 140.5 μs / 66.68 KB — a 5.4× disagreement between two rows that cannot disagree. The specific
/// rows carried <c>[GlobalSetup]</c> numbers while the shared row carried <c>[IterationSetup]</c>
/// numbers: one connector, two harness modes, one table. Measured on 2026-09-08, the harness mode
/// alone is worth 6.9× (21.9 μs against 149.9 μs on unchanged code), which is the whole of the
/// movement the page had recorded as unexplained.
/// </para>
/// <para>
/// <b>It asserts allocation, not time.</b> Means drift between sessions — the page's own note puts
/// the run-to-run band near 8% — so equality there would be flaky and an inequality threshold would
/// be a number nobody could defend. Allocation is deterministic: two runs 24 hours apart reported
/// the same byte counts, and the two rows today agree exactly. A mixed-mode table breaks that
/// equality (48.42 against 66.68 KB), which is precisely the defect, so allocation catches it
/// without inviting a tolerance.
/// </para>
/// <para>
/// <b>What it cannot catch.</b> Both rows being re-recorded in the same wrong mode would satisfy
/// it, because they would agree with each other. This is a consistency check, not a correctness
/// one: it says the page does not contradict itself, not that the numbers are right. Nothing here
/// compares the page against a run — BenchmarkDotNet's artifacts are not tracked in this
/// repository, so a test cannot reach them, which is also why a 1 μs transcription slip in the
/// <c>DeltaWithFilter</c> row survived until #207 went looking.
/// </para>
/// </remarks>
public class BenchmarkSelfConsistencyTests
{
    private const string BenchmarksFileRelativePath = "docs/reference/benchmarks.md";

    /// <summary>The Shared Ingestion row: <c>| Airtable | 119.0 μs | 59.87 KB |</c>.</summary>
    private static readonly Regex SharedRow = new(
        @"^\|\s*Airtable\s*\|\s*(?<mean>[\d.]+)\s*(?:μs|us)\s*\|\s*(?<alloc>[\d.]+)\s*KB\s*\|",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The connector-specific row: <c>| Airtable — FullTraversal | 20 | 123.9 μs | 59.87 KB |</c>.
    /// The dash is an em dash in the page; matching any dash keeps a hyphen edit from silently
    /// turning this guard off.
    /// </summary>
    private static readonly Regex FullTraversalRow = new(
        @"^\|\s*Airtable\s*[—–-]\s*FullTraversal\s*\|\s*\d+\s*\|\s*(?<mean>[\d.]+)\s*(?:μs|us)\s*\|\s*(?<alloc>[\d.]+)\s*KB\s*\|",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Fact]
    public void TheTwoAirtableRowsThatMeasureTheSameWorkPublishTheSameAllocation()
    {
        var page = ReadPage();

        var shared = SharedRow.Match(page);
        var specific = FullTraversalRow.Match(page);

        // Anti-vacuous: a parse that finds nothing would pass every assertion below.
        Assert.True(
            shared.Success,
            $"No Shared Ingestion 'Airtable' row parsed out of {BenchmarksFileRelativePath}. The " +
            "table's shape changed and this guard is now asserting over nothing — fix the parse " +
            "rather than deleting the test.");

        Assert.True(
            specific.Success,
            $"No 'Airtable — FullTraversal' row parsed out of {BenchmarksFileRelativePath}. Same " +
            "reasoning as above: a guard that matches nothing passes forever.");

        var sharedAllocation = Allocation(shared);
        var specificAllocation = Allocation(specific);

        Assert.True(
            sharedAllocation == specificAllocation,
            FormattableString.Invariant(
                $"{BenchmarksFileRelativePath} publishes 'Airtable — FullTraversal' at ") +
            FormattableString.Invariant(
                $"{specificAllocation} KB and Shared Ingestion 'Airtable' at {sharedAllocation} KB. ") +
            "Those two rows enumerate the same twenty records through the same factory, so they " +
            "cannot allocate different amounts. This is what #207 turned out to be: figures from " +
            "two different harness modes published in one table. Re-record both rows in the same " +
            "run rather than reconciling them by hand.");
    }

    private static decimal Allocation(Match match) =>
        decimal.Parse(match.Groups["alloc"].Value, CultureInfo.InvariantCulture);

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(
            TestProject.FindRepositoryRoot(), "docs", "reference", "benchmarks.md"));
}
