using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Validates the <c>.nupkg</c> files that <c>dotnet pack Rag.NET.slnx</c> actually produced.
/// </summary>
/// <remarks>
/// <para>
/// These checks are the only guard there is. Phase 4.1's Task 1 measured that <c>dotnet pack</c>
/// enforces none of its metadata: a missing licence, authors, URLs or tags emits nothing at all, a
/// missing README is a codeless advisory that fails nothing, and a missing description silently
/// ships as the literal string <c>"Package Description"</c>. The SDK's own <c>NU5xxx</c> surface
/// covers only structural defects (a file named by <c>PackageReadmeFile</c> but not packed, an
/// invalid version, a licence expression that does not parse) — everything asserted here is
/// enforced nowhere else.
/// </para>
/// <para>
/// The tests read <c>artifacts/packages</c> under the repository root and skip when it does not
/// exist, because on the CI matrix legs (and on a fresh checkout) nothing has packed. That skip
/// cannot rot into permanent green: <see cref="WorkflowWiringTests"/> runs unconditionally and
/// pins ci.yml to pack into exactly this directory and then run exactly this project.
/// </para>
/// </remarks>
public sealed class ProducedPackageTests
{
    /// <summary>
    /// The shippable set: the 70 packable projects under <c>src/</c> (71 minus
    /// <c>Rag.NET.Benchmarks.Quality</c>, which declares <c>IsPackable=false</c>). Exact on both
    /// sides deliberately — fewer means a project silently dropped out of the solution, which is
    /// precisely how <c>Rag.NET.WebSearch.Tavily.Tests</c> went unbuilt and untested for an
    /// unknown period; more means something unshippable is packing, which is how the sample and
    /// benchmark projects inflated the count before Task 1b. Adding or removing a package is a
    /// deliberate act; update this constant in the same commit.
    /// </summary>
    private const int ExpectedPackageCount = 70;

    /// <summary>
    /// What NuGet ships when a project declares no <c>Description</c>. Not a warning, not an
    /// error — the published package simply says this. Measured by Phase 4.1's Task 1.
    /// </summary>
    private const string SdkPlaceholderDescription = "Package Description";

    private const string ExpectedRepositoryUrl = "https://github.com/MarcelRoozekrans/Rag.NET";

    [Fact]
    public void ExactlyTheShippableSetIsPacked()
    {
        var packages = DiscoverPackages();

        Assert.True(
            packages.Count == ExpectedPackageCount,
            $"dotnet pack produced {packages.Count} packages, expected exactly " +
            $"{ExpectedPackageCount}. Fewer means a src/ project dropped out of the solution and " +
            "its package silently stopped shipping; more means something unshippable is packing " +
            "again, as samples/ and benchmarks/ did before Task 1b. If the shippable set really " +
            "changed, update ExpectedPackageCount in the same commit as the project.");
    }

    [Fact]
    public void EveryPackageDeclaresTheLicenceTheRepositoryShipsUnder()
    {
        // Both halves, so the two cannot drift apart: the repository's LICENSE file is MIT, and
        // every package must say the same thing in the machine-readable field nuget.org displays.
        var licenseFile = Path.Combine(FindRepositoryRoot(), "LICENSE");

        Assert.True(
            File.Exists(licenseFile),
            "The repository has no LICENSE file at its root, so there is nothing for the " +
            "packages' licence expression to match.");

        var firstLine = File.ReadLines(licenseFile).First().Trim();
        Assert.True(
            string.Equals(firstLine, "MIT License", StringComparison.Ordinal),
            $"LICENSE no longer starts with 'MIT License' (found '{firstLine}'). Every package " +
            "declares <license type=\"expression\">MIT</license>; if the repository relicensed, " +
            "Directory.Build.props and this test must change with it, deliberately.");

        foreach (var package in DiscoverPackages())
        {
            var license = ReadNuspecElement(package, "license");

            Assert.True(
                string.Equals(license, "MIT", StringComparison.Ordinal),
                $"{Path.GetFileName(package)} declares licence expression '{license}', expected " +
                "'MIT'. dotnet pack emits nothing at all for a missing licence — this check is " +
                "the only thing standing between an unlicensed package and nuget.org.");
        }
    }

    [Fact]
    public void EveryPackageCarriesTheReadmeItNames()
    {
        // PackageReadmeFile only names a file; packing it is a separate act, and forgetting the
        // second half is a codeless advisory that fails nothing. Task 1 shipped exactly that
        // defect, so this asserts the file is inside the package, not merely named by it.
        foreach (var package in DiscoverPackages())
        {
            var readme = ReadNuspecElement(package, "readme");
            Assert.True(
                string.Equals(readme, "README.md", StringComparison.Ordinal),
                $"{Path.GetFileName(package)} names '{readme}' as its readme, expected " +
                "'README.md' from Directory.Build.props.");

            Assert.True(
                HasEntry(package, static entry => string.Equals(entry, "README.md", StringComparison.Ordinal)),
                $"{Path.GetFileName(package)} names README.md but does not contain it. " +
                "PackageReadmeFile without the Pack=\"true\" item is a codeless advisory; the " +
                "package would reach nuget.org with no readme at all.");
        }
    }

