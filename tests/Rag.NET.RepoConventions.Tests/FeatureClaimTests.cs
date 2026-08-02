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
/// Scope, deliberately narrow: only sections whose <c>**Package:**</c> line sits immediately
/// after the Done status line are parsed — 11 of the 54 Done sections today. The other 43 put
/// the package line at the top of the section, under the heading; widening the parse to those is
/// Task 2 of Phase 4.0, which is allowed to conclude the widening is not worth it.
/// </para>
/// </remarks>
public sealed partial class FeatureClaimTests
{
    private const string FeaturesFileRelativePath = "docs/reference/features.md";
    private const string DoneStatusMarker = "**Status:** ✅ Done";
    private const string PackageMarker = "**Package:**";

    /// <summary>
    /// There are 11 Done sections with an adjacent package line today, naming 12 packages
    /// between them. Far fewer means the parse lost the file's shape and is asserting over
    /// nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausibleClaims = 10;

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
        };

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
                $"No Done section in {FeaturesFileRelativePath} names '{package}' adjacent to " +
                $"its status line any more, so its entry in {nameof(KnownFalseClaims)} is " +
                $"stale. Delete the entry. It was recorded because: {reason}");
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
    /// Parses every package claim a Done section makes: for each <c>**Status:** ✅ Done</c>
    /// line immediately followed by a <c>**Package:**</c> line, one claim per package name on
    /// that line.
    /// </summary>
    /// <returns>The claims, in file order.</returns>
    private static IReadOnlyList<FeatureClaim> ParseClaims()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), "docs", "reference", "features.md");
        var lines = File.ReadAllLines(path);
        var claims = new List<FeatureClaim>();
        var heading = "(before the first heading)";

        for (var index = 0; index < lines.Length - 1; index++)
        {
            var line = lines[index];

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                heading = line[4..].Trim();
                continue;
            }

            // StartsWith, not Equals: several Done lines carry trailing prose ("✅ Done — all
            // three triggers delivered…") and are no less Done for it.
            if (!line.StartsWith(DoneStatusMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var next = lines[index + 1];
            if (next.StartsWith(PackageMarker, StringComparison.Ordinal))
            {
                foreach (Match match in PackageName().Matches(next))
                {
                    claims.Add(new FeatureClaim(heading, match.Groups["package"].Value, index + 2));
                }
            }
        }

        return claims;
    }

    /// <summary>Matches one backticked package name on a <c>**Package:**</c> line.</summary>
    /// <remarks>
    /// Only names starting with <c>Rag.NET</c> count as packages: the same lines also backtick
    /// type names (<c>`PersistentConversationMemory`</c>) and carry qualifiers ("(core)",
    /// "(dotnet tool)"), none of which are directories under <c>src/</c>. A line naming several
    /// packages ("`Rag.NET` (core) + `Rag.NET.DataProviders.GitHub`") yields one claim each.
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"`(?<package>Rag\.NET[\w.]*)`", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex PackageName();

    /// <summary>
    /// One package claim: the section <paramref name="Heading"/> that is marked Done, the
    /// <paramref name="Package"/> it names, and the 1-based <paramref name="LineNumber"/> of
    /// the package line in features.md.
    /// </summary>
    private sealed record FeatureClaim(string Heading, string Package, int LineNumber);
}
