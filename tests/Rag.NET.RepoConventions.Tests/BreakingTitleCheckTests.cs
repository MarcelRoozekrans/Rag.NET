using System.Diagnostics;
using System.Text;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Executes <c>.github/scripts/check-breaking-title.sh</c> and asserts when it demands a breaking
/// pull request title.
/// </summary>
/// <remarks>
/// <b>Why a title.</b> A compatibility suppression records a deliberate break. release-please
/// derives the version bump from the title, so a break under a non-breaking title would ship as a
/// minor or patch release. The script is run, not read: a check that reads the wrong status or
/// quotes the title badly would pass everything.
/// </remarks>
public sealed class BreakingTitleCheckTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>An added suppression file under a non-breaking title fails and is named.</summary>
    [Fact]
    public async Task ASuppressionAddedUnderANonBreakingTitleFails()
    {
        var result = await RunAsync("feat(core): x", "added\tsrc/Foo/CompatibilitySuppressions.1.x.xml");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("::error::", result.Output, StringComparison.Ordinal);
        Assert.Contains("src/Foo/CompatibilitySuppressions.1.x.xml", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A breaking title, scoped or not, satisfies the check.</summary>
    [Theory]
    [InlineData("feat(core)!: x")]
    [InlineData("feat!: x")]
    public async Task ASuppressionAddedUnderABreakingTitlePasses(string title)
    {
        var result = await RunAsync(title, "added\tsrc/Foo/CompatibilitySuppressions.1.x.xml");

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>Deleting a suppression removes a recorded break, so it needs no breaking title.</summary>
    [Fact]
    public async Task ARemovedSuppressionNeedsNoBreakingTitle()
    {
        var result = await RunAsync(
            "chore: drop stale suppressions", "removed\tsrc/Foo/CompatibilitySuppressions.1.x.xml");

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>Editing, moving or copying a suppression file changes what is suppressed.</summary>
    [Theory]
    [InlineData("modified")]
    [InlineData("renamed")]
    [InlineData("copied")]
    [InlineData("changed")]
    public async Task AnyStatusExceptRemovedAndUnchangedCountsAsAChange(string status)
    {
        var result = await RunAsync("fix: x", $"{status}\tsrc/Foo/CompatibilitySuppressions.2.x.xml");

        Assert.NotEqual(0, result.ExitCode);
    }

    /// <summary>An unchanged file has no content change, so it never requires a breaking title.</summary>
    [Fact]
    public async Task AnUnchangedSuppressionNeedsNoBreakingTitle()
    {
        var result = await RunAsync("fix: x", "unchanged\tsrc/Foo/CompatibilitySuppressions.1.x.xml");

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>The SDK's default file name is a suppression file too, so a break cannot hide in it.</summary>
    [Fact]
    public async Task TheDefaultNamedSuppressionFileCountsToo()
    {
        var result = await RunAsync("fix: x", "added\tsrc/Foo/CompatibilitySuppressions.xml");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("src/Foo/CompatibilitySuppressions.xml", result.Output, StringComparison.Ordinal);
    }

    /// <summary>Other files, even ones with a similar name, never require a breaking title.</summary>
    [Theory]
    [InlineData("added\tsrc/Foo/Bar.cs")]
    [InlineData("added\tsrc/Foo/CompatibilitySuppressions.md")]
    [InlineData("added\tdocs/NotCompatibilitySuppressions.1.x.xml")]
    public async Task UnrelatedFilesNeverRequireIt(string line)
    {
        var result = await RunAsync("fix: x", line);

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>The title is data: shell metacharacters in it are neither run nor mistaken for a break.</summary>
    [Fact]
    public async Task AHostileTitleIsTreatedAsData()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"ragnet-title-{Guid.NewGuid():N}");
        try
        {
            var result = await RunAsync(
                $"fix: $(touch {marker}) `touch {marker}`; x!: y",
                "added\tsrc/Foo/CompatibilitySuppressions.1.x.xml");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(marker), "The title was executed.");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>Editing the title alone must re-run the check, and the title must not be spliced into a shell command.</summary>
    [Fact]
    public void TheWorkflowRerunsWhenOnlyTheTitleChanges()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        var typesStart = workflow.IndexOf("types: [", StringComparison.Ordinal);
        Assert.True(typesStart >= 0, "The workflow lists no pull_request types.");
        var types = workflow[typesStart..workflow.IndexOf(']', typesStart)];
        foreach (var type in new[] { "edited", "opened", "synchronize", "reopened" })
        {
            Assert.Contains(type, types, StringComparison.Ordinal);
        }

        Assert.Contains(".github/scripts/check-breaking-title.sh", workflow, StringComparison.Ordinal);
        Assert.Equal(["contents: read", "pull-requests: read"], PermissionEntries());
        Assert.DoesNotContain("write", workflow, StringComparison.Ordinal);
        Assert.Contains("TITLE: ${{ github.event.pull_request.title }}", workflow, StringComparison.Ordinal);
        Assert.Equal(2, workflow.Split("permissions:").Length); // declared once, at workflow level
        var run = RunBlocks();
        Assert.Contains("gh api", run, StringComparison.Ordinal);
        Assert.Contains("check-breaking-title.sh", run, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event.pull_request.title", run, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", run, StringComparison.Ordinal);
    }

    /// <summary>Reads the workflow as one line, comments stripped.</summary>
    private static string ReadWorkflow()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), ".github", "workflows", "api-compat-title.yml");
        Assert.True(File.Exists(path), $"'{path}' does not exist.");
        var lines = File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] != '#');
        return string.Join(' ', lines);
    }

    /// <summary>The entries of the single <c>permissions:</c> block, sorted, comments stripped.</summary>
    private static string[] PermissionEntries()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), ".github", "workflows", "api-compat-title.yml");
        var entries = new List<string>();
        var indent = -1;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var depth = line.Length - trimmed.Length;
            if (indent >= 0 && depth <= indent)
            {
                indent = -1;
            }

            if (indent >= 0)
            {
                var hash = trimmed.IndexOf(" #", StringComparison.Ordinal);
                entries.Add((hash >= 0 ? trimmed[..hash] : trimmed).Trim());
            }
            else if (trimmed.StartsWith("permissions:", StringComparison.Ordinal))
            {
                indent = depth;
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return [.. entries];
    }

    /// <summary>The text of every <c>run:</c> block, where an expression would be spliced into shell source.</summary>
    private static string RunBlocks()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), ".github", "workflows", "api-compat-title.yml");
        var builder = new StringBuilder();
        var indent = -1;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            var depth = line.Length - trimmed.Length;
            if (indent >= 0 && trimmed.Length > 0 && depth <= indent)
            {
                indent = -1;
            }

            if (indent >= 0)
            {
                _ = builder.AppendLine(line);
            }
            else if (trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                indent = depth;
                _ = builder.AppendLine(trimmed);
            }
        }

        return builder.ToString();
    }

    private static async Task<ScriptResult> RunAsync(string title, string filesLine)
    {
        var script = Path.Combine(TestProject.FindRepositoryRoot(), ".github", "scripts", "check-breaking-title.sh");
        Assert.True(File.Exists(script), $"'{script}' does not exist.");

        var startInfo = new ProcessStartInfo(FindShell())
        {
            WorkingDirectory = TestProject.FindRepositoryRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(title);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null for bash.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(filesLine + "\n");
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(RunTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"check-breaking-title.sh did not exit within {RunTimeout.TotalSeconds:0}s.");
        }

        return new ScriptResult(process.ExitCode, await stdout + await stderr);
    }

    private static string FindShell()
    {
        string[] candidates =
        [
            "/bin/bash",
            "/usr/bin/bash",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No bash found to execute .github/scripts/check-breaking-title.sh. Looked in: " +
            string.Join(", ", candidates) + ".");
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
