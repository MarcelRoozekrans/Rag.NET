using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every shippable package describes itself, because nothing else will:
/// Phase 4.1's Task 1 measured that <c>dotnet pack</c> enforces none of its metadata — a missing
/// <c>Description</c> does not warn, it silently becomes the literal string
/// <c>"Package Description"</c> on nuget.org. These are the source-level halves of the guard; the
/// packaged halves live in <c>tests/Rag.NET.PackageValidation.Tests</c>, which reads the produced
/// <c>.nupkg</c> files instead of the csproj files.
/// </summary>
public sealed class PackageDescriptionTests
{
    /// <summary>
    /// Mirrors <c>PackabilityTests.FewestPlausibleSourceProjects</c>: a scan that finds far fewer
    /// projects than the repository holds has lost the working tree, and would pass — silently,
    /// forever — while asserting nothing.
    /// </summary>
    private const int FewestPlausibleSourceProjects = 60;

    /// <summary>
    /// What NuGet ships when a project declares no <c>Description</c> at all. Not a warning, not
    /// an error — the published package simply says this. Measured by Phase 4.1's Task 1.
    /// </summary>
    private const string SdkPlaceholderDescription = "Package Description";

    [Fact]
    public void EverySourceProjectDeclaresItsOwnDescription()
    {
        var projects = LoadSourceProjects();

        foreach (var (relativePath, document) in projects)
        {
            var description = DeclaredDescription(document);

            Assert.False(
                string.IsNullOrWhiteSpace(description),
                $"{relativePath} declares no <Description>. dotnet pack does not warn about " +
                $"this: the package ships saying \"{SdkPlaceholderDescription}\", literally. " +
                "Describe what this package does, in its own csproj — Directory.Build.props " +
                "deliberately sets none, because a shared description describes nothing.");

            Assert.False(
                string.Equals(description!.Trim(), SdkPlaceholderDescription, StringComparison.OrdinalIgnoreCase),
                $"{relativePath} declares the SDK placeholder \"{SdkPlaceholderDescription}\" as " +
                "its description. That is the string NuGet ships when nobody wrote one; writing " +
                "it by hand is the same defect with extra steps.");
        }
    }

    [Fact]
    public void NoTwoPackagesShareADescription()
    {
        // Two packages described identically means at least one of them is wrong: the description
        // is the one piece of metadata that must differ per package, and duplication is how
        // copy-paste boilerplate looks from nuget.org's search results.
        var projects = LoadSourceProjects();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, document) in projects)
        {
            var description = DeclaredDescription(document)?.Trim();
            if (string.IsNullOrEmpty(description))
            {
                // The sibling test above owns missing descriptions; failing twice for the same
                // defect would only blur which guard to read.
                continue;
            }

            Assert.False(
                seen.TryGetValue(description, out var earlier),
                $"{relativePath} and {earlier} declare the same description: " +
                $"\"{description}\". A description two packages share fits neither of them — " +
                "each package must say what it, specifically, does.");

            seen[description] = relativePath;
        }
    }

    private static IReadOnlyList<(string RelativePath, XDocument Document)> LoadSourceProjects()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var projects = new List<(string, XDocument)>();

        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(repositoryRoot, "src")))
        {
            foreach (var projectFile in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                projects.Add((
                    Path.GetRelativePath(repositoryRoot, projectFile).Replace('\\', '/'),
                    XDocument.Load(projectFile)));
            }
        }

        Assert.True(
            projects.Count >= FewestPlausibleSourceProjects,
            $"Found only {projects.Count} projects under src/, expected at least " +
            $"{FewestPlausibleSourceProjects}. A guard that scans nothing passes for the " +
            "wrong reason, so this fails instead.");

        return projects;
    }

    private static string? DeclaredDescription(XDocument project) =>
        project.Root!
            .Elements("PropertyGroup")
            .Elements("Description")
            .Select(static property => property.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
