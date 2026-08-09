using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Guards the per-package READMEs against this repository's dominant defect: documentation and
/// code agreeing with each other and both being wrong. Reads every README from inside the
/// produced <c>.nupkg</c> — what ships, never the working tree.
/// </summary>
/// <remarks>
/// <para>
/// Sixty-six packages each carrying a hand-written README is sixty-six new homes for the defect
/// found seven-plus times here already — most recently features.md telling consumers to set
/// <c>&lt;EnableOcr&gt;true&lt;/EnableOcr&gt;</c> in their own project file, which is impossible
/// against a compiled package. So the guard exists before any per-package README does: every
/// README written for Tasks 13–14 is written against a check that already works. Three checks:
/// the README is the package's own (not the repo-wide file every package ships today), it names
/// its own package id in its install command, and every API its C# examples reference exists as
/// a public member of what the package actually ships.
/// </para>
/// <para>
/// The extraction rule and resolvable-set construction are shared with
/// <see cref="DocsCodeExamplesTests"/> — which runs the identical check against docs/ pages, only
/// against the full set of produced packages rather than one package's closure — via
/// <see cref="ApiSurfaceCatalog"/>; its class remarks state the rule precisely and what is
/// deliberately left unchecked.
/// </para>
/// <para>
/// Discovery and skip behaviour are shared with <see cref="ProducedPackageTests"/>: no
/// <c>artifacts/packages</c> means nothing has packed and the tests skip, and
/// <see cref="WorkflowWiringTests"/> pins ci.yml so that skip cannot rot into permanent green.
/// </para>
/// </remarks>
public sealed partial class PackageReadmeTests
{
    [Fact]
    public void EveryPackageShipsItsOwnReadme()
    {
        var rootReadme = File.ReadAllBytes(
            Path.Combine(ProducedPackageTests.FindRepositoryRoot(), "README.md"));
        var failures = new List<string>();

        foreach (var package in ProducedPackageTests.DiscoverPackages())
        {
            var name = Path.GetFileName(package);
            var readme = ReadReadmeBytes(package);

            if (readme is null)
            {
                failures.Add($"{name}: contains no README.md entry.");
            }
            else if (BytesEqual(readme, rootReadme))
            {
                failures.Add($"{name}: ships a README byte-identical to the repository root README.md.");
            }
        }

        Assert.True(
            failures.Count == 0,
            ApiSurfaceCatalog.DescribeFailures(
                "Every package must ship its own README: the repo-wide README.md shows every " +
                "nuget.org visitor the whole project instead of the package they are looking " +
                "at, which is the consumer confusion this phase exists to end. " +
                "Directory.Build.props packs the root README.md into every package today; " +
                "Tasks 13-14 of the package-decomposition plan replace it per package.",
                failures));
    }

    [Fact]
    public void EveryReadmeNamesItsOwnPackageId()
    {
        var failures = new List<string>();

        foreach (var package in ProducedPackageTests.DiscoverPackages())
        {
            var nuspec = ProducedPackageTests.ReadNuspec(package);
            var id = ApiSurfaceCatalog.ReadNuspecValue(nuspec, "id");

            Assert.False(
                string.IsNullOrEmpty(id),
                $"{Path.GetFileName(package)} has a nuspec with no <id> element — not a valid package.");

            var readme = ReadReadmeText(package);
            if (readme is null)
            {
                continue; // EveryPackageShipsItsOwnReadme reports the missing file.
            }

            CheckInstallCommand(
                Path.GetFileName(package), id!, readme, IsDotnetToolPackage(nuspec), failures);
        }

        Assert.True(
            failures.Count == 0,
            ApiSurfaceCatalog.DescribeFailures(
                "Every README's install command must name that package's exact id, read from " +
                "the nuspec — this is what stops one templated README being pasted 66 times " +
                "with nobody noticing that none of them installs the package it sits in.",
                failures));
    }

    [Fact]
    public void EveryReadmeExampleResolvesAgainstTheAssembly()
    {
        var packages = ProducedPackageTests.DiscoverPackages();
        var byId = ApiSurfaceCatalog.MapPackagesById(packages);
        var failures = new List<string>();

        foreach (var package in packages)
        {
            CheckReadmeExamples(package, byId, failures);
        }

        Assert.True(
            failures.Count == 0,
            ApiSurfaceCatalog.DescribeFailures(
                "Every C# example in a package README must resolve against what that package " +
                "actually ships (its assemblies, its dependency closure, the shared " +
                "framework) — docs referencing APIs the installed package does not have is " +
                "this repository's dominant, seven-times-found defect. The extraction rule is " +
                "in ApiSurfaceCatalog's remarks; judge failures against it.",
                failures));
    }

    private static void CheckInstallCommand(
        string name, string id, string readme, bool isTool, List<string> failures)
    {
        // A dotnet tool is installed with `dotnet tool install`, a library with `dotnet add
        // package` — decided by the packageTypes the nuspec declares, never by a list of ids.
        var pattern = isTool ? ToolInstallCommand() : AddPackageCommand();
        var verb = isTool ? "dotnet tool install" : "dotnet add package";
        var namedIds = new List<string>();

        foreach (Match match in pattern.Matches(readme))
        {
            namedIds.Add(match.Groups["id"].Value);
        }

        if (namedIds.Count == 0)
        {
            failures.Add(
                $"{name}: its README contains no `{verb}` line at all, so a reader cannot " +
                "install what the page describes.");
            return;
        }

        foreach (var candidate in namedIds)
        {
            if (string.Equals(candidate, id, StringComparison.Ordinal))
            {
                return;
            }
        }

        failures.Add(
            $"{name}: its README's `{verb}` line(s) name [{string.Join(", ", namedIds)}] but " +
            $"never '{id}', the id this package actually publishes under — the templated-README " +
            "shape this test exists to stop.");
    }

