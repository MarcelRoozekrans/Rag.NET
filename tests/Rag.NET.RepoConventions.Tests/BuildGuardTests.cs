using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Covers the repo-wide build guard in <c>Directory.Build.targets</c> that refuses a VSTest
/// <c>--filter</c> on a project Microsoft.Testing.Platform runs (#529, phase 6.2.35).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect being guarded is a silent no-op, not an error.</b> `dotnet test --filter` sets the
/// MSBuild property <c>VSTestTestCaseFilter</c>; under Microsoft.Testing.Platform the runner is
/// <c>InvokeTestingPlatform</c>, which does not read it. MTP warns (MTP0001) and runs the whole
/// assembly. Measured 2026-09-10 on the benchmark project: a filter naming one class ran
/// <b>267</b> tests, 149 of them for real.
/// </para>
/// <para>
/// <b>Why <c>dotnet msbuild</c> rather than <c>dotnet test</c>.</b> Three harnesses were measured
/// before choosing: <c>dotnet test --filter</c> takes 44.8s; adding <c>--no-build</c> takes 0.95s
/// but requires the benchmark project to be built already, which a run of THIS project has not
/// done; invoking the target by name takes <b>0.535s and needs no build at all</b>. The last is
/// what these tests use.
/// </para>
/// <para>
/// <b>It cannot reach the <c>BeforeTargets</c> hook</b>, because invoking a target by name bypasses
/// it — which is why <see cref="TheGuardIsHookedToTheTestingPlatformRunner"/> exists alongside
/// these. Deleting the hook leaves both behavioural tests green and the guard entirely inert.
/// </para>
/// </remarks>
public sealed class BuildGuardTests
{
    private const string TargetName = "RefuseVSTestFilterUnderTestingPlatform";
    private const string ErrorCode = "RAGNET0001";

    /// <summary>
    /// The project the guard exists for: the one setting <c>TestingPlatformDotnetTestSupport</c>,
    /// and the one holding every expensive cell — BEIR corpora, ONNX replay, graph and RAPTOR runs.
    /// </summary>
    private static string BenchmarkProject => Path.Combine(
        TestProject.FindRepositoryRoot(),
        "tests",
        "Rag.NET.Benchmarks.Quality.IntegrationTests",
        "Rag.NET.Benchmarks.Quality.IntegrationTests.csproj");

    private static string TargetsFile => Path.Combine(
        TestProject.FindRepositoryRoot(), "Directory.Build.targets");

    /// <summary>
    /// Asking for a filter on a Microsoft.Testing.Platform project fails the build rather than
    /// quietly running everything.
    /// </summary>
    /// <remarks>
    /// Asserts on the error <b>code</b>, not the message text. The message is prose and will be
    /// reworded; the code is the contract, and it is what a reader searches for.
    /// </remarks>
    [Fact]
    public void PassingAVSTestFilterToATestingPlatformProject_IsRefused()
    {
        var (exitCode, output) = RunTarget(withFilter: true);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(ErrorCode, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// And with no filter the guard says nothing at all.
    /// </summary>
    /// <remarks>
    /// <b>Not padding.</b> A guard whose condition was dropped would pass the test above and break
    /// every `dotnet test` in the repository — `Directory.Build.targets` is imported by all ~140
    /// projects, so an unconditional error here is a repo-wide outage. This is the test that makes
    /// the repo-wide placement safe.
    /// </remarks>
    [Fact]
    public void WithNoFilter_TheGuardIsSilent()
    {
        var (_, output) = RunTarget(withFilter: false);

        Assert.DoesNotContain(ErrorCode, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard is wired to the Microsoft.Testing.Platform runner, so it fires on a real
    /// <c>dotnet test</c> and not only when invoked by name.
    /// </summary>
    /// <remarks>
    /// <b>Structural on purpose, and not redundant.</b> The two tests above invoke the target
    /// directly, so they prove the condition and the message but say nothing about the hook.
    /// Removing <c>BeforeTargets</c> would leave them both green while the guard never runs — the
    /// same shape as 6.2.32's Weaviate corrupt-blob posture, which stood by deliberate review
    /// decision for six weeks with nothing covering it, and which any refactor could have reverted
    /// silently.
    /// </remarks>
    [Fact]
    public void TheGuardIsHookedToTheTestingPlatformRunner()
    {
        var target = XDocument.Load(TargetsFile)
            .Descendants()
            .Single(e => string.Equals(e.Name.LocalName, "Target", StringComparison.Ordinal)
                         && string.Equals((string?)e.Attribute("Name"), TargetName, StringComparison.Ordinal));

        Assert.Equal("InvokeTestingPlatform", (string?)target.Attribute("BeforeTargets"));
    }

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Invokes the guard's target and returns its exit code and combined output.
    /// </summary>
    /// <remarks>
    /// <b>Reads both streams asynchronously and bounds the wait, following
    /// <c>CliProcessTests.RunAsync</c>.</b> Reading one redirected stream to the end and then the
    /// other deadlocks if the child fills the second stream's buffer while we are blocked on the
    /// first — and an unbounded <c>WaitForExit</c> turns a hung child into a hung test run. Both
    /// are unlikely with one MSBuild target and <c>-nologo</c>. Neither is worth carrying in the
    /// guard for <c>TestingPlatformDotnetTestSupport</c>, which is in this repository **because of
    /// #275**, a deadlock in test infrastructure that hung 2 of 4 runs before entering test code.
    /// </remarks>
    private static (int ExitCode, string Output) RunTarget(bool withFilter)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(BenchmarkProject);
        startInfo.ArgumentList.Add($"-t:{TargetName}");
        startInfo.ArgumentList.Add("-p:Configuration=Release");
        startInfo.ArgumentList.Add("-nologo");
        if (withFilter)
        {
            startInfo.ArgumentList.Add("-p:VSTestTestCaseFilter=AnyValueAtAll");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null for dotnet.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        Assert.True(
            process.WaitForExit((int)RunTimeout.TotalMilliseconds),
            $"`dotnet msbuild -t:{TargetName}` did not exit within {RunTimeout.TotalSeconds:0}s.");

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
