using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Guards the dependency shape Phase "package decomposition" Part 1 produced, by reading the
/// nuspec inside each produced <c>.nupkg</c> — what actually ships — never the <c>.csproj</c>.
/// </summary>
/// <remarks>
/// <para>
/// Part 1 moved three opt-in clusters out of the <c>Rag.NET</c> core package (SQLite storage,
/// resilience, the hybrid cache) and merged three satellite families into single packages
/// (<c>Rag.NET.Parsers.Office</c>, <c>Rag.NET.DataProviders.Microsoft365</c>,
/// <c>Rag.NET.Chunking</c>). Nothing in <c>dotnet pack</c> enforces either outcome: re-adding
/// <c>Microsoft.Data.Sqlite</c> to core, or growing a merged package a new dependency its sources
/// never had, packs cleanly and exits 0. These tests are the mechanical enforcement.
/// </para>
/// <para>
/// Discovery and skip behaviour are shared with <see cref="ProducedPackageTests"/>: when
/// <c>artifacts/packages</c> does not exist nothing has packed and the tests skip, and
/// <see cref="WorkflowWiringTests"/> pins ci.yml so that skip cannot rot into permanent green.
/// </para>
/// </remarks>
public sealed class DependencyClosureTests
{
    /// <summary>
    /// The clusters Part 1 extracted from core, as nuspec dependency-id patterns. An id is
    /// forbidden when it equals a pattern or starts with the pattern plus a dot, so
    /// <c>SQLitePCLRaw</c> catches <c>SQLitePCLRaw.core</c> and <c>Polly</c> catches
    /// <c>Polly.Core</c> without also catching unrelated ids that merely share a prefix.
    /// <c>Microsoft.Extensions.Caching.Abstractions</c> is deliberately NOT here: core's cache
    /// behaviours compile against the abstractions; only the Hybrid implementation moved to
    /// <c>Rag.NET.Caching</c>.
    /// </summary>
    private static readonly string[] ExtractedClusterPatterns =
    [
        // SQLite storage, extracted to Rag.NET.Storage.Sqlite (f2518d5).
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw",
        // Resilience, extracted to Rag.NET.Resilience (7a1a661).
        "Microsoft.Extensions.Resilience",
        "Polly",
        "System.Threading.RateLimiting",
        // Hybrid cache implementation, moved to Rag.NET.Caching (e46fe26).
        "Microsoft.Extensions.Caching.Hybrid",
    ];

    /// <summary>
    /// The merged packages and the union of what their source packages declared, measured from
    /// the pre-merge csprojs in git history (parents of a8db630, a32f860 and 9ef4048). "Must"
    /// entries are the dependency that made each source package what it was — their absence means
    /// the test is reading the wrong thing, not that the package slimmed down.
    /// </summary>
    private static readonly IReadOnlyList<MergedPackageContract> MergedPackages =
    [
        new(
            "Rag.NET.Parsers.Office",
            Must: ["DocumentFormat.OpenXml"],
            // Union of Rag.NET.Parsers.Word, .Excel and .PowerPoint (parent of a8db630): each
            // declared exactly Rag.NET.Abstractions + DocumentFormat.OpenXml.
            May: ["Rag.NET.Abstractions"]),
        new(
            "Rag.NET.DataProviders.Microsoft365",
            // Kiota was a Must until the Microsoft.Graph v6 upgrade. All four sources declared it
            // explicitly, but only as an audit lift: Graph 5.x pinned Kiota 1.21.1, which carries
            // GHSA-7j59-v9qr-6fq9, patched in 1.22.0. Graph 6.2.0 pulls Kiota 2.0.0 transitively,
            // so the explicit references went and the package no longer declares it. It stays in
            // May rather than being deleted, because it was a genuine source dependency and a
            // future Graph that declares it again must not fail this guard.
            Must: ["Microsoft.Graph", "Azure.Identity"],
            // Union of the four Graph connectors (parent of a32f860): all four declared
            // Rag.NET.DataProviders, Graph, Kiota, Azure.Identity, Http.Resilience and
            // DependencyInjection.Abstractions; Exchange alone added Logging.Abstractions.
            May:
            [
                "Rag.NET.DataProviders",
                "Microsoft.Kiota.Abstractions",
                "Microsoft.Extensions.Http.Resilience",
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Microsoft.Extensions.Logging.Abstractions",
            ]),
        new(
            "Rag.NET.Chunking",
            Must: ["Microsoft.ML.Tokenizers"],
            // Union of pre-fold Rag.NET.Chunking, .TokenAware and .Semantic (parent of 9ef4048):
            // Chunking declared Abstractions + Logging.Abstractions + both Tokenizers packages,
            // TokenAware declared Abstractions + both Tokenizers packages, Semantic declared
            // Abstractions only.
            May:
            [
                "Rag.NET.Abstractions",
                "Microsoft.Extensions.Logging.Abstractions",
                "Microsoft.ML.Tokenizers.Data.Cl100kBase",
            ]),
    ];

