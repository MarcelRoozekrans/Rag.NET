using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that the instrument table in <c>docs/reference/opentelemetry.md</c>'s <c>## Metrics</c>
/// section names exactly the instruments <c>src/Rag.NET/Telemetry/RagTelemetry.cs</c> actually
/// defines — no more, no fewer.
/// </summary>
/// <remarks>
/// <para>
/// <b>This has already drifted once.</b> The table used to list 8 of <c>RagTelemetry</c>'s 11
/// instruments, missing <c>ragnet.ratelimit.wait.duration</c>, <c>ragnet.llm.tokens</c> and
/// <c>ragnet.llm.cost</c> — three instruments added after the table was last touched. Nothing
/// caught it because nothing compared the two. A doc that disagrees with the code it documents is
/// this repository's most-repeated defect; this test exists so this specific instance of it cannot
/// recur silently.
/// </para>
/// <para>
/// <b>Scope: the <c>## Metrics</c> section only.</b> <c>opentelemetry.md</c> mentions
/// <c>ragnet.*</c> names elsewhere too — the Spans table, the satellite-spans reference table, the
/// tag-naming examples — none of which name a <see cref="System.Diagnostics.Metrics.Meter"/>
/// instrument, and the "Two meters" subsection names <c>ragnet.shadow.*</c> counters that live on
/// the separate <c>"Rag.NET.Evaluation"</c> meter (<c>ShadowTelemetry</c>), not on
/// <c>RagTelemetry</c>. Scanning the whole file would conflate all of these with the instrument
/// table this test guards. The scan therefore reads only the lines between the <c>## Metrics</c>
/// heading and the next <c>## </c> heading, and within that range only Markdown table rows whose
/// first cell is a backticked <c>ragnet.*</c> name — which is what the "Two meters" prose is not,
/// since it names <c>ragnet.shadow.*</c> inside a parenthetical aside rather than a table cell.
/// </para>
/// </remarks>
public sealed partial class RagTelemetryMetricsDocumentationTests
{
    private const string RagTelemetryRelativePath = "src/Rag.NET/Telemetry/RagTelemetry.cs";
    private const string DocsRelativePath = "docs/reference/opentelemetry.md";
    private const string MetricsSectionHeading = "## Metrics";

    /// <summary>
    /// <c>RagTelemetry</c> defines 11 instruments today (5 histograms, 6 counters). Far fewer means
    /// the source-scanning regex stopped matching <c>Meter.CreateHistogram</c>/<c>CreateCounter</c>
    /// calls, which would make the comparison below vacuous — both sides empty, and green for the
    /// wrong reason.
    /// </summary>
    private const int FewestPlausibleInstruments = 10;

    [Fact]
    public void RagTelemetryDefinesPlausiblyManyInstruments()
    {
        var instruments = ParseRagTelemetryInstruments();

        Assert.True(
            instruments.Count >= FewestPlausibleInstruments,
            $"Found only {instruments.Count} instruments in {RagTelemetryRelativePath}, expected " +
            $"at least {FewestPlausibleInstruments}. A scan that stops matching " +
            "Meter.CreateHistogram/CreateCounter calls would pass the comparison test below for " +
            "the wrong reason — both sides empty — so this fails instead.");
    }

    [Fact]
    public void TheDocumentedInstrumentListMatchesRagTelemetry()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var codeInstruments = ParseRagTelemetryInstruments();
        var docInstruments = ParseDocumentedInstruments();

        var missingFromDocs = codeInstruments.Except(docInstruments, StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        var goneFromCode = docInstruments.Except(codeInstruments, StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missingFromDocs.Count == 0 && goneFromCode.Count == 0,
            BuildMismatchMessage(repositoryRoot, missingFromDocs, goneFromCode));
    }

    private static string BuildMismatchMessage(
        string repositoryRoot, IReadOnlyList<string> missingFromDocs, IReadOnlyList<string> goneFromCode)
    {
        var message = $"{DocsRelativePath}'s ## Metrics table disagrees with " +
            $"{RagTelemetryRelativePath} (repository root: {repositoryRoot}).";

        if (missingFromDocs.Count > 0)
        {
            message += " Defined in RagTelemetry but missing from the doc table: " +
                string.Join(", ", missingFromDocs) + ".";
        }

        if (goneFromCode.Count > 0)
        {
            message += " Listed in the doc table but no longer defined in RagTelemetry: " +
                string.Join(", ", goneFromCode) + ".";
        }

        return message + " Fix the doc table; do not relax this scan.";
    }

    /// <summary>
    /// Reads every <c>Meter.CreateHistogram&lt;T&gt;("name", ...)</c> /
    /// <c>Meter.CreateCounter&lt;T&gt;("name", ...)</c> instrument name declared in
    /// <see cref="RagTelemetryRelativePath"/>.
    /// </summary>
    private static HashSet<string> ParseRagTelemetryInstruments()
    {
        var path = Path.Combine(
            TestProject.FindRepositoryRoot(), RagTelemetryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in InstrumentDeclaration().Matches(source))
        {
            names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    [GeneratedRegex(
        @"Meter\.Create(?:Histogram|Counter)<\w+>\(\s*""(?<name>ragnet\.[a-z0-9_.]+)""",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex InstrumentDeclaration();

    /// <summary>
    /// Reads every backticked <c>ragnet.*</c> name in the first cell of a Markdown table row
    /// between the <c>## Metrics</c> heading and the next <c>## </c> heading in
    /// <see cref="DocsRelativePath"/>.
    /// </summary>
    private static HashSet<string> ParseDocumentedInstruments()
    {
        var path = Path.Combine(
            TestProject.FindRepositoryRoot(), DocsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var lines = File.ReadAllLines(path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var inSection = false;

        foreach (var line in lines)
        {
            if (line.StartsWith(MetricsSectionHeading, StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            var match = InstrumentTableRow().Match(line);
            if (match.Success)
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    [GeneratedRegex(
        @"^\|\s*`(?<name>ragnet\.[a-z0-9_.]+)`\s*\|",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex InstrumentTableRow();
}
