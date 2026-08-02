using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every feature section in <c>docs/reference/features.md</c> marked
/// <c>**Status:** ✅ Done</c> names only packages that exist under <c>src/</c>.
/// </summary>
/// <remarks>
/// Milestone 3's Definition of Done required features.md and the code to agree, and nothing
/// enforced it — which is how a section advertising a package that was never built survived to a
/// green v1.0 marked Done. Only <c>✅ Done</c> gates the assertion: a planned section that names
/// its future package is legitimate roadmap prose, and the live example — <c>Rag.NET.Cli</c>,
/// Phase 4.6's deliverable — carries no status line at all.
/// <para>
/// A claim is associated with its section structurally: a section runs from one heading to the
/// next, and its <c>**Package:**</c> / <c>**Packages:**</c> lines count wherever they sit in
/// it — under the heading, adjacent to the status line, anywhere between. The SaaS connector
/// section names its packages in tables instead, so table rows whose first cell is a package
/// name count too. All 54 Done statuses sit in sections the parse covers today.
/// </para>
/// </remarks>
public sealed partial class FeatureClaimTests
{
    private const string FeaturesFileRelativePath = "docs/reference/features.md";
    private const string DoneStatusMarker = "**Status:** ✅ Done";
    private const string PackageMarker = "**Package:**";
    private const string PackagesMarker = "**Packages:**";

    /// <summary>
    /// The 54 Done sections name 73 packages between them today — package lines and the SaaS
    /// connector tables combined. Far fewer means the parse lost the file's shape and is
    /// asserting over nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausibleClaims = 70;

    /// <summary>
    /// Done claims known to be false, recorded so the suite stays green while the findings stay
    /// visible in source. Every entry is a defect in features.md, not in this test: the section
    /// says Done and the package does not exist. Correcting the documentation is later
    /// Milestone 4 work; this list is Phase 4.0's record of what its guard found, and
    /// <see cref="EveryRecordedFalseClaimIsStillFalse"/> fails the moment an entry goes stale,
    /// so an entry cannot outlive the defect it records.
    /// </summary>
    private static readonly Dictionary<string, string> KnownFalseClaims =
        new(StringComparer.Ordinal)
        {
            // 'OpenTelemetry Tracing & Metrics', features.md line 667. Genuinely false, not a
            // wrong name: the section describes `.UseTelemetry()`, gen_ai.* semantic conventions
            // and metrics named ragnet.retrieve.latency / ragnet.answer.tokens /
            // ragnet.embed.batch_size, none of which were ever built. The real instruments live
            // in src/Rag.NET/Telemetry/RagTelemetry.cs — internal, in the core package, under
            // different names (ragnet.retrieve.duration, ragnet.llm.tokens, …) — and the
            // section's own feature-matrix row (~line 1135) is unchecked. Found by Phase 4.0;
            // correcting the section is a later Milestone 4 phase's work.
            ["Rag.NET.Telemetry"] =
                "features.md line 667 marks the OpenTelemetry section Done, but the package was " +
                "never built and the described API surface does not exist anywhere.",

            // 'C# Semantic Chunking (Roslyn)', features.md line 66. A wrong name, not a false
            // claim: the feature is real and lives in src/Rag.NET.Chunking.CSharp —
            // CSharpChunkingStrategy and CSharpChunkingOptions, the exact types the section
            // describes, with the Microsoft.CodeAnalysis.CSharp reference — but the section
            // names Rag.NET.Parsers.CSharp, which was never a directory under src/. Found by
            // Phase 4.0 Task 2's parse widening (the package line sits under the heading, six
            // lines before the status line, so Task 1's adjacency-only parse never read it);
            // correcting the section is a later Milestone 4 phase's work.
            ["Rag.NET.Parsers.CSharp"] =
                "features.md line 66 marks the C# Semantic Chunking section Done under the " +
                "package name Rag.NET.Parsers.CSharp, but the code lives in " +
                "src/Rag.NET.Chunking.CSharp — the documented package name is wrong.",
        };

