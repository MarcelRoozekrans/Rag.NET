using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// The check behind the cost design's central claim (§2.1): <b>no figure is produced by timing
/// across the process boundary</b>. Every entrant times itself, in its own runtime — a .NET
/// stopwatch around a Python process would measure interpreter start-up, module imports and file
/// I/O as if they were a library's retrieval, which is exactly the confound the sidecar design
/// exists to remove.
/// <para>
/// The guard is a source scan over the figure-producing trees — <c>benchmarks/</c> and the BEIR
/// integration tests — and it is deliberately <b>stricter than the claim</b>: no source file there
/// may both use a timing primitive (<c>Stopwatch</c>, <c>time.perf_counter</c>,
/// <c>time.monotonic</c>) and launch a subprocess (<c>Process.Start</c>, <c>subprocess.</c>,
/// <c>os.system</c>, <c>Popen</c>). Deciding whether a span textually <i>encloses</i> a launch
/// would need a parser per language and would invite exactly the clever almost-enclosing code
/// this guard exists to refuse; a file that both times and launches is where a cross-boundary
/// figure would be born, so the two concerns must not share a file at all. Launching
/// <c>uv run</c> from scanned code requires one of the launch APIs above, so the <c>uv run</c>
/// case is covered by them.
/// </para>
/// <para>
/// File discovery copies <c>tests/Rag.NET.RepoConventions.Tests</c>' conventions — walk up to
/// <c>Rag.NET.slnx</c> for the root, enumerate recursively, exclude build output by path segment,
/// and fail loudly when the scan finds implausibly little, because a guard that scans nothing
/// passes for the wrong reason.
/// </para>
/// </summary>
public sealed class BoundaryTimingGuardTests
{
    /// <summary>The in-process clocks the entrants bracket themselves with.</summary>
    private static readonly string[] TimingTokens =
        ["Stopwatch", "time.perf_counter", "time.monotonic"];

    /// <summary>
    /// The subprocess-launch APIs available to the scanned code, .NET and Python. Matched as
    /// call-shaped tokens rather than prose (a skip message that tells a human to run
    /// <c>uv run python run_entrant.py</c> is not a launch).
    /// </summary>
    private static readonly string[] LaunchTokens =
        ["Process.Start", "ProcessStartInfo", "subprocess.", "os.system(", "os.popen(", "Popen("];

    /// <summary>
    /// Directory segments that are not source: build output (the conventions suite's exclusions)
    /// plus Python's environment and bytecode caches.
    /// </summary>
    private static readonly string[] ExcludedSegments = ["bin", "obj", ".venv", "__pycache__"];

    /// <summary>
    /// Fewer scanned files than this means the scan lost the tree and would pass over nothing —
    /// silently, forever. The two trees hold well over this many .cs and .py files today.
    /// </summary>
    private const int FewestPlausibleSourceFiles = 20;

    /// <summary>
    /// Fewer self-timing files than this means the entrants stopped timing themselves (or the
    /// scan stopped seeing them) — either way the guard would be guarding nothing.
    /// At minimum <c>run_entrant.py</c> and <c>BeirHarness.cs</c> time.
    /// </summary>
    private const int FewestPlausibleTimedFiles = 2;

    [Fact]
    public void NoFigureProducingSourceFile_BothTimesAndLaunchesAProcess()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = ScannedFiles(repositoryRoot);

        Assert.True(
            files.Count >= FewestPlausibleSourceFiles,
            $"Scanned only {files.Count} source files under benchmarks/ and the BEIR integration " +
            $"tests, expected at least {FewestPlausibleSourceFiles}. A guard that scans nothing " +
            "passes for the wrong reason, so this fails instead.");

        var timedFiles = 0;
        var violations = new List<string>();
        foreach (var file in files)
        {
            InspectFile(repositoryRoot, file, ref timedFiles, violations);
        }

        Assert.True(
            timedFiles >= FewestPlausibleTimedFiles,
            $"Only {timedFiles} scanned files use a timing primitive, expected at least " +
            $"{FewestPlausibleTimedFiles} (the entrants time themselves in run_entrant.py and " +
            "BeirHarness.cs). Either self-timing was removed or the scan lost the tree; both " +
            "would leave this guard guarding nothing.");

        Assert.True(
            violations.Count == 0,
            "The following source files both use a timing primitive and launch a subprocess. " +
            "The cost design's rule (§2.1) is that no figure is timed across the process " +
            "boundary — every entrant times itself, in its own runtime — and this guard enforces " +
            "it at file granularity: timing and process-launching must not share a source file, " +
            "because a stopwatch around a process would bill interpreter start-up and file I/O " +
            "as a library's latency. Move one of the two concerns to its own file, with the " +
            "launch outside every span:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", violations));
    }

    /// <summary>Flags <paramref name="file"/> if timing and launching share it; counts it if it times.</summary>
    private static void InspectFile(
        string repositoryRoot, string file, ref int timedFiles, List<string> violations)
    {
        var lines = File.ReadAllLines(file);
        var timing = FirstLineContaining(lines, TimingTokens, out var timingToken);
        var launch = FirstLineContaining(lines, LaunchTokens, out var launchToken);

        if (timing > 0)
        {
            timedFiles++;
        }

        if (timing > 0 && launch > 0)
        {
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            violations.Add(
                $"{relative}: times at line {timing} ('{timingToken}') and launches a " +
                $"subprocess at line {launch} ('{launchToken}')");
        }
    }

    /// <summary>The 1-based first line holding any of <paramref name="tokens"/>, or 0 for none.</summary>
    private static int FirstLineContaining(string[] lines, string[] tokens, out string foundToken)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var token in tokens)
            {
                if (lines[i].Contains(token, StringComparison.Ordinal))
                {
                    foundToken = token;
                    return i + 1;
                }
            }
        }

        foundToken = string.Empty;
        return 0;
    }

    /// <summary>
    /// Every .cs and .py file in the figure-producing trees, build output and Python caches
    /// excluded — the same discovery shape the repo-conventions guards use.
    /// </summary>
    private static List<string> ScannedFiles(string repositoryRoot)
    {
        string[] roots =
        [
            Path.Combine(repositoryRoot, "benchmarks"),
            Path.Combine(repositoryRoot, "tests", "Rag.NET.Benchmarks.Quality.IntegrationTests"),
        ];

        var files = new List<string>();
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                var isSource = string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase);
                if (isSource && !IsExcluded(Path.GetRelativePath(root, file)))
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    /// <summary>Reports whether any path segment names build output or a Python cache.</summary>
    private static bool IsExcluded(string relativePath)
    {
        foreach (var segment in relativePath.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (var excluded in ExcludedSegments)
            {
                if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> to the directory holding
    /// <c>Rag.NET.slnx</c> — <c>TestProject.FindRepositoryRoot</c>'s convention, copied so the
    /// two suites never disagree about where the repository is.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rag.NET.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find 'Rag.NET.slnx' in any ancestor of '{AppContext.BaseDirectory}'. " +
            "This guard reads the working tree at run time and cannot run without it.");
    }
}
