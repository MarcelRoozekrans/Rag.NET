using System.Diagnostics;
using System.Text;
using Xunit;

namespace Rag.NET.Cli.Tests.Integration;

/// <summary>
/// Runs the shipped <c>ragnet</c> binary as a real process and asserts on its real stdout, stderr
/// and exit code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (§2(d) of the phase design).</b> Every other test in this project
/// exercises <see cref="CliArguments"/>, <see cref="CliOutput"/>, <c>IngestCommand</c> and
/// <c>QueryCommand</c> directly, against a <c>FakeRagPipeline</c>. <b>Nothing executed
/// <c>Program.cs</c>.</b> That is where the CLI's composition lives — argument parsing feeding
/// command dispatch, the exit code each path returns, and which stream each message is written to —
/// and composition is where every hosted-surface defect this repository has found actually lived.
/// </para>
/// <para>
/// The four cases below are deliberately the ones that need no model, no store and no network: the
/// point is to prove the binary starts, routes and reports, not to re-test retrieval. A CLI that
/// returned 0 on an unknown command, or wrote its usage to stdout when it failed, would break every
/// script that consumes it, and no test in this repository would have noticed.
/// </para>
/// <para>
/// Invoked through <c>dotnet exec</c> against the copied <c>Rag.NET.Cli.dll</c> rather than the
/// native apphost, because the apphost is <c>Rag.NET.Cli.exe</c> on Windows and extensionless on
/// Linux, and this suite runs on both.
/// </para>
/// </remarks>
public sealed class CliProcessTests
{
    private static string CliAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "Rag.NET.Cli.dll");

    [Fact]
    public void TheBinaryExists_WhereTheseTestsExpectIt()
    {
        // Guards the suite itself: if the project reference stops copying the CLI next to these
        // tests, every assertion below would fail for a reason that has nothing to do with the CLI.
        Assert.True(
            File.Exists(CliAssemblyPath),
            $"Rag.NET.Cli.dll was not found at {CliAssemblyPath}. The ProjectReference in " +
            "Rag.NET.Cli.Tests.csproj is what puts it there.");
    }

    [Fact]
    public async Task Help_ExitsZero_AndWritesUsageToStdout()
    {
        var run = await RunAsync("--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("ragnet - command-line access to a configured Rag.NET pipeline.", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Usage:", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("ragnet ingest <path>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("ragnet query <question>", run.Stdout, StringComparison.Ordinal);

        // Help is a success, so it must not pollute stderr — a script doing `ragnet --help 2>&1`
        // into a log should see nothing alarming.
        Assert.True(string.IsNullOrWhiteSpace(run.Stderr), $"stderr was not empty: {run.Stderr}");
    }

    /// <remarks>
    /// <b>Bare <c>ragnet</c> is help, not an error</b> — <c>CliArguments.Parse</c> treats
    /// <c>args.Length == 0</c> as <c>ShowHelp</c>, and <c>Program.cs</c> tests <c>ShowHelp</c>
    /// before anything else. This test asserted exit 1 when it was written, on the assumption that
    /// no command is a usage error; running the real binary said otherwise, and the binary is right.
    /// </remarks>
    [Fact]
    public async Task NoArguments_IsTreatedAsHelp_AndExitsZero()
    {
        var run = await RunAsync();

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Usage:", run.Stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(run.Stderr), $"stderr was not empty: {run.Stderr}");
    }

    /// <remarks>
    /// <para>
    /// <b>Was #260's finding; now the test that keeps its resolution honest.</b> <c>Program.cs</c>
    /// carried a branch writing <c>"No command given."</c> when <c>parsed.Command</c> was null.
    /// <c>CliArguments.Parse</c> returns a null <c>Command</c> in exactly one case —
    /// <c>args.Length == 0 || IsHelpFlag(args[0])</c> — and that same case sets <c>ShowHelp: true</c>,
    /// which <c>Program.cs</c> handles first by printing usage and returning 0. Nothing could reach
    /// the message, so nobody could ever read it. The branch is gone.
    /// </para>
    /// <para>
    /// Neither <c>CliArgumentsTests</c> nor <c>CliOutputTests</c> could have found it: both are
    /// correct about the unit they test. The dead branch was a property of the <i>composition</i> —
    /// the order in which <c>Program.cs</c> tested two flags against the contract <c>Parse</c>
    /// actually offers — which only running the real binary exposes. That is the argument for
    /// §2(d)'s "real transport, not a direct call", arriving in the first package it was applied to.
    /// </para>
    /// <para>
    /// <b>What this test now guards is the behaviour, which did not change.</b> Bare <c>ragnet</c>
    /// prints usage and exits 0; <c>ragnet ""</c> is an unknown command and exits 1. Those two facts
    /// are the reason removing the branch was correct rather than a behaviour change dressed as a
    /// cleanup — had bare invocation been meant to be a usage error, the fix would have been to make
    /// the message reachable instead.
    /// </para>
    /// <para>
    /// The <c>"No command given."</c> assertions are kept. The string no longer exists in the source,
    /// so they cannot fail today — which is the point: if someone reintroduces that branch, these are
    /// what say so.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BareInvocationPrintsUsageAndExitsZero_AndEmptyStringIsAnUnknownCommand()
    {
        // Every way of reaching Program.cs with no usable command. All print usage and succeed.
        foreach (var argv in new[] { Array.Empty<string>(), new[] { "--help" }, new[] { "-h" } })
        {
            var run = await RunAsync(argv);
            Assert.DoesNotContain("No command given.", run.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, run.ExitCode);
        }

        // The nearest neighbour, and the case that proves the surviving branch still fires.
        var empty = await RunAsync("");
        Assert.Equal(1, empty.ExitCode);
        Assert.Contains("Unknown command", empty.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("No command given.", empty.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_ExitsOne_AndNamesTheCommandItRejected()
    {
        var run = await RunAsync("not-a-command");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Unknown command 'not-a-command'.", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_ExitsOne_AndSaysItIsNotImplementedRatherThanPretending()
    {
        var run = await RunAsync("evaluate");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("'evaluate' is not implemented.", run.Stderr, StringComparison.Ordinal);
        // The deferral is a documented decision, not a stub that silently succeeds.
        Assert.Equal(string.Empty, run.Stdout.Trim());
    }

    private static async Task<ProcessRun> RunAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // A working directory with no appsettings.json, so configuration binding sees the same
            // empty state on every machine.
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(CliAssemblyPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        Assert.True(process.Start(), "the ragnet process did not start");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return new ProcessRun(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record ProcessRun(int ExitCode, string Stdout, string Stderr);
}