    /// <summary>
    /// Pins every shape a package claim takes in features.md, one live example per shape. Losing
    /// a shape narrows the parse silently — the existence test would still pass, over fewer
    /// claims — so each shape is held by name here instead.
    /// </summary>
    /// <param name="package">A package that a Done section names in the given shape.</param>
    /// <param name="shape">Where in its section the claim sits, for the failure message.</param>
    [Theory]
    [InlineData("Rag.NET.Chunking.Templates", "a **Package:** line immediately after the Done status line")]
    [InlineData("Rag.NET.Chunking.Semantic", "a **Package:** line under the heading, the Done status at the section's end")]
    [InlineData("Rag.NET.Ingestion.AzureServiceBus", "a **Packages:** (plural) line naming several packages")]
    [InlineData("Rag.NET.DataProviders.Zendesk", "a package-table row under a Done group heading")]
    public void TheParseSeesEveryShapeAPackageClaimTakes(string package, string shape)
    {
        Assert.True(
            AnyClaimNames(ParseClaims(), package),
            $"No parsed claim names '{package}', which a Done section in " +
            $"{FeaturesFileRelativePath} claims via {shape}. That shape has stopped being " +
            "parsed, so every claim written that way is now unguarded — widen the parse back " +
            "rather than updating this example.");
    }

    [Fact]
    public void TheScanFindsTheDoneSectionsThatNameAPackage()
    {
        var claims = ParseClaims();

        Assert.True(
            claims.Count >= FewestPlausibleClaims,
            $"Parsed only {claims.Count} package claims from Done sections in " +
            $"{FeaturesFileRelativePath}, expected at least {FewestPlausibleClaims}. A guard " +
            "that parses nothing passes for the wrong reason, so this fails instead — either " +
            "the file moved, its status/package markers changed shape, or the parse regressed.");
    }

    [Fact]
    public void EveryPackageNamedInADoneSectionExistsUnderSrc()
    {
        var sourceDirectory = Path.Combine(TestProject.FindRepositoryRoot(), "src");

        foreach (var claim in ParseClaims())
        {
            if (KnownFalseClaims.ContainsKey(claim.Package))
            {
                continue;
            }

            Assert.True(
                Directory.Exists(Path.Combine(sourceDirectory, claim.Package)),
                $"'{claim.Heading}' is marked '{DoneStatusMarker}' and names package " +
                $"'{claim.Package}' ({FeaturesFileRelativePath} line {claim.LineNumber}), but " +
                $"src/{claim.Package} does not exist. Either the feature is not Done, the " +
                "package name is wrong, or the code was never built — each of those is a defect " +
                "in the documentation, not in this test. Fix the claim; do not relax the parse.");
        }
    }

    [Fact]
    public void EveryRecordedFalseClaimIsStillFalse()
    {
        // The allow-list keeps the suite green without hiding the findings; this keeps the
        // allow-list honest in both directions. An entry whose package now exists, or whose
        // claim has left features.md, is stale — and a stale entry is a hole in the guard, so
        // it fails here until it is deleted.
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var claims = ParseClaims();

        foreach (var (package, reason) in KnownFalseClaims)
        {
            Assert.False(
                Directory.Exists(Path.Combine(repositoryRoot, "src", package)),
                $"src/{package} exists now, so its entry in {nameof(KnownFalseClaims)} is " +
                $"stale. Delete the entry so " +
                $"{nameof(EveryPackageNamedInADoneSectionExistsUnderSrc)} guards the claim " +
                $"again. It was recorded because: {reason}");

            Assert.True(
                AnyClaimNames(claims, package),
                $"No Done section in {FeaturesFileRelativePath} names '{package}' any more, " +
                $"so its entry in {nameof(KnownFalseClaims)} is stale. Delete the entry. " +
                $"It was recorded because: {reason}");
        }
    }

