using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that <c>Directory.Packages.props</c> stays the only place a dependency version can be
/// written, and that no version written there floats.
/// </summary>
/// <remarks>
/// <para>
/// Phase 4.8 exists because a floating <c>PackageReference</c> does not ship as a range: it
/// resolves at pack time and freezes into the published nuspec as a concrete floor NuGet reads as
/// <c>&gt;=</c>. The dependency contract all 66 packages publish was therefore decided by when
/// <c>dotnet pack</c> happened to run — and the defect was measured live: <c>Qdrant.Client</c>
/// drifted from 1.18.1 to 1.19.0 between the phase plan being written and Task 1's baseline
/// capture, hours apart, with no commit touching it.
/// </para>
/// <para>
/// The phase's fix is Central Package Management: every version lives in
/// <c>Directory.Packages.props</c>, every <c>PackageReference</c> is bare. These guards keep it
/// that way. The third is the one that matters most — CPM only centralises the place a floating
/// version would be written, so a future <c>Version="1.*"</c> in the central file would
/// reintroduce the whole defect in one reviewable-looking line.
/// </para>
/// </remarks>
public sealed class DependencyPinningTests
{
    private const string CentralVersionsFileName = "Directory.Packages.props";

    /// <summary>
    /// There are 142 tracked csproj/props files today. A scan finding far fewer has lost the
    /// working tree and would assert over nothing — passing, silently, forever.
    /// </summary>
    private const int FewestPlausibleProjectFiles = 100;

    /// <summary>
    /// <c>Directory.Packages.props</c> pins 99 packages today. Far fewer means the file moved or
    /// the read went wrong, and every assertion over its contents would pass vacuously.
    /// </summary>
    private const int FewestPlausibleCentralPins = 80;

    /// <summary>
    /// Directories whose csproj/props files are not this repository's build: build output,
    /// git internals, other branches' checkouts under <c>.worktrees</c>, and packed artifacts.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".worktrees", "artifacts", "node_modules",
        };

    [Fact]
    public void NoPackageReferenceCarriesItsOwnVersion()
    {
        var files = LoadProjectFiles();
        AssertPlausibleScan(files.Count);

        var offenders = new List<string>();
        foreach (var (relativePath, document) in files)
        {
            foreach (var reference in PackageReferences(document))
            {
                // Any of the three spellings defeats central management for that one reference:
                // the Version attribute, the Version child element, and VersionOverride (which
                // CPM honours by design and this repository forbids by convention).
                if (reference.Attribute("Version") is not null ||
                    reference.Attribute("VersionOverride") is not null ||
                    reference.Element("Version") is not null)
                {
                    offenders.Add($"{relativePath}: {Id(reference)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These PackageReferences carry their own version, bypassing " +
            $"{CentralVersionsFileName}. A version written at the reference resolves at pack " +
            "time and freezes into the published nuspec, so what ships depends on when pack " +
            $"ran. Move the version to {CentralVersionsFileName}:\n" +
            string.Join('\n', offenders));
    }

    [Fact]
    public void EveryPackageReferenceHasACentralPin()
    {
        var files = LoadProjectFiles();
        AssertPlausibleScan(files.Count);
        var pins = LoadCentralPins().Keys;

        var offenders = new List<string>();
        foreach (var (relativePath, document) in files)
        {
            foreach (var reference in PackageReferences(document))
            {
                var id = reference.Attribute("Include")?.Value;
                if (id is not null && !pins.Contains(id))
                {
                    offenders.Add($"{relativePath}: {id}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These PackageReferences have no <PackageVersion> in " +
            $"{CentralVersionsFileName}. With central management on and no local version, " +
            "restore fails NU1010 — but only when that project restores, which a partial build " +
            $"can miss. Add the pin to {CentralVersionsFileName}:\n" +
            string.Join('\n', offenders));
    }

    /// <remarks>
    /// NuGet is the first line of defence here, not this test: with central management on, a
    /// floating <c>PackageVersion</c> fails restore outright with <c>NU1011</c> — verified by
    /// mutation, which never reached the test because restore failed first. What this guard
    /// actually covers is the case NU1011 stops covering: someone setting
    /// <c>CentralPackageFloatingVersionsEnabled</c> to <c>true</c>, which switches the error off
    /// repository-wide and would let every pin drift again. Mutating both together does reach
    /// this assertion and fails it, which is the only way it can fire.
    /// </remarks>
    [Fact]
    public void NoCentralPinFloats()
    {
        var pins = LoadCentralPins();

        var offenders = pins
            .Where(static pin => pin.Value.Contains('*', StringComparison.Ordinal))
            .Select(static pin => $"{pin.Key}: {pin.Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These <PackageVersion> entries in {CentralVersionsFileName} float. This is the " +
            "regression Phase 4.8 exists to prevent: a floating version resolves at pack time " +
            "and freezes into every published nuspec as a floor that moves with the restore " +
            "cache, exactly how Qdrant.Client drifted 1.18.1 to 1.19.0 in hours with no commit. " +
            "Pin the exact version:\n" +
            string.Join('\n', offenders));
    }

    /// <summary>
    /// Reads every <c>&lt;PackageVersion&gt;</c> pin out of <c>Directory.Packages.props</c>,
    /// failing loudly if the file has implausibly few — a vacuous scan passes for the wrong
    /// reason.
    /// </summary>
    /// <returns>Package id to pinned version, compared case-insensitively as NuGet does.</returns>
    private static Dictionary<string, string> LoadCentralPins()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), CentralVersionsFileName);
        var document = XDocument.Load(path);
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pin in document.Root!.Elements("ItemGroup").Elements("PackageVersion"))
        {
            var id = pin.Attribute("Include")?.Value;
            var version = pin.Attribute("Version")?.Value ?? pin.Element("Version")?.Value;
            if (id is not null && version is not null)
            {
                pins[id] = version;
            }
        }

        Assert.True(
            pins.Count >= FewestPlausibleCentralPins,
            $"Found only {pins.Count} <PackageVersion> pins in {CentralVersionsFileName}, " +
            $"expected at least {FewestPlausibleCentralPins}. A guard that scans nothing " +
            "passes for the wrong reason, so this fails instead.");

        return pins;
    }

    /// <summary>Loads every csproj and props file in the repository's own working tree.</summary>
    /// <returns>Repository-relative path and parsed document, in directory-walk order.</returns>
    private static IReadOnlyList<(string RelativePath, XDocument Document)> LoadProjectFiles()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var files = new List<(string, XDocument)>();

        foreach (var file in EnumerateProjectFiles(repositoryRoot))
        {
            files.Add((
                Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'),
                XDocument.Load(file)));
        }

        return files;
    }

    private static IEnumerable<string> EnumerateProjectFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(subdirectory);
            if (!SkippedDirectoryNames.Contains(name))
            {
                foreach (var file in EnumerateProjectFiles(subdirectory))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<XElement> PackageReferences(XDocument document) =>
        document.Root!.Elements("ItemGroup").Elements("PackageReference");

    private static string Id(XElement reference) =>
        reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value ?? "(no id)";

    private static void AssertPlausibleScan(int fileCount) =>
        Assert.True(
            fileCount >= FewestPlausibleProjectFiles,
            $"Found only {fileCount} csproj/props files, expected at least " +
            $"{FewestPlausibleProjectFiles}. A guard that scans nothing passes for the wrong " +
            "reason, so this fails instead.");
}