    [Fact]
    public void EveryPackageDescribesItself()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in DiscoverPackages())
        {
            var name = Path.GetFileName(package);
            var description = ReadNuspecElement(package, "description")?.Trim();

            Assert.False(
                string.IsNullOrEmpty(description),
                $"{name} has an empty description. dotnet pack does not enforce this; " +
                "nuget.org would simply show nothing.");

            Assert.False(
                string.Equals(description, SdkPlaceholderDescription, StringComparison.OrdinalIgnoreCase),
                $"{name} shipped the SDK placeholder \"{SdkPlaceholderDescription}\" — the " +
                "string dotnet pack substitutes, without warning, when a project declares no " +
                "Description.");

            Assert.False(
                descriptions.TryGetValue(description!, out var earlier),
                $"{name} and {earlier} ship the same description: \"{description}\". A " +
                "description two packages share is boilerplate describing neither.");

            descriptions[description!] = name;
        }
    }

    [Fact]
    public void EveryPackageRecordsItsRepositoryAndCommit()
    {
        foreach (var package in DiscoverPackages())
        {
            var name = Path.GetFileName(package);
            var repository = ReadNuspecRepository(package);

            Assert.True(
                string.Equals(repository.Url, ExpectedRepositoryUrl, StringComparison.Ordinal),
                $"{name} records repository URL '{repository.Url}', expected " +
                $"'{ExpectedRepositoryUrl}'. Without it nobody can get from the package back to " +
                "the source.");

            Assert.True(
                !string.IsNullOrEmpty(repository.Commit),
                $"{name} records no repository commit. The SDK's SourceLink stamps one whenever " +
                "pack runs inside a git checkout, so an empty commit means the package was built " +
                "from sources no one can identify — unbuildable-from-source is not shippable.");
        }
    }

    [Fact]
    public void EveryPackageShipsItsSymbols()
    {
        // dotnet pack produces symbol packages only when IncludeSymbols is set, and says nothing
        // when it is not — the .snupkg simply never exists. No exemption for the DotnetTool
        // package: measured on 2026-08-03, pack produces a .snupkg for Rag.NET.Mcp.Tool exactly
        // like the libraries, so demanding fewer than all 70 would be leniency nothing needs.
        foreach (var package in DiscoverPackages())
        {
            var symbols = Path.ChangeExtension(package, ".snupkg");
            Assert.True(
                File.Exists(symbols),
                $"{Path.GetFileName(package)} has no matching .snupkg. Without symbol packages " +
                "every consumer stack trace into this library is line-numberless. IncludeSymbols " +
                "and SymbolPackageFormat=snupkg in Directory.Build.props produce them.");
        }
    }

    [Fact]
    public void NoPackageIsEmpty()
    {
        // A .nupkg with neither lib/ nor tools/ is metadata wrapped around nothing — the shape a
        // misconfigured project produces while exiting 0.
        foreach (var package in DiscoverPackages())
        {
            Assert.True(
                HasEntry(package, static entry =>
                    entry.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) ||
                    entry.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)),
                $"{Path.GetFileName(package)} contains no lib/ and no tools/ entries: it is a " +
                "package of pure metadata. Almost certainly a project misconfiguration that " +
                "dotnet pack was happy to ship.");
        }
    }

    /// <summary>
    /// Finds the produced packages, or skips when nothing has packed — see the class remarks for
    /// why that skip cannot become permanent.
    /// </summary>
    /// <returns>The absolute paths of the <c>.nupkg</c> files, symbol packages excluded.</returns>
    private static IReadOnlyList<string> DiscoverPackages()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "artifacts", "packages");

        Assert.SkipWhen(
            !Directory.Exists(directory),
            $"'{directory}' does not exist: nothing has packed. Produce it with " +
            "`dotnet pack Rag.NET.slnx -c Release -o artifacts/packages` — ci.yml's " +
            "pack-validate job does exactly that before running this project, and " +
            "WorkflowWiringTests pins that wiring so this skip cannot rot silently.");

        var packages = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            {
                packages.Add(file);
            }
        }

        packages.Sort(StringComparer.Ordinal);
        return packages;
    }

    private static string? ReadNuspecElement(string packagePath, string localName)
    {
        var nuspec = ReadNuspec(packagePath);

        foreach (var element in nuspec.Descendants())
        {
            if (string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
            {
                return element.Value;
            }
        }

        return null;
    }

    private static (string? Url, string? Commit) ReadNuspecRepository(string packagePath)
    {
        foreach (var element in ReadNuspec(packagePath).Descendants())
        {
            if (string.Equals(element.Name.LocalName, "repository", StringComparison.Ordinal))
            {
                return (element.Attribute("url")?.Value, element.Attribute("commit")?.Value);
            }
        }

        return (null, null);
    }

    private static XDocument ReadNuspec(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.Contains('/', StringComparison.Ordinal))
            {
                using var stream = entry.Open();
                return XDocument.Load(stream);
            }
        }

        throw new InvalidOperationException(
            $"'{packagePath}' contains no root-level .nuspec entry — it is not a NuGet package.");
    }

    private static bool HasEntry(string packagePath, Func<string, bool> predicate)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (predicate(entry.FullName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> to the directory holding
    /// <c>Rag.NET.slnx</c>. Duplicated from <c>Rag.NET.RepoConventions.Tests</c> deliberately —
    /// this project references nothing in the repository; see the csproj comment.
    /// </summary>
    /// <returns>The absolute path of the repository root.</returns>
    /// <exception cref="InvalidOperationException">The solution file was not found.</exception>
    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rag.NET.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find 'Rag.NET.slnx' in any ancestor of '{AppContext.BaseDirectory}'. " +
            "The package-validation tests read the working tree at run time and cannot run without it.");
    }
}
