using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that packability is declared where the SDK defaults would otherwise decide it
/// silently. Phase 4.1's Task 1 found both failure shapes live: <c>Rag.NET.Mcp.Tool</c> was
/// configured as a shippable dotnet tool (<c>PackAsTool</c>, <c>ToolCommandName</c>,
/// <c>PackageId</c>) but used <c>Microsoft.NET.Sdk.Web</c>, whose <c>IsPackable=false</c>
/// default made every solution pack produce nothing for it — a codeless "packaging has been
/// disabled" advisory and no package. A package that silently never ships is the same shape as
/// a test suite that silently never runs.
/// </summary>
public sealed class PackabilityTests
{
    /// <summary>
    /// Mirrors <c>PackageVerificationTests.FewestPlausiblePackages</c>: a scan that finds far
    /// fewer projects than the repository holds has lost the working tree, and would pass —
    /// silently, forever — while asserting nothing.
    /// </summary>
    private const int FewestPlausibleSourceProjects = 60;

    [Fact]
    public void EveryDotnetToolProjectDeclaresItselfPackable()
    {
        var projects = LoadProjects("src");

        Assert.True(
            projects.Count >= FewestPlausibleSourceProjects,
            $"Found only {projects.Count} projects under src/, expected at least " +
            $"{FewestPlausibleSourceProjects}. A guard that scans nothing passes for the " +
            "wrong reason, so this fails instead.");

        foreach (var (relativePath, document) in projects)
        {
            if (!DeclaresValue(document, "PackAsTool", "true"))
            {
                continue;
            }

            // PackAsTool declares intent to ship; the SDK must not be able to retract it
            // silently. Microsoft.NET.Sdk.Web defaults IsPackable to false, so a tool project
            // that leaves the property implicit packs nothing and says so only in a codeless
            // advisory nobody reads. Intent to ship and permission to pack must be declared
            // together, in the file a reviewer actually sees.
            Assert.True(
                DeclaresValue(document, "IsPackable", "true"),
                $"{relativePath} declares PackAsTool=true but no <IsPackable>true</IsPackable>. " +
                "Its SDK's default decides packability silently — Microsoft.NET.Sdk.Web " +
                "defaults to false, which turns a configured dotnet tool into a package that " +
                "never ships and never fails. Declare IsPackable explicitly beside PackAsTool.");
        }
    }

    /// <summary>Loads every <c>{root}/*/*.csproj</c> with its repository-relative path.</summary>
    /// <param name="root">The top-level directory to scan, for example <c>src</c>.</param>
    /// <returns>The projects in directory order — the same shape the sibling guards scan.</returns>
    private static IReadOnlyList<(string RelativePath, XDocument Document)> LoadProjects(string root)
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var projects = new List<(string, XDocument)>();

        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(repositoryRoot, root)))
        {
            foreach (var projectFile in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                projects.Add((
                    Path.GetRelativePath(repositoryRoot, projectFile).Replace('\\', '/'),
                    XDocument.Load(projectFile)));
            }
        }

        return projects;
    }

    private static bool DeclaresValue(XDocument project, string propertyName, string value) =>
        project.Root!
            .Elements("PropertyGroup")
            .Elements(propertyName)
            .Any(property => string.Equals(property.Value.Trim(), value, StringComparison.OrdinalIgnoreCase));
}