    [Fact]
    public void TheCorePackageDoesNotDependOnExtractedClusters()
    {
        // Walk the produced-package graph from Rag.NET: its own nuspec plus every Rag.NET.*
        // package it reaches transitively. A forbidden cluster regressing into any of those ships
        // to every core consumer just as surely as a direct declaration would.
        var reachable = CollectReachablePackages("Rag.NET");

        // Prove the walk is reading real dependencies before asserting absences on it: core
        // legitimately compiles against the caching abstractions, so they must be visible here.
        Assert.Contains(
            "Microsoft.Extensions.Caching.Abstractions",
            reachable["Rag.NET"],
            StringComparer.OrdinalIgnoreCase);

        foreach (var (packageId, dependencies) in reachable)
        {
            foreach (var dependency in dependencies)
            {
                var pattern = FindMatchingClusterPattern(dependency);
                Assert.True(
                    pattern is null,
                    $"{packageId} (reached from the Rag.NET core package) declares a dependency " +
                    $"on '{dependency}', which matches extracted cluster '{pattern}'. Part 1 " +
                    "moved SQLite to Rag.NET.Storage.Sqlite, resilience to Rag.NET.Resilience " +
                    "and the hybrid cache to Rag.NET.Caching precisely so core consumers do not " +
                    "pay for them; consume the satellite package instead of re-adding the " +
                    "dependency to anything core reaches.");
            }
        }
    }

    [Fact]
    public void MergedPackagesDeclareOnlyWhatTheirSourcesDeclared()
    {
        foreach (var contract in MergedPackages)
        {
            var declared = ReadDependencyIds(FindPackage(contract.PackageId));

            foreach (var required in contract.Must)
            {
                Assert.True(
                    declared.Contains(required),
                    $"{contract.PackageId} no longer declares '{required}', the dependency its " +
                    "source packages existed to carry. Either the nuspec is being read wrongly " +
                    "or the package genuinely changed shape — both need a human.");
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allowed.UnionWith(contract.Must);
            allowed.UnionWith(contract.May);
            foreach (var dependency in declared)
            {
                Assert.True(
                    allowed.Contains(dependency),
                    $"{contract.PackageId} declares '{dependency}', which none of its pre-merge " +
                    "source packages declared. The merge contract is that every dependency of " +
                    "the merged package was already a dependency of AT LEAST ONE source — not " +
                    "of each source: pre-merge Rag.NET.Chunking.Semantic declared only " +
                    "Rag.NET.Abstractions, so a Semantic-only consumer does gain the tokenizer " +
                    "packages (at zero byte cost, because core independently ships both and " +
                    "semantic chunking is unusable without core). A dependency outside the " +
                    "union is new to every consumer; if it is genuinely needed, widen this " +
                    "contract in the same commit and say why consumers now pay for it.");
            }
        }
    }

    private static string? FindMatchingClusterPattern(string dependencyId)
    {
        foreach (var pattern in ExtractedClusterPatterns)
        {
            if (dependencyId.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                dependencyId.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase))
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>
    /// Breadth-first walk of the produced packages from <paramref name="rootId"/>, following
    /// dependencies that are themselves produced packages. External ids stay as leaves — their
    /// nuspecs are not in <c>artifacts/packages</c> to read.
    /// </summary>
    /// <param name="rootId">The package id to start from.</param>
    /// <returns>Each reachable produced package id mapped to the dependency ids it declares.</returns>
    private static Dictionary<string, IReadOnlySet<string>> CollectReachablePackages(string rootId)
    {
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in ProducedPackageTests.DiscoverPackages())
        {
            byId[ReadPackageId(package)] = package;
        }

        Assert.True(
            byId.ContainsKey(rootId),
            $"No produced package has id '{rootId}' — the core package itself is missing from " +
            "artifacts/packages, so there is no dependency shape to guard.");

        var reachable = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (reachable.ContainsKey(id) || !byId.TryGetValue(id, out var path))
            {
                continue;
            }

            var dependencies = ReadDependencyIds(path);
            reachable[id] = dependencies;
            foreach (var dependency in dependencies)
            {
                queue.Enqueue(dependency);
            }
        }

        return reachable;
    }

    private static string FindPackage(string packageId)
    {
        foreach (var package in ProducedPackageTests.DiscoverPackages())
        {
            if (ReadPackageId(package).Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                return package;
            }
        }

        throw new InvalidOperationException(
            $"No package in artifacts/packages has id '{packageId}'. The merged package this " +
            "contract guards no longer packs — that is a shape change a human must look at.");
    }

    private static string ReadPackageId(string packagePath)
    {
        foreach (var element in ProducedPackageTests.ReadNuspec(packagePath).Descendants())
        {
            if (string.Equals(element.Name.LocalName, "id", StringComparison.Ordinal))
            {
                return element.Value;
            }
        }

        throw new InvalidOperationException(
            $"'{packagePath}' has a nuspec with no <id> element — it is not a valid package.");
    }

    /// <summary>
    /// Reads every <c>&lt;dependency id="..."&gt;</c> across all target-framework groups of the
    /// package's nuspec.
    /// </summary>
    /// <param name="packagePath">The absolute path of the <c>.nupkg</c>.</param>
    /// <returns>The distinct declared dependency ids.</returns>
    private static IReadOnlySet<string> ReadDependencyIds(string packagePath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in ProducedPackageTests.ReadNuspec(packagePath).Descendants())
        {
            if (string.Equals(element.Name.LocalName, "dependency", StringComparison.Ordinal))
            {
                var id = element.Attribute("id")?.Value;
                Assert.False(
                    string.IsNullOrEmpty(id),
                    $"'{packagePath}' declares a <dependency> element without an id attribute — " +
                    "not a shape NuGet produces; the nuspec reading here is broken.");
                ids.Add(id!);
            }
        }

        return ids;
    }

    /// <summary>
    /// The dependency contract of a package produced by merging several source packages: what it
    /// must still declare, and the only other things it may declare.
    /// </summary>
    /// <param name="PackageId">The merged package's id.</param>
    /// <param name="Must">Dependencies whose absence means the test is reading the wrong thing.</param>
    /// <param name="May">The rest of the union of what the source packages declared.</param>
    private sealed record MergedPackageContract(string PackageId, string[] Must, string[] May);
}