    private static void CheckReadmeExamples(
        string packagePath, Dictionary<string, string> byId, List<string> failures)
    {
        var name = Path.GetFileName(packagePath);
        var readme = ReadReadmeText(packagePath);
        if (readme is null)
        {
            return; // EveryPackageShipsItsOwnReadme reports the missing file.
        }

        var fences = ApiSurfaceCatalog.ExtractCsharpFences(readme);
        if (fences.Count == 0)
        {
            return; // Nothing for shape-extraction to check; see ApiSurfaceCatalog's remarks.
        }

        var catalog = ResolutionCatalogs.GetOrAdd(packagePath, path => ApiSurfaceCatalog.BuildCatalogFromPackages(
            ApiSurfaceCatalog.CollectProducedClosure(path, byId), byId));
        var own = OwnCatalogs.GetOrAdd(packagePath, HarvestOwnAssembly);
        var declaredTypes = ApiSurfaceCatalog.ExtractDeclaredTypeNames(
            string.Concat(fences.Select(fence => fence.Code)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var touchesOwnApi = false;

        foreach (var fence in fences)
        {
            foreach (var reference in ApiSurfaceCatalog.ExtractReferences(fence.Code, catalog, declaredTypes))
            {
                var failure = ApiSurfaceCatalog.ResolveFailure(reference, catalog);
                if (failure is not null && seen.Add(failure))
                {
                    failures.Add($"{name}: {failure}.");
                }

                touchesOwnApi = touchesOwnApi || TouchesOwnApi(reference, own);
            }
        }

        if (!touchesOwnApi)
        {
            failures.Add(
                $"{name}: none of its C# examples reference any public API declared in the " +
                "package's own assembly — the examples demonstrate something other than this " +
                "package.");
        }
    }

    private static bool TouchesOwnApi(ApiSurfaceCatalog.ApiReference reference, ApiSurfaceCatalog.ApiCatalog own) =>
        reference.Kind switch
        {
            ApiSurfaceCatalog.ReferenceKind.Namespace => own.NamespacePrefixes.Contains(reference.Name),
            ApiSurfaceCatalog.ReferenceKind.Type => own.TypeNames.Contains(reference.Name),
            ApiSurfaceCatalog.ReferenceKind.StaticMember => own.TypeNames.Contains(reference.DeclaringType!),
            ApiSurfaceCatalog.ReferenceKind.MethodCall => own.MethodNames.Contains(reference.Name),
            _ => own.MemberNames.Contains(reference.Name),
        };

    // ---- Package reading ---------------------------------------------------------------------

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[]? ReadReadmeBytes(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, "README.md", StringComparison.OrdinalIgnoreCase))
            {
                using var source = entry.Open();
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        return null;
    }

    private static string? ReadReadmeText(string packagePath)
    {
        var bytes = ReadReadmeBytes(packagePath);
        if (bytes is null)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes));
        return reader.ReadToEnd();
    }

    private static bool IsDotnetToolPackage(XDocument nuspec)
    {
        foreach (var element in nuspec.Descendants())
        {
            if (string.Equals(element.Name.LocalName, "packageType", StringComparison.Ordinal) &&
                string.Equals(element.Attribute("name")?.Value, "DotnetTool", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Catalog construction ----------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, ApiSurfaceCatalog.CatalogSet> ResolutionCatalogs =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ApiSurfaceCatalog.ApiCatalog> OwnCatalogs =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The catalog of the one assembly that carries the package's own API — <c>{id}.dll</c>
    /// under <c>lib/</c> or <c>tools/</c> — used by the must-reference-own-API check. A dotnet
    /// tool ships its whole dependency graph in <c>tools/</c>, which is exactly why the check
    /// cannot use everything the package contains.
    /// </summary>
    /// <param name="packagePath">The absolute path of the <c>.nupkg</c>.</param>
    /// <returns>The catalog, empty when the package ships no assembly named after its id.</returns>
    private static ApiSurfaceCatalog.ApiCatalog HarvestOwnAssembly(string packagePath)
    {
        var id = ApiSurfaceCatalog.ReadNuspecValue(ProducedPackageTests.ReadNuspec(packagePath), "id");
        var fileName = id + ".dll";
        var catalog = new ApiSurfaceCatalog.ApiCatalog();
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (ApiSurfaceCatalog.IsShippedAssembly(entry.FullName) &&
                string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase))
            {
                ApiSurfaceCatalog.HarvestZipEntry(entry, catalog);
            }
        }

        return catalog;
    }

    // ---- Regexes (install-command checks; README-specific) -----------------------------------

    private const int RegexTimeout = 2000;

    [GeneratedRegex(
        @"dotnet\s+add\s+package\s+(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex AddPackageCommand();

    [GeneratedRegex(
        @"dotnet\s+tool\s+install\s+(?:-{1,2}[A-Za-z-]+\s+)*(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex ToolInstallCommand();
}
