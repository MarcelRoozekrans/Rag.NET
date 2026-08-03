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
