using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every test project's declared CI tier matches what the project actually does.
/// </summary>
/// <remarks>
/// A test project in no tier runs nowhere, and that failure is invisible: CI stays green because
/// nothing ran. Every other defect this repository has produced announced itself somehow; this one
/// looks like success, so it needs a test of its own.
/// </remarks>
public sealed class TestProjectTierTests
{
    /// <summary>
    /// There are 64 test projects today. A far smaller number means the scan lost the working tree
    /// and is asserting over nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausibleTestProjects = 50;

    [Fact]
    public void TheScanFindsEveryTestProjectInTheRepository()
    {
        var projects = TestProject.DiscoverAll();

        Assert.True(
            projects.Count >= FewestPlausibleTestProjects,
            $"Found only {projects.Count} test projects under tests/, expected at least " +
            $"{FewestPlausibleTestProjects}. A conventions test that scans nothing passes for the " +
            "wrong reason, so this fails instead.");
    }

    [Fact]
    public void RequiresLlmImpliesRequiresDocker()
    {
        // OllamaFixture is a container. An LLM project that does not need Docker is a contradiction,
        // and it would land in the fast tier, where there is no daemon to run it.
        foreach (var project in TestProject.DiscoverAll())
        {
            Assert.False(
                project.DeclaresRequiresLlm && !project.DeclaresRequiresDocker,
                $"{project.Name} declares <RequiresLlm>true</RequiresLlm> without " +
                "<RequiresDocker>true</RequiresDocker>. The LLM fixtures are containers, so the " +
                "project needs both or it will be scheduled into a tier that cannot run it.");
        }
    }

    [Fact]
    public void EveryProjectThatStartsAContainerDeclaresRequiresDocker()
    {
        // Both directions in one assertion. The obvious version of this test — grep the csproj for
        // "Testcontainers" — is wrong twice over: three projects start containers through
        // Rag.NET.Testing's fixtures and reference no Testcontainers package of their own, and
        // Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests references Rag.NET.Testing for WireMock
        // cassettes while starting no container at all.
        foreach (var project in TestProject.DiscoverAll())
        {
            Assert.True(
                project.StartsAContainer == project.DeclaresRequiresDocker,
                MismatchMessage(project));
        }
    }

    [Fact]
    public void EveryProjectThatReadsASecretDeclaresRequiresSecrets()
    {
        // Both directions, the same shape as the container guard above, and it exists because the
        // gap it closes was invisible in exactly the way this phase is about. Every env-gated test
        // in this repository lives in a fast-tier project, so supplying RAGNET_* secrets to the LLM
        // job reached nothing: the variables were set on a job that runs one project, and that
        // project reads none of them. Nothing failed. The tests simply skipped, forever, and a
        // green run said so in a way nobody would read as a gap.
        //
        // The reverse direction matters just as much: a project that declares RequiresSecrets and
        // has stopped reading the variable pulls credentials into a job for nothing, and hides that
        // the coverage it was declared for no longer exists.
        foreach (var project in TestProject.DiscoverAll())
        {
            Assert.True(
                project.ReadsASecretEnvironmentVariable == project.DeclaresRequiresSecrets,
                SecretMismatchMessage(project));
        }
    }

    [Fact]
    public void TheWorkflowSelectsOnThePropertyNotAHardcodedList()
    {
        // Guards the mechanism itself. If someone replaces the property query with a list of project
        // names, drift becomes silent again and the two tests above become decorative: they would
        // keep asserting about a property nothing reads.
        var workflow = Path.Combine(TestProject.FindRepositoryRoot(), ".github", "workflows", "ci.yml");

        // The workflows arrive in Part B of this phase. Skipping rather than failing lets this test
        // land green now and start guarding the moment ci.yml exists, without a period where the
        // suite is red for work that has not been done yet.
        Assert.SkipWhen(
            !File.Exists(workflow),
            $"'{workflow}' does not exist yet — CI workflows land in Phase 3.5 Part B. This test " +
            "begins guarding automatically once it does.");

        var yaml = File.ReadAllText(workflow);

        Assert.Contains("RequiresDocker", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNightlyWorkflowSelectsTheSecretsJobOnTheProperty()
    {
        // The same mechanism guard as above, for the second property. A job that names
        // Rag.NET.Parsers.Pdf.Tests and its two siblings directly would work today and rot the
        // first time a fourth suite becomes env-gated — which is precisely how the gap this
        // property closes came about.
        var workflow = Path.Combine(TestProject.FindRepositoryRoot(), ".github", "workflows", "nightly.yml");

        Assert.SkipWhen(
            !File.Exists(workflow),
            $"'{workflow}' does not exist yet. This test begins guarding automatically once it does.");

        var yaml = File.ReadAllText(workflow);

        Assert.Contains("RequiresSecrets", yaml, StringComparison.Ordinal);
    }

    private static string SecretMismatchMessage(TestProject project) => project.ReadsASecretEnvironmentVariable
        ? $"{project.Name} reads a RAGNET_ environment variable but does not declare " +
            "<RequiresSecrets>true</RequiresSecrets>. Without it nightly.yml never selects the " +
            "project, the variable is never set, and the test skips on every automated run there is."
        : $"{project.Name} declares <RequiresSecrets>true</RequiresSecrets> but {project.SecretEvidence}. " +
            "It would pull credentials into the nightly job for nothing, or the declaration is stale " +
            "and hiding that the env-gated coverage it stood for has gone.";

    private static string MismatchMessage(TestProject project) => project.StartsAContainer
        ? $"{project.Name} starts a container ({project.ContainerEvidence}) but does not declare " +
            "<RequiresDocker>true</RequiresDocker>. Without it CI runs the project in the fast tier, " +
            "where there is no Docker daemon."
        : $"{project.Name} declares <RequiresDocker>true</RequiresDocker> but starts no container: " +
            $"{project.ContainerEvidence}. It would occupy the slow Docker tier for nothing, or the " +
            "declaration is stale and hiding that the project stopped testing what it used to.";
}
