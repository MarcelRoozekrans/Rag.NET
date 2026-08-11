using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that the Rag.NET column of the scope matrix in
/// <c>docs/reference/library-comparison-scope.md</c> agrees with the working tree: every ✅ names
/// a capability whose packages exist under <c>src/</c>, and every ✖ names one that genuinely has
/// no package.
/// </summary>
/// <remarks>
/// The scope page is a reading rather than a measurement — nothing in this repository builds the
/// comparator columns, so nothing can guard them. Rag.NET's own column is the one part that *is*
/// checkable, and leaving it unchecked is how a marketing table drifts: a ✅ survives the deletion
/// of the package behind it, or the ✖ rows quietly stop being true when the library grows an
/// agent surface and nobody edits the page.
/// <para>
/// Both directions are guarded on purpose. A ✅ going stale overstates the library; a ✖ going
/// stale understates it, and the two ✖ rows are the only place the page concedes anything, so
/// they are the rows most worth keeping honest.
/// </para>
/// </remarks>
public sealed partial class ScopeMatrixClaimTests
{
    private const string ScopeFileRelativePath = "docs/reference/library-comparison-scope.md";

    /// <summary>
    /// The capability rows of the matrix, mapped to the packages that back them. Keyed by the
    /// row's first cell with Markdown emphasis stripped, so a row may be bolded on the page
    /// without breaking the lookup.
    /// </summary>
    /// <remarks>
    /// An empty package list means the row claims ✖ — the capability is absent — and is checked
    /// by <see cref="EveryAbsenceClaimIsStillTrue"/> instead of by package existence.
    /// </remarks>
    private static readonly Dictionary<string, string[]> CapabilityPackages = new(StringComparer.Ordinal)
    {
        ["Ingestion pipeline"] = ["Rag.NET"],
        ["Document parsers, first-party"] = ["Rag.NET.Parsers.Pdf", "Rag.NET.Parsers.Office", "Rag.NET.Parsers.Html"],
        ["Source connectors (SaaS/storage)"] = ["Rag.NET.DataProviders", "Rag.NET.DataProviders.Confluence", "Rag.NET.DataProviders.Slack"],
        ["Chunking strategies"] = ["Rag.NET.Chunking", "Rag.NET.Chunking.CSharp", "Rag.NET.Chunking.Templates"],
        ["Vector stores, first-party"] = ["Rag.NET.VectorStores.Qdrant", "Rag.NET.VectorStores.PgVector", "Rag.NET.VectorStores.AzureAISearch"],
        ["Hybrid / sparse retrieval"] = ["Rag.NET"],
        ["Reranking"] = ["Rag.NET.Reranking.Onnx", "Rag.NET.Reranking.Cohere"],
        ["Query transformation"] = ["Rag.NET.QueryTechniques"],
        ["Hierarchical summary index (RAPTOR)"] = ["Rag.NET.Raptor"],
        ["Graph-community RAG (Leiden)"] = ["Rag.NET.GraphRag", "Rag.NET.Graph"],
        ["Multi-pass answer engines"] = ["Rag.NET.AnswerEngines"],
        ["Conversational memory"] = ["Rag.NET.Memory"],
        ["RAG evaluation"] = ["Rag.NET.Evaluation", "Rag.NET.Evaluation.Ragas"],
        ["IR metrics in-library"] = ["Rag.NET.Benchmarks.Quality"],
        ["Caching"] = ["Rag.NET.Caching"],
        ["Observability"] = ["Rag.NET.Telemetry", "Rag.NET.Diagnostics"],
        ["Resilience policies"] = ["Rag.NET.Resilience"],
        ["Prompt-injection defence"] = ["Rag.NET.Security"],
        ["Serving surface"] = ["Rag.NET.Api", "Rag.NET.Api.Grpc", "Rag.NET.Mcp", "Rag.NET.Cli"],
        ["Agent orchestration"] = [],
        ["Tool / function calling"] = [],
    };

    /// <summary>
    /// Package-name prefixes whose absence under <c>src/</c> is what makes the ✖ rows true. A
    /// directory matching one of these is the signal that Rag.NET grew the capability the page
    /// says it lacks.
    /// </summary>
    /// <remarks>
    /// This is a proxy, and a deliberately coarse one: a tool-calling surface added inside an
    /// existing package would not create a directory and would not trip this. The page's ✖ rows
    /// are a reading like every other cell — this test catches the loud way they go stale, not
    /// every way.
    /// </remarks>
    private static readonly string[] AbsentCapabilityPrefixes =
    [
        "Rag.NET.Agents",
        "Rag.NET.Agent",
        "Rag.NET.Tools",
        "Rag.NET.ToolCalling",
        "Rag.NET.Planning",
    ];

    /// <summary>
    /// The matrix has 21 capability rows today. Far fewer means the parse lost the table's shape
    /// and is asserting over nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausibleRows = 20;

    [Fact]
    public void TheScanFindsTheMatrixRows()
    {
        var rows = ParseRows();

        Assert.True(
            rows.Count >= FewestPlausibleRows,
            $"Parsed only {rows.Count} capability rows from the matrix in {ScopeFileRelativePath}, " +
            $"expected at least {FewestPlausibleRows}. A guard that parses nothing passes for the " +
            "wrong reason, so this fails instead — either the file moved, the table changed shape, " +
            "or the parse regressed.");
    }