    private static bool AnyClaimNames(IReadOnlyList<FeatureClaim> claims, string package)
    {
        foreach (var claim in claims)
        {
            if (string.Equals(claim.Package, package, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses every package claim a Done section makes. A section runs from one <c>##</c> or
    /// <c>###</c> heading to the next; a section any of whose lines starts with
    /// <c>**Status:** ✅ Done</c> claims every package its <c>**Package:**</c> /
    /// <c>**Packages:**</c> lines and package-table rows name, wherever in the section they sit.
    /// </summary>
    /// <returns>The claims, in file order.</returns>
    private static IReadOnlyList<FeatureClaim> ParseClaims()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), "docs", "reference", "features.md");
        var lines = File.ReadAllLines(path);
        var claims = new List<FeatureClaim>();
        var heading = "(before the first heading)";
        var sectionStart = 0;
        var index = 0;

        foreach (var line in lines)
        {
            if (IsSectionHeading(line))
            {
                AddSectionClaims(claims, lines, sectionStart, index, heading);
                heading = line.TrimStart('#').Trim();
                sectionStart = index + 1;
            }

            index++;
        }

        AddSectionClaims(claims, lines, sectionStart, lines.Length, heading);
        return claims;
    }

    /// <summary>
    /// A section boundary is a <c>##</c> or <c>###</c> heading — deliberately not <c>####</c>.
    /// The SaaS connector groups are <c>####</c> subsections whose Done statuses and package
    /// tables belong to the one parent section, and splitting there would orphan the tables
    /// from the section that claims them.
    /// </summary>
    /// <param name="line">The line to classify.</param>
    /// <returns>Whether <paramref name="line"/> starts a new section.</returns>
    private static bool IsSectionHeading(string line) =>
        line.StartsWith("## ", StringComparison.Ordinal) ||
        line.StartsWith("### ", StringComparison.Ordinal);

    /// <summary>
    /// Adds one claim per package named by the section spanning
    /// [<paramref name="start"/>, <paramref name="end"/>), if any of its lines marks it Done.
    /// </summary>
    /// <param name="claims">The list to add to.</param>
    /// <param name="lines">All lines of features.md.</param>
    /// <param name="start">The first line index of the section, inclusive.</param>
    /// <param name="end">The line index the section ends before, exclusive.</param>
    /// <param name="heading">The section's heading text, for failure messages.</param>
    private static void AddSectionClaims(List<FeatureClaim> claims, string[] lines, int start, int end, string heading)
    {
        if (!SectionIsDone(lines, start, end))
        {
            return;
        }

        var lineNumber = start;

        foreach (ref readonly var line in lines.AsSpan(start, end - start))
        {
            lineNumber++;

            if (line.StartsWith(PackageMarker, StringComparison.Ordinal) ||
                line.StartsWith(PackagesMarker, StringComparison.Ordinal))
            {
                foreach (Match match in PackageName().Matches(line))
                {
                    claims.Add(new FeatureClaim(heading, match.Groups["package"].Value, lineNumber));
                }

                continue;
            }

            var row = PackageTableRow().Match(line);
            if (row.Success)
            {
                claims.Add(new FeatureClaim(heading, row.Groups["package"].Value, lineNumber));
            }
        }
    }

    /// <summary>
    /// Tells whether any line of the section marks it Done. StartsWith, not Equals: several
    /// Done lines carry trailing prose ("✅ Done — all three triggers delivered…") and are no
    /// less Done for it.
    /// </summary>
    /// <param name="lines">All lines of features.md.</param>
    /// <param name="start">The first line index of the section, inclusive.</param>
    /// <param name="end">The line index the section ends before, exclusive.</param>
    /// <returns>Whether the section is marked Done.</returns>
    private static bool SectionIsDone(string[] lines, int start, int end)
    {
        foreach (ref readonly var line in lines.AsSpan(start, end - start))
        {
            if (line.StartsWith(DoneStatusMarker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Matches one backticked package name on a <c>**Package:**</c> line.</summary>
    /// <remarks>
    /// Only names starting with <c>Rag.NET</c> count as packages: the same lines also backtick
    /// type names (<c>`PersistentConversationMemory`</c>) and carry qualifiers ("(core)",
    /// "(dotnet tool)"), none of which are directories under <c>src/</c>. A line naming several
    /// packages ("`Rag.NET` (core) + `Rag.NET.DataProviders.GitHub`") yields one claim each.
    /// The SaaS section's own package line says <c>`Rag.NET.DataProviders.*`</c> — the
    /// <c>*</c> keeps it from matching, which is right: the concrete names live in the tables,
    /// and <see cref="PackageTableRow"/> claims those.
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"`(?<package>Rag\.NET[\w.]*)`", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex PackageName();

    /// <summary>
    /// Matches a Markdown table row whose first cell is exactly one backticked package name —
    /// the shape of the SaaS connector tables, whose header row literally reads "Package".
    /// </summary>
    /// <remarks>
    /// First cell only, and only in Done sections: the other tables in Done sections (option
    /// tables, benchmark tables, the bomb-cap table) never put a <c>Rag.NET</c> name in their
    /// first cell, and dependency mentions in later cells are not claims.
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"^\|\s*`(?<package>Rag\.NET[\w.]*)`\s*\|", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex PackageTableRow();

    /// <summary>
    /// One package claim: the section <paramref name="Heading"/> that is marked Done, the
    /// <paramref name="Package"/> it names, and the 1-based <paramref name="LineNumber"/> of
    /// the package line in features.md.
    /// </summary>
    private sealed record FeatureClaim(string Heading, string Package, int LineNumber);
}
