using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Pins the failure-log dump into every workflow loop that runs tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Phase 6.2.41 moved every test project onto Microsoft.Testing.Platform,
/// which prints a failure <i>count</i> and nothing else — no test name, no assertion, no stack. The
/// first red build after that migration was #571, where diagnosing one flaky test took a full log
/// read, a count comparison against main, and a reproduction outside the repository, and the failing
/// test still could not be named.
/// </para>
/// <para>
/// A dump is exactly the kind of change that reviews well and silently stops working — a renamed
/// directory, a restructured loop, a tidy-up that drops it. This pins it to all four loops rather
/// than trusting that nobody removes it.
/// </para>
/// </remarks>
public sealed class CiFailureReportingTests
{
    private static readonly string[] WorkflowsThatRunTests = ["ci.yml", "nightly.yml"];

    /// <summary>Every loop that runs tests must also dump the log when a project fails.</summary>
    [Fact]
    public void EveryWorkflowLoopThatRunsTestsDumpsTheFailureLog()
    {
        foreach (var workflow in WorkflowsThatRunTests)
        {
            var path = Path.Combine(
                TestProject.FindRepositoryRoot(), ".github", "workflows", workflow);
            var text = File.ReadAllText(path);

            var runs = CountOccurrences(text, "dotnet test \"$project\" --no-build -c Release");
            var dumps = CountOccurrences(text, "TestResults/*.log");

            Assert.True(
                runs > 0,
                $"{workflow} no longer runs tests with the pinned command, so this guard is " +
                "asserting nothing. Either the workflow changed shape or this test is stale.");

            Assert.True(
                dumps >= runs,
                $"{workflow} runs `dotnet test` in {runs} place(s) but dumps the failure log in " +
                $"{dumps}. Microsoft.Testing.Platform prints only a failure count, so a loop " +
                "without the dump produces a red job that names no test at all — see #571.");
        }
    }

    /// <summary>Every dump must decode UTF-16 rather than printing it as bytes.</summary>
    [Fact]
    public void TheDumpDecodesTheLogRatherThanPrintingItRaw()
    {
        foreach (var workflow in WorkflowsThatRunTests)
        {
            var path = Path.Combine(
                TestProject.FindRepositoryRoot(), ".github", "workflows", workflow);
            var text = File.ReadAllText(path);

            var runs = CountOccurrences(text, "dotnet test \"$project\" --no-build -c Release");
            var decodes = CountOccurrences(text, "iconv -f UTF-16LE");

            Assert.True(
                runs > 0,
                $"{workflow} no longer runs tests with the pinned command, so this guard is " +
                "asserting nothing. Either the workflow changed shape or this test is stale.");

            Assert.True(
                decodes >= runs,
                $"{workflow} runs `dotnet test` in {runs} place(s) but decodes the failure log " +
                $"with iconv in only {decodes}. A loop whose dump falls back to a bare `cat` " +
                "prints UTF-16LE as spaced-out mojibake instead of the test name and assertion.");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
