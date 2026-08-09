using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// One test project under <c>tests/</c>, as it exists on disk: what it declares about the CI tier it
/// belongs to, and what it actually does. The two are asserted against each other in
/// <see cref="TestProjectTierTests"/>.
/// </summary>
public sealed partial class TestProject
{
    private const string SolutionFileName = "Rag.NET.slnx";
    private const string TestSdkPackageId = "Microsoft.NET.Test.Sdk";
    private const string TestingProjectFileName = "Rag.NET.Testing.csproj";

    /// <summary>
    /// The one fixture that downloads a language model. Named on its own because
    /// <c>RequiresLlm</c> is about the model, not about Docker: the other fixtures start a container
    /// and nothing else.
    /// </summary>
    private const string OllamaFixtureName = "OllamaFixture";

    /// <summary>
    /// Container fixtures published by <c>tests/Rag.NET.Testing</c>. Adding a fixture that starts a
    /// container is one edit here.
    /// </summary>
    private static readonly string[] ContainerFixtureNames =
        ["PgVectorFixture", "QdrantFixture", OllamaFixtureName];

    private TestProject(string name, string directory, string relativePath, XDocument project, ISet<string> solutionProjects)
    {
        var fixtures = MentionedContainerFixtures(directory, project);

        Name = name;
        RelativePath = relativePath;
        IsInTheSolution = solutionProjects.Contains(relativePath);
        DeclaresRequiresDocker = DeclaresTrue(project, "RequiresDocker");
        DeclaresRequiresLlm = DeclaresTrue(project, "RequiresLlm");
        DeclaresRequiresSecrets = DeclaresTrue(project, "RequiresSecrets");
        ReferencesTestcontainers = HasTestcontainersPackage(project);
        UsesAContainerFixture = fixtures.Count > 0;
        UsesTheOllamaFixture = fixtures.Contains(OllamaFixtureName);
        ReadsASecretEnvironmentVariable = ReadsARagnetEnvironmentVariable(directory);
    }

    /// <summary>Gets the project's directory name, which is also its assembly name.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the csproj path relative to the repository root, with forward slashes — the same shape
    /// <c>Rag.NET.slnx</c> writes.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>Gets a value indicating whether <c>Rag.NET.slnx</c> lists this project.</summary>
    public bool IsInTheSolution { get; }

    /// <summary>Gets a value indicating whether the csproj declares <c>RequiresDocker</c>.</summary>
    public bool DeclaresRequiresDocker { get; }

    /// <summary>Gets a value indicating whether the csproj declares <c>RequiresLlm</c>.</summary>
    public bool DeclaresRequiresLlm { get; }

    /// <summary>Gets a value indicating whether the csproj declares <c>RequiresSecrets</c>.</summary>
    public bool DeclaresRequiresSecrets { get; }

    /// <summary>Gets a value indicating whether the csproj references a Testcontainers package.</summary>
    public bool ReferencesTestcontainers { get; }

    /// <summary>Gets a value indicating whether the project uses a container fixture from Rag.NET.Testing.</summary>
    public bool UsesAContainerFixture { get; }

    /// <summary>Gets a value indicating whether the project uses Rag.NET.Testing's Ollama fixture.</summary>
    public bool UsesTheOllamaFixture { get; }

    /// <summary>
    /// Gets a value indicating whether any source file in the project reads a <c>RAGNET_</c>
    /// environment variable.
    /// </summary>
    public bool ReadsASecretEnvironmentVariable { get; }

    /// <summary>Gets a value indicating whether this project starts a container when its tests run.</summary>
    public bool StartsAContainer => ReferencesTestcontainers || UsesAContainerFixture;

    /// <summary>Gets a human-readable account of why <see cref="StartsAContainer"/> is what it is.</summary>
    public string ContainerEvidence => StartsAContainer
        ? ReferencesTestcontainers
            ? "it references a Testcontainers package"
            : "it uses a container fixture from Rag.NET.Testing"
        : "it references no Testcontainers package and uses no container fixture from Rag.NET.Testing";

