using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Every <c>✅ Done</c> feature claim must name at least one symbol that actually ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds over <c>FeatureClaimTests</c>.</b> That guard checks a Done entry's
/// <c>**Package:**</c> line names a package that exists under <c>src/</c>. It cannot tell whether
/// the work the entry <i>describes</i> was done, which is
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/560">#560</see>: "Prompt Injection
/// Fortification" was marked Done while its body was a future-tense proposal, and the package it
/// named existed the whole time.
/// </para>
/// <para>
/// <b>What #560's premise turned out to be.</b> Measured 2026-09-13 across all 64 Done entries:
/// exactly one carried proposal-shaped language, and the entry that prompted the issue had already
/// been corrected by phase 6.2.40. The remaining entries are backed by shipping code. So this is
/// not an audit of stale claims — it is a floor under the one property that separates a claim from
/// a description: <b>a shipped feature can name something a reader can call.</b>
/// </para>
/// <para>
/// <b>Why a symbol and not a prose pattern.</b> Three text-shape scans were tried while scoping
/// this and the first two gave confidently wrong counts — one stripped the <c>**Status:**</c> line,
/// which is exactly where several entries name their type, and one rejected <c>GetDeltaToken()</c>
/// for carrying parentheses. Resolving against the produced assemblies is the method this
/// repository already trusts for docs examples; see
/// <see cref="DocsCodeExamplesTests"/> and <see cref="ApiSurfaceCatalog"/>.
/// </para>
/// <para>
/// <b>Why sections without a <c>**Package:**</c> line are out of scope, with no allowlist.</b>
/// Five of the 64 are <c>Group N</c> headings whose body is a table of connector packages rather
/// than a feature description. They claim no package, so there is no entry point for them to name.
/// The boundary is structural rather than a list of exceptions, so a new group heading needs no
/// maintenance here.
/// </para>
/// <para>
/// <b>A type OR a member counts, and the first draft of this guard got that wrong.</b> Written to
/// accept types only, it failed eleven entries — including "Time-Weighted Retrieval", which names
/// <c>DecayRate</c>, and "Map-Reduce Synthesis", which names <c>AskAsync</c> and
/// <c>SystemPrompt</c>. Those are entry points a reader can call; they are simply members rather
/// than types. Editing eleven correct entries to satisfy the guard would have damaged the
/// documentation to please a wrong check, which is the inversion this milestone keeps finding.
/// </para>
/// <para>
/// <b>Deliberately weak.</b> One resolvable symbol is enough. This cannot catch an entry that names
/// a real type while describing behaviour that type does not have — that needs a reader, and #560
/// records it. What it does catch is the shape that produced #560: an entry a reader cannot act on.
/// </para>
/// </remarks>
public sealed class FeatureClaimSymbolTests
{
    private const string FeaturesFileRelativePath = "docs/reference/features.md";

    private const string DoneStatusMarker = "**Status:** ✅ Done";

    /// <summary>
    /// Below this, the parse has broken rather than the document having shrunk.
    /// </summary>
    /// <remarks>
    /// 64 were marked Done on 2026-09-13. A guard that silently matches nothing reports success
    /// precisely when it has stopped guarding, which is the failure this milestone keeps removing.
    /// </remarks>
    private const int FewestPlausibleDoneClaims = 40;

    private static readonly Regex SectionSplit = new(
        @"^(?<heading>\#{2,4}\ .+)$",
        RegexOptions.Multiline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex PackageLine = new(
        @"^\*\*Packages?:\*\*",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private static readonly Regex BacktickedToken = new(
        @"`(?<token>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>A Done entry that claims a package must name a symbol from one.</summary>
    [Fact]
    public void EveryDoneFeatureNamesASymbolThatShips()
    {
        var packages = ProducedPackageTests.DiscoverPackages();
        Assert.SkipWhen(
            packages.Count == 0,
            "No packages under artifacts/packages, so nothing has been packed to resolve against. " +
            "WorkflowWiringTests pins ci.yml so this skip cannot rot into permanent green.");

        var catalog = ApiSurfaceCatalog.BuildCatalogFromPackages(
            packages, ApiSurfaceCatalog.MapPackagesById(packages));

        var claims = ReadDoneClaims();

        Assert.True(
            claims.Count >= FewestPlausibleDoneClaims,
            $"Found only {claims.Count} '{DoneStatusMarker}' sections carrying a package line in " +
            $"{FeaturesFileRelativePath}, which is below {FewestPlausibleDoneClaims}. The document " +
            "shrinking that far is less likely than this test's parse having broken, and a guard " +
            "that matches nothing passes exactly when it has stopped guarding.");

        var failures = new List<string>();
        foreach (var (heading, body) in claims)
        {
            if (!NamesAShippedSymbol(body, catalog))
            {
                failures.Add(heading);
            }
        }

        Assert.True(
            failures.Count == 0,
            $"These '{DoneStatusMarker}' entries name no type or member that ships in any produced package, " +
            "so a reader has no entry point to call and nothing distinguishes the entry from a " +
            "description of intent — the shape that produced #560:\n  " +
            string.Join("\n  ", failures) +
            "\n\nName a public type or method the feature exposes, in backticks. One is enough. " +
            "If a feature genuinely exposes nothing callable, that is worth saying out loud in " +
            "the entry rather than allowlisting here.");
    }

    /// <summary>Whether any backticked token in <paramref name="body"/> resolves to a shipped symbol.</summary>
    /// <param name="body">The section body, including its metadata lines.</param>
    /// <param name="catalog">The public surface of the produced packages.</param>
    /// <returns><see langword="true"/> when at least one token is a public type or member name.</returns>
    /// <remarks>
    /// The whole section is searched, metadata lines included: several entries name their type on
    /// the <c>**Status:**</c> line itself — "Sliding Window Chunking with Overlap" names
    /// <c>TokenAwareChunkingStrategy</c> there — and a scan that skipped those lines is one of the
    /// wrong answers described in this class's remarks.
    /// </remarks>
    private static bool NamesAShippedSymbol(string body, ApiSurfaceCatalog.CatalogSet catalog)
    {
        foreach (Match match in BacktickedToken.Matches(body))
        {
            var token = match.Groups["token"].Value;
            if (catalog.HasType(token) || catalog.HasMember(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads every Done section that claims a package.</summary>
    /// <returns>Heading and body for each, in document order.</returns>
    private static List<(string Heading, string Body)> ReadDoneClaims()
    {
        var path = Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), FeaturesFileRelativePath);
        var markdown = File.ReadAllText(path);

        var parts = SectionSplit.Split(markdown);
        var claims = new List<(string, string)>();

        for (var i = 1; i < parts.Length - 1; i += 2)
        {
            var heading = parts[i].Trim();
            var body = parts[i + 1];

            if (body.Contains(DoneStatusMarker, StringComparison.Ordinal)
                && PackageLine.IsMatch(body))
            {
                claims.Add((heading, body));
            }
        }

        return claims;
    }
}
