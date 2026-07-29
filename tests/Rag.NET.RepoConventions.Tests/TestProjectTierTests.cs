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
    /// There are 63 test projects today. A far smaller number means the scan lost the working tree
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

    private static string MismatchMessage(TestProject project) => project.StartsAContainer
        ? $"{project.Name} starts a container ({project.ContainerEvidence}) but does not declare " +
            "<RequiresDocker>true</RequiresDocker>. Without it CI runs the project in the fast tier, " +
            "where there is no Docker daemon."
        : $"{project.Name} declares <RequiresDocker>true</RequiresDocker> but starts no container: " +
            $"{project.ContainerEvidence}. It would occupy the slow Docker tier for nothing, or the " +
            "declaration is stale and hiding that the project stopped testing what it used to.";
}
