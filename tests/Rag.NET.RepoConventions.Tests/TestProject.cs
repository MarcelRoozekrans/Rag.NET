using System.Xml.Linq;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// One test project under <c>tests/</c>, as it exists on disk: what it declares about the CI tier it
/// belongs to, and what it actually does. The two are asserted against each other in
/// <see cref="TestProjectTierTests"/>.
/// </summary>
public sealed class TestProject
{
    private const string SolutionFileName = "Rag.NET.slnx";
    private const string TestSdkPackageId = "Microsoft.NET.Test.Sdk";
    private const string TestingProjectFileName = "Rag.NET.Testing.csproj";

    /// <summary>
    /// Container fixtures published by <c>tests/Rag.NET.Testing</c>. Adding a fixture that starts a
    /// container is one edit here.
    /// </summary>
    private static readonly string[] ContainerFixtureNames =
        ["PgVectorFixture", "QdrantFixture", "OllamaFixture"];

    private TestProject(string name, string directory, XDocument project)
    {
        Name = name;
        DeclaresRequiresDocker = DeclaresTrue(project, "RequiresDocker");
        DeclaresRequiresLlm = DeclaresTrue(project, "RequiresLlm");
        ReferencesTestcontainers = HasTestcontainersPackage(project);
        UsesAContainerFixture = MentionsAContainerFixture(directory, project);
    }

    /// <summary>Gets the project's directory name, which is also its assembly name.</summary>
    public string Name { get; }

    /// <summary>Gets a value indicating whether the csproj declares <c>RequiresDocker</c>.</summary>
    public bool DeclaresRequiresDocker { get; }

    /// <summary>Gets a value indicating whether the csproj declares <c>RequiresLlm</c>.</summary>
    public bool DeclaresRequiresLlm { get; }

    /// <summary>Gets a value indicating whether the csproj references a Testcontainers package.</summary>
    public bool ReferencesTestcontainers { get; }

    /// <summary>Gets a value indicating whether the project uses a container fixture from Rag.NET.Testing.</summary>
    public bool UsesAContainerFixture { get; }

    /// <summary>Gets a value indicating whether this project starts a container when its tests run.</summary>
    public bool StartsAContainer => ReferencesTestcontainers || UsesAContainerFixture;

    /// <summary>Gets a human-readable account of why <see cref="StartsAContainer"/> is what it is.</summary>
    public string ContainerEvidence => StartsAContainer
        ? ReferencesTestcontainers
            ? "it references a Testcontainers package"
            : "it uses a container fixture from Rag.NET.Testing"
        : "it references no Testcontainers package and uses no container fixture from Rag.NET.Testing";

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> to the directory holding
    /// <c>Rag.NET.slnx</c>.
    /// </summary>
    /// <returns>The absolute path of the repository root.</returns>
    /// <exception cref="InvalidOperationException">The solution file was not found.</exception>
    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        // Loudly, because the alternative is a conventions test that scans an empty set and reports
        // success — a guard that looks green precisely when it has stopped guarding anything.
        throw new InvalidOperationException(
            $"Could not find '{SolutionFileName}' in any ancestor of '{AppContext.BaseDirectory}'. " +
            "The repository-conventions tests read the working tree at run time and cannot run without it.");
    }

    /// <summary>Discovers every test project under <c>tests/</c>.</summary>
    /// <returns>The discovered projects, in directory order.</returns>
    public static IReadOnlyList<TestProject> DiscoverAll()
    {
        var testsDirectory = Path.Combine(FindRepositoryRoot(), "tests");
        var projects = new List<TestProject>();

        // tests/*/*.csproj — the same shape the CI workflow globs, so the two never disagree about
        // which projects exist.
        foreach (var directory in Directory.EnumerateDirectories(testsDirectory))
        {
            foreach (var projectFile in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                var document = XDocument.Load(projectFile);

                // Rag.NET.Testing lives here too but runs no tests: it is the shared fixture library,
                // and it references Testcontainers on behalf of the projects that consume it. A tier
                // means nothing for a project no test runner ever executes.
                if (HasPackage(document, TestSdkPackageId))
                {
                    projects.Add(new TestProject(Path.GetFileName(directory), directory, document));
                }
            }
        }

        return projects;
    }

    private static bool DeclaresTrue(XDocument project, string propertyName) =>
        project.Root!
            .Elements("PropertyGroup")
            .Elements(propertyName)
            .Any(static property => string.Equals(property.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static bool HasTestcontainersPackage(XDocument project)
    {
        // The package reference, not the word: several csprojs explain their RequiresDocker
        // declaration in a comment that says "Testcontainers", and a text search would therefore
        // match every project that already declares the property — turning this whole assertion
        // into `declares == declares`, which is vacuously true and catches nothing.
        foreach (var id in Includes(project, "PackageReference"))
        {
            if (string.Equals(id, "Testcontainers", StringComparison.Ordinal) ||
                id.StartsWith("Testcontainers.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPackage(XDocument project, string packageId)
    {
        foreach (var id in Includes(project, "PackageReference"))
        {
            if (string.Equals(id, packageId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReferencesTheTestingLibrary(XDocument project)
    {
        foreach (var include in Includes(project, "ProjectReference"))
        {
            if (include.EndsWith(TestingProjectFileName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Includes(XDocument project, string itemName)
    {
        foreach (var item in project.Root!.Elements("ItemGroup").Elements(itemName))
        {
            var include = item.Attribute("Include")?.Value;
            if (include is not null)
            {
                yield return include;
            }
        }
    }

    private static bool MentionsAContainerFixture(string directory, XDocument project)
    {
        // The gate below is load-bearing, not an optimisation. The fixture types are defined in
        // tests/Rag.NET.Testing, so a project that does not reference that project cannot be using
        // one no matter what its source text says — while a bare source scan produces false
        // positives for any project that merely *names* a fixture. This project is exactly such a
        // project: ContainerFixtureNames above spells all three names out, so without this gate the
        // conventions tests would conclude that they themselves start containers and demand they
        // declare RequiresDocker. Anything asserting on, or documenting, fixture names would hit the
        // same trap. Do not remove this as redundant.
        if (!ReferencesTheTestingLibrary(project))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file, directory))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (var fixtureName in ContainerFixtureNames)
            {
                if (source.Contains(fixtureName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsBuildOutput(string file, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, file);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        return string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase);
    }
}