    /// <summary>Gets a human-readable account of why <see cref="UsesTheOllamaFixture"/> is what it is.</summary>
    public string LlmEvidence => UsesTheOllamaFixture
        ? $"it uses {OllamaFixtureName}"
        : $"it does not use {OllamaFixtureName}";

    /// <summary>Gets a human-readable account of why <see cref="ReadsASecretEnvironmentVariable"/> is what it is.</summary>
    public string SecretEvidence => ReadsASecretEnvironmentVariable
        ? "a source file reads a RAGNET_ environment variable"
        : "no source file reads a RAGNET_ environment variable";

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

    /// <summary>Gets the path of a workflow file under <c>.github/workflows</c>.</summary>
    /// <param name="fileName">The workflow file name, for example <c>ci.yml</c>.</param>
    /// <returns>The absolute path, whether or not the file exists.</returns>
    public static string WorkflowPath(string fileName) =>
        Path.Combine(FindRepositoryRoot(), ".github", "workflows", fileName);

    /// <summary>
    /// Enumerates every C# source file under one top-level directory of the repository, skipping
    /// build output — the one shared walk for every guard that scans raw source
    /// (<see cref="TestGateTests"/>, <see cref="DocumentedConstraintGuardTests"/>,
    /// <see cref="InertValidatorGuardTests"/>), so the guards cannot disagree about which files
    /// exist.
    /// </summary>
    /// <param name="topDirectory">The top-level directory to walk, <c>src</c> or <c>tests</c>.</param>
    /// <returns>Repository-relative (forward-slash) and absolute paths, in directory order.</returns>
    public static IEnumerable<(string RelativePath, string FullPath)> EnumerateSourceFiles(string topDirectory)
    {
        var root = FindRepositoryRoot();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, topDirectory), "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = NormalisePath(Path.GetRelativePath(root, file));
            if (!relativePath.Contains("/bin/", StringComparison.Ordinal) &&
                !relativePath.Contains("/obj/", StringComparison.Ordinal))
            {
                yield return (relativePath, file);
            }
        }
    }

    /// <summary>
    /// Reads a workflow file as the commands it will run: comment lines removed, shell line
    /// continuations joined, runs of whitespace collapsed to one space.
    /// </summary>
    /// <param name="workflowPath">The absolute path of the workflow file.</param>
    /// <returns>The workflow's non-comment text on a single line.</returns>
    /// <remarks>
    /// Load-bearing, and the reason is a measured failure. The guard tests used to assert
    /// <c>Contains("RequiresDocker")</c> over the raw file, and that string appears four times in
    /// <c>ci.yml</c>'s prose. Replacing the entire tier selection with a hardcoded list of project
    /// names — the one thing these tests exist to catch — left the comments untouched and the suite
    /// green. Prose cannot satisfy an assertion about what runs.
    /// </remarks>
    public static string ReadWorkflowCommands(string workflowPath)
    {
        var builder = new StringBuilder();

        foreach (var line in File.ReadLines(workflowPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            // A trailing backslash continues a shell command onto the next line. Dropping it lets an
            // assertion name a whole pipeline as one string instead of as YAML-wrapped fragments.
            if (trimmed[^1] == '\\')
            {
                trimmed = trimmed[..^1];
            }

            _ = builder.Append(trimmed).Append(' ');
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Discovers every test project under <c>tests/</c>.</summary>
    /// <returns>The discovered projects, in directory order.</returns>
    public static IReadOnlyList<TestProject> DiscoverAll()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testsDirectory = Path.Combine(repositoryRoot, "tests");
        var solutionProjects = ReadSolutionProjectPaths(repositoryRoot);
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
                    var relativePath = NormalisePath(Path.GetRelativePath(repositoryRoot, projectFile));
                    projects.Add(new TestProject(
                        Path.GetFileName(directory), directory, relativePath, document, solutionProjects));
                }
            }
        }

        return projects;
    }

    /// <summary>
    /// Finds every project under <c>src/</c> that <c>Rag.NET.slnx</c> does not list.
    /// </summary>
    /// <returns>The missing csproj paths relative to the repository root, or an empty list.</returns>
    /// <remarks>
    /// The sibling of <c>EveryTestProjectIsInTheSolution</c>, for the half of the tree that guard
    /// cannot see. A source project outside the solution still compiles — anything referencing it
    /// pulls it in transitively — so nothing looks wrong, which is exactly how
    /// <c>src/Rag.NET.WebSearch.Tavily</c> stayed out of the solution alongside its test project.
    /// <para>
    /// It stops looking harmless at Phase 4.1: <c>dotnet pack</c> over the solution packs the
    /// projects the solution lists, so a missing one is a package that silently never ships. That is
    /// the same shape as a test suite that silently never runs, and it is cheaper to guard now than
    /// to discover from an absent package on a release day.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SourceProjectsMissingFromTheSolution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var solutionProjects = ReadSolutionProjectPaths(repositoryRoot);
        var missing = new List<string>();

        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(repositoryRoot, "src")))
        {
            foreach (var projectFile in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                var relativePath = NormalisePath(Path.GetRelativePath(repositoryRoot, projectFile));
                if (!solutionProjects.Contains(relativePath))
                {
                    missing.Add(relativePath);
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Reads every <c>&lt;Project Path="…"/&gt;</c> out of <c>Rag.NET.slnx</c>.
    /// </summary>
    /// <remarks>
    /// A project absent from the solution is never built by <c>dotnet build Rag.NET.slnx</c>, and on
    /// a fresh checkout that makes the workflow's <c>dotnet test --no-build</c> exit 0 having run
    /// nothing at all. Read from the file rather than from an SDK query because the workflow's
    /// build reads this same file, and it is the file that must be right.
    /// </remarks>
    private static HashSet<string> ReadSolutionProjectPaths(string repositoryRoot)
    {
        var solution = XDocument.Load(Path.Combine(repositoryRoot, SolutionFileName));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Descendants("Project"))
        {
            var path = project.Attribute("Path")?.Value;
            if (path is not null)
            {
                _ = paths.Add(NormalisePath(path));
            }
        }

        return paths;
    }

    private static string NormalisePath(string path) => path.Replace('\\', '/').Trim();

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

    private static HashSet<string> MentionedContainerFixtures(string directory, XDocument project)
    {
        var mentioned = new HashSet<string>(StringComparer.Ordinal);

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
            return mentioned;
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
                    _ = mentioned.Add(fixtureName);
                }
            }
        }

        return mentioned;
    }

    private static bool ReadsARagnetEnvironmentVariable(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file, directory))
            {
                continue;
            }

            if (SecretEnvironmentVariableRead().IsMatch(File.ReadAllText(file)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a source-level read of a <c>RAGNET_</c> environment variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The call, not the name. Searching for the bare text <c>RAGNET_</c> would flag
    /// <c>Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests</c> for a doc comment that merely
    /// cross-references the <c>RAGNET_TESSDATA</c> precedent in another suite, and it would flag
    /// the CI documentation page and this file's own comments too. A project that documents a
    /// variable does not need it supplied.
    /// </para>
    /// <para>
    /// Load-bearing detail: the escapes make this pattern unable to match its own source text.
    /// The regex requires a literal <c>.</c> after <c>Environment</c>, and the source here has
    /// <c>\.</c> — so the conventions project does not conclude that it reads secrets and demand
    /// it declare <c>RequiresSecrets</c>. The same trap that <c>MentionsAContainerFixture</c>
    /// documents, avoided a different way because there is no project reference to gate on.
    /// </para>
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"Environment\.GetEnvironmentVariable\(\s*""RAGNET_", RegexOptions.NonBacktracking)]
    private static partial Regex SecretEnvironmentVariableRead();

    private static bool IsBuildOutput(string file, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, file);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        return string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase);
    }
}
