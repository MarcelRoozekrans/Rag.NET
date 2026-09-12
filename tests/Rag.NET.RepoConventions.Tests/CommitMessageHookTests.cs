using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Executes <c>.githooks/commit-msg</c> and asserts what it accepts and rejects.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hook is run, not read.</b> A test that greps the script for "100" would pass against a
/// hook that never rejects anything. This phase exists because rules that are written down are not
/// checked at the moment they apply, and a guard nobody executes is the same failure one level up.
/// </para>
/// <para>
/// <b>What this cannot test is adoption.</b> The hook does nothing until someone runs
/// <c>git config core.hooksPath .githooks</c> in their clone. That is a stated cost of the chosen
/// delivery mechanism, recorded in the design, not an oversight here.
/// </para>
/// </remarks>
public sealed class CommitMessageHookTests
{
    private const int Cap = 100;

    /// <summary>A header one character over the cap must be rejected.</summary>
    [Fact]
    public void AHeaderOverTheCapIsRejected()
    {
        var header = "chore: " + new string('x', Cap + 1 - "chore: ".Length);
        Assert.Equal(Cap + 1, header.Length);

        var (exitCode, output) = RunHook(header);

        Assert.True(
            exitCode != 0,
            $"The hook accepted a {header.Length}-character header. commitlint caps headers at " +
            $"{Cap} and lints every commit a pull request adds, so this one fails CI after the " +
            "push — which is the cost the hook exists to remove.");

        Assert.Contains(Cap.ToString(CultureInfo.InvariantCulture), output, StringComparison.Ordinal);
    }

    /// <summary>A header exactly at the cap must be accepted.</summary>
    [Fact]
    public void AHeaderExactlyAtTheCapIsAccepted()
    {
        var header = "chore: " + new string('x', Cap - "chore: ".Length);
        Assert.Equal(Cap, header.Length);

        var (exitCode, output) = RunHook(header);

        Assert.True(
            exitCode == 0,
            $"The hook rejected a header of exactly {Cap} characters, which commitlint accepts. " +
            $"Output: {output}");
    }

    /// <summary>
    /// A 100-character header containing em dashes must be accepted — the byte-counting trap.
    /// </summary>
    /// <remarks>
    /// An em dash is one character and three UTF-8 bytes, and this repository's headers use them.
    /// A hook measuring bytes rejects this header while reporting a length of 104, which looks
    /// exactly like a correct guard.
    /// </remarks>
    [Fact]
    public void AHeaderAtTheCapContainingEmDashesIsAccepted()
    {
        const string prefix = "docs(plans): scope — a phase — with em dashes ";
        var header = prefix + new string('x', Cap - prefix.Length);
        Assert.Equal(Cap, header.Length);

        var (exitCode, output) = RunHook(header);

        Assert.True(
            exitCode == 0,
            "The hook rejected a 100-CHARACTER header containing em dashes, so it is counting " +
            $"bytes rather than characters. commitlint counts characters. Output: {output}");
    }

    /// <summary>Leading git-generated comment lines must not be mistaken for the header.</summary>
    [Fact]
    public void CommentLinesAreNotTreatedAsTheHeader()
    {
        var (exitCode, output) = RunHookWithRawMessage(
            "# a comment git put here\nchore: a short header\n");

        Assert.True(
            exitCode == 0,
            $"The hook measured a comment line instead of the header. Output: {output}");
    }

    private static (int ExitCode, string Output) RunHook(string header) =>
        RunHookWithRawMessage(header + "\n\nA body line.\n");

    private static (int ExitCode, string Output) RunHookWithRawMessage(string message)
    {
        var root = TestProject.FindRepositoryRoot();
        var hook = Path.Combine(root, ".githooks", "commit-msg");

        Assert.True(
            File.Exists(hook),
            $"'{hook}' does not exist. The hook is tracked in the repository so that a clone can " +
            "enable it with `git config core.hooksPath .githooks`.");

        var messageFile = Path.Combine(Path.GetTempPath(), $"ragnet-commit-msg-{Guid.NewGuid():N}");
        File.WriteAllText(messageFile, message, new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo(FindShell())
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(hook);
            psi.ArgumentList.Add(messageFile);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(messageFile);
        }
    }

    /// <summary>Locates a POSIX shell to run the hook with.</summary>
    /// <remarks>
    /// Both CI legs have one: ubuntu at /bin/bash, and the Windows runner ships Git Bash. This
    /// throws rather than skipping, because a guard that quietly stops running is the shape this
    /// phase exists to remove.
    /// </remarks>
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
            "No bash found to execute .githooks/commit-msg. Looked in: " +
            string.Join(", ", candidates) +
            ". Both CI legs provide one, so this means the test environment is not what it was " +
            "written against.");
    }
}
