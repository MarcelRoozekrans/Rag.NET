using System.Text;
using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Pins ci.yml's pack-validate wiring, unconditionally — this is what stops the skip gate in
/// <see cref="ProducedPackageTests"/> from rotting into permanent green.
/// </summary>
/// <remarks>
/// The package checks skip when <c>artifacts/packages</c> does not exist, which is correct on the
/// matrix legs and on a fresh checkout — but a skip gate whose provisioning step someone deletes
/// or renames skips everywhere, forever, while looking like coverage. This test runs in the fast
/// tier on every push, so the moment ci.yml stops packing into the directory the package checks
/// read, or stops running this project after packing, a gating job goes red. Asserted against the
/// workflow's commands with comment lines stripped, the same way and for the same measured reason
/// as <c>TestProjectTierTests</c>: prose must not satisfy an assertion about what runs.
/// </remarks>
public sealed class WorkflowWiringTests
{
    [Fact]
    public void TheWorkflowPacksAndThenValidatesWhatItProduced()
    {
        var workflow = Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), ".github", "workflows", "ci.yml");

        Assert.True(
            File.Exists(workflow),
            $"'{workflow}' does not exist. The package validation in this project only ever " +
            "runs if a workflow packs first; without ci.yml nothing does.");

        var commands = ReadWorkflowCommands(workflow);

        Assert.Contains(
            "dotnet pack Rag.NET.slnx -c Release -o artifacts/packages",
            commands,
            StringComparison.Ordinal);

        Assert.Contains(
            "dotnet test tests/Rag.NET.PackageValidation.Tests -c Release",
            commands,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflowRehearsesThePushAgainstALocalFeed()
    {
        // The push to nuget.org cannot run before Phase 6.3, which makes it exactly the inert
        // path this repository keeps finding defects in — so ci.yml pushes every produced
        // package to a local directory feed on every run, twice, and asserts arrival. This test
        // is what stops that rehearsal from being deleted while the real push stays gated: lose
        // the rehearsal and the first execution of `dotnet nuget push` is the release itself.
        var commands = ReadWorkflowCommands(Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), ".github", "workflows", "ci.yml"));

        // The exact command 6.3 runs, minus credential and endpoint: the quoted glob NuGet
        // expands itself, and --skip-duplicate, the deliberate duplicate policy — nuget.org
        // never forgets a version, so a partial push must be re-runnable without failing on
        // what already arrived.
        Assert.Contains(
            "dotnet nuget push \"artifacts/packages/*.nupkg\" --source \"$feed\" --skip-duplicate",
            commands,
            StringComparison.Ordinal);

        // The .snupkg push is a measured silent no-op against a directory feed (exit 0, nothing
        // delivered, 2026-08-03); the workflow attempts it and asserts non-arrival so the day
        // NuGet changes that, the run fails and the rehearsal widens. This pin keeps the attempt
        // itself from being removed as "does nothing anyway" — doing nothing loudly is its job.
        Assert.Contains(
            "dotnet nuget push \"artifacts/packages/*.snupkg\" --source \"$feed\" --skip-duplicate",
            commands,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a workflow file as the commands it will run: comment lines removed, shell line
    /// continuations joined, runs of whitespace collapsed to one space. Duplicated from
    /// <c>Rag.NET.RepoConventions.Tests.TestProject</c> deliberately — this project references
    /// nothing in the repository; see the csproj comment.
    /// </summary>
    /// <param name="workflowPath">The absolute path of the workflow file.</param>
    /// <returns>The workflow's non-comment text on a single line.</returns>
    private static string ReadWorkflowCommands(string workflowPath)
    {
        var builder = new StringBuilder();

        foreach (var line in File.ReadLines(workflowPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed[^1] == '\\')
            {
                trimmed = trimmed[..^1];
            }

            _ = builder.Append(trimmed).Append(' ');
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
