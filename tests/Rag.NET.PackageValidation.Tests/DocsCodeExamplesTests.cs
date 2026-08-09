using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Guards docs/ against the same defect <see cref="PackageReadmeTests"/> guards package READMEs
/// against: documentation and code agreeing with each other and both being wrong. Phase 4.5 found
/// three of these on <c>docs/getting-started.md</c> alone — a missing package in the install list,
/// two methods that do not exist on the pinned <c>Microsoft.Extensions.AI.OpenAI</c>, and a
/// namespace confused with its package id — on the one page every new user reads first, in a
/// repository that already had a working extractor for exactly this shape of defect sitting
/// unused one directory over. Nothing had ever checked the other docs pages.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="ApiSurfaceCatalog"/> verbatim: the same four extraction shapes, the same
/// <see cref="MetadataReader"/>-based harvesting of what a package actually ships, read straight
/// from the produced <c>.nupkg</c> files — never the working tree, never assembly loading. See
/// that class's remarks for the extraction rule stated precisely and what is deliberately left
/// unchecked.
/// </para>
/// <para>
/// <b>What differs from the README case.</b> A package README may only use what that package
/// ships (plus its dependency closure), so <see cref="PackageReadmeTests"/> resolves each README
/// against its own package's closure. A docs page can legitimately use anything the project
/// ships — <c>docs/getting-started.md</c> alone spans <c>Rag.NET</c>,
/// <c>Rag.NET.VectorStores.PgVector</c>, and <c>Rag.NET.Parsers.Pdf</c> in one walkthrough — so
/// this class resolves every fence against <em>every</em> produced package's surface at once, via
/// <see cref="ApiSurfaceCatalog.BuildCatalogFromPackages"/> given the whole discovered set rather
/// than one package's closure. There is no must-touch-its-own-API requirement here, because there
/// is no single "its own" package for a docs page to belong to.
/// </para>
/// <para>
/// <c>docs/plans/</c> is excluded: those are dated historical design records, not live
/// documentation, and their snippets can describe code that never shipped or was later removed.
/// </para>
/// <para>
/// Discovery and skip behaviour are shared with <see cref="ProducedPackageTests"/>: no
/// <c>artifacts/packages</c> means nothing has packed and the test skips, and
/// <see cref="WorkflowWiringTests"/> pins ci.yml so that skip cannot rot into permanent green.
/// </para>
/// </remarks>
public sealed class DocsCodeExamplesTests
{
    /// <summary>The docs subdirectory excluded from the check — see the class remarks.</summary>
    private const string ExcludedDirectoryName = "plans";

    [Fact]
    public void EveryDocsCodeExampleResolvesAgainstTheProducedPackages()
    {
        var packages = ProducedPackageTests.DiscoverPackages();
        var byId = ApiSurfaceCatalog.MapPackagesById(packages);
        var catalog = ApiSurfaceCatalog.BuildCatalogFromPackages(packages, byId);
        var repositoryRoot = ProducedPackageTests.FindRepositoryRoot();
        var failures = new List<string>();

        foreach (var file in DiscoverDocFiles(repositoryRoot))
        {
            CheckDocsExamples(file, repositoryRoot, catalog, failures);
        }

        Assert.True(
            failures.Count == 0,
            ApiSurfaceCatalog.DescribeFailures(
                "Every C# example on a docs page must resolve against what the produced " +
                "packages actually ship (their assemblies, their dependency closures, the " +
                "shared framework) — docs referencing APIs nothing ships is this repository's " +
                "dominant, repeatedly-found defect, and getting-started.md is the page every " +
                "new user follows first. The extraction rule is in ApiSurfaceCatalog's " +
                "remarks; judge failures against it.",
                failures));
    }

    /// <summary>
    /// Finds every markdown file under <c>docs/</c>, <c>docs/plans/</c> excluded — see the class
    /// remarks for why.
    /// </summary>
    /// <param name="repositoryRoot">The repository root, as found by
    /// <see cref="ProducedPackageTests.FindRepositoryRoot"/>.</param>
    /// <returns>The absolute paths of the markdown files to check, sorted for stable output.</returns>
    private static List<string> DiscoverDocFiles(string repositoryRoot)
    {
        var docsRoot = Path.Combine(repositoryRoot, "docs");
        var excludedRoot = $"{Path.Combine(docsRoot, ExcludedDirectoryName)}{Path.DirectorySeparatorChar}";
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories))
        {
            if (!file.StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void CheckDocsExamples(
        string filePath,
        string repositoryRoot,
        ApiSurfaceCatalog.CatalogSet catalog,
        List<string> failures)
    {
        var markdown = File.ReadAllText(filePath);
        var fences = ApiSurfaceCatalog.ExtractCsharpFences(markdown);
        if (fences.Count == 0)
        {
            return; // Nothing for shape-extraction to check; see ApiSurfaceCatalog's remarks.
        }

        var relativePath = Path.GetRelativePath(repositoryRoot, filePath).Replace('\\', '/');
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fence in fences)
        {
            foreach (var reference in ApiSurfaceCatalog.ExtractReferences(fence.Code, catalog))
            {
                var failure = ApiSurfaceCatalog.ResolveFailure(reference, catalog);
                if (failure is not null && seen.Add($"{fence.StartLine}:{failure}"))
                {
                    failures.Add($"{relativePath}:{fence.StartLine}: {failure}.");
                }
            }
        }
    }
}