    [Fact]
    public void EveryMatrixRowIsMapped()
    {
        foreach (var row in ParseRows())
        {
            Assert.True(
                CapabilityPackages.ContainsKey(row.Capability),
                $"The matrix in {ScopeFileRelativePath} has a row '{row.Capability}' (line " +
                $"{row.LineNumber}) that {nameof(CapabilityPackages)} does not map. A row nobody " +
                "mapped is a row nobody guards — add it to the map with the packages behind it, " +
                "or an empty list if the Rag.NET cell claims ✖.");
        }
    }

    [Fact]
    public void EveryMappedCapabilityIsStillInTheMatrix()
    {
        // The mirror of EveryMatrixRowIsMapped: a mapping whose row has left the page is stale,
        // and a stale mapping is a guard asserting over a claim nobody makes any more.
        var rows = ParseRows();

        foreach (var capability in CapabilityPackages.Keys)
        {
            Assert.Contains(rows, row => string.Equals(row.Capability, capability, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EveryPresenceClaimNamesPackagesThatExist()
    {
        var sourceDirectory = Path.Combine(TestProject.FindRepositoryRoot(), "src");

        foreach (var row in ParseRows())
        {
            if (!row.ClaimsPresent)
            {
                continue;
            }

            var packages = CapabilityPackages[row.Capability];

            Assert.True(
                packages.Length > 0,
                $"'{row.Capability}' is marked ✅ in {ScopeFileRelativePath} (line {row.LineNumber}) " +
                $"but {nameof(CapabilityPackages)} maps it to no package. Either the row is not ✅ " +
                "or the map is wrong.");

            foreach (var package in packages)
            {
                Assert.True(
                    Directory.Exists(Path.Combine(sourceDirectory, package)),
                    $"'{row.Capability}' is marked ✅ in {ScopeFileRelativePath} (line " +
                    $"{row.LineNumber}) and rests on package '{package}', but src/{package} does " +
                    "not exist. Either the capability is gone, the package was renamed, or the " +
                    "claim was never true — each is a defect in the page, not in this test.");
            }
        }
    }

    [Fact]
    public void EveryAbsenceClaimIsStillTrue()
    {
        var sourceDirectory = Path.Combine(TestProject.FindRepositoryRoot(), "src");
        var directories = Directory.GetDirectories(sourceDirectory).Select(Path.GetFileName).ToArray();

        foreach (var row in ParseRows())
        {
            if (row.ClaimsPresent)
            {
                continue;
            }

            foreach (var prefix in AbsentCapabilityPrefixes)
            {
                var match = Array.Find(
                    directories,
                    name => name is not null && name.StartsWith(prefix, StringComparison.Ordinal));

                if (match is not null)
                {
                    Assert.Fail(
                        $"'{row.Capability}' is marked ✖ in {ScopeFileRelativePath} (line " +
                        $"{row.LineNumber}) — the page concedes Rag.NET does not have it — but " +
                        $"src/{match} now exists. The concession has gone stale: update the matrix " +
                        "row and the prose under 'The two rows Rag.NET loses' rather than deleting " +
                        "this assertion.");
                }
            }
        }
    }

    /// <summary>
    /// Parses the capability rows of the scope matrix — the table whose header's first cell is
    /// "Capability". Each row yields its first cell (emphasis stripped) and whether the Rag.NET
    /// cell, the second, claims presence.
    /// </summary>
    /// <returns>The rows, in file order.</returns>
    private static IReadOnlyList<MatrixRow> ParseRows()
    {
        var path = Path.Combine(
            TestProject.FindRepositoryRoot(), "docs", "reference", "library-comparison-scope.md");
        var lines = File.ReadAllLines(path);
        var rows = new List<MatrixRow>();
        var inMatrix = false;
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;

            if (line.StartsWith("| Capability ", StringComparison.Ordinal))
            {
                inMatrix = true;
                continue;
            }

            if (!inMatrix)
            {
                continue;
            }

            // The table ends at the first line that is not a table row — the blank line after it.
            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = line.Split('|', StringSplitOptions.TrimEntries);

            // A split on '|' yields empty leading and trailing entries, so a row with a capability
            // and five library cells is 8 entries; the separator row's cells are all dashes.
            if (cells.Length < 3 || SeparatorCell().IsMatch(cells[1]))
            {
                continue;
            }

            var capability = Emphasis().Replace(cells[1], string.Empty).Trim();
            rows.Add(new MatrixRow(capability, cells[2].Contains('✅', StringComparison.Ordinal), lineNumber));
        }

        return rows;
    }

    /// <summary>Matches a Markdown table separator cell, whose content is only dashes and colons.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"^[-:]+$", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SeparatorCell();

    /// <summary>Matches Markdown emphasis markers, so a bolded row label still looks itself up.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"\*+", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex Emphasis();

    /// <summary>
    /// One matrix row: the <paramref name="Capability"/> its first cell names, whether its
    /// Rag.NET cell <paramref name="ClaimsPresent"/> (✅ rather than ✖), and the 1-based
    /// <paramref name="LineNumber"/> of the row.
    /// </summary>
    private sealed record MatrixRow(string Capability, bool ClaimsPresent, int LineNumber);
}
