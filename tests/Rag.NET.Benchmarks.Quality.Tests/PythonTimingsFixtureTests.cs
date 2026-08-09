using System.Globalization;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Proves the Python writer and the .NET reader agree about the sidecar format, against a
/// committed fixture that <c>benchmarks/library-comparison-python/timings.py</c> actually wrote
/// (<c>Fixtures/python-written-format-fixture.trec.timings.json</c> — invented numbers, labelled
/// by its own run tag; see <c>Fixtures/README.md</c> for the regenerating command).
/// <para>
/// <b>Why a committed cross-language fixture and not a round-trip test.</b> Every other test in
/// this suite exercises the .NET writer against the .NET reader, so a format the two agreed to
/// misunderstand together would stay green. The real risk at this boundary is the writer nobody
/// here compiles: Python's <c>timings.py</c> mirrors <see cref="TimingsSidecar"/> by convention,
/// and only a file that genuinely crossed the language boundary can catch the two drifting —
/// which is the same reason <c>trec_run.py</c>'s output is verified on this side by
/// <c>BeirPythonEntrantsTests</c> rather than trusted.
/// </para>
/// <para>
/// The values are chosen to expose the two historic failure modes: <c>4.25</c> and <c>12.25</c>
/// catch a culture-sensitive writer or reader (a comma-culture machine would produce or expect
/// <c>4,25</c> — the defect <c>TrecRunFile.ParseScore</c> documents as already found once here),
/// and the id set <c>q-1</c>/<c>q-10</c>/<c>q-2</c> catches a writer sorting numerically or by
/// culture instead of ordinally, because ordinal is the one order that puts <c>q-10</c> before
/// <c>q-2</c>.
/// </para>
/// </summary>
public sealed class PythonTimingsFixtureTests
{
    /// <summary>The run-file path the committed sidecar sits beside; the sidecar is derived from it.</summary>
    private static string FixtureRunPath => Path.Combine(
        FindRepositoryRoot(), "tests", "Rag.NET.Benchmarks.Quality.Tests", "Fixtures",
        "python-written-format-fixture.trec");

    [Fact]
    public void Read_AcceptsThePythonWrittenSidecar_ValueForValue()
    {
        var timings = TimingsSidecar.Read(FixtureRunPath);

        // Every value exactly as the Python command invented it — no tolerance, because these are
        // parsed literals, not measurements, and any difference is a format disagreement.
        Assert.Equal("format-fixture-not-a-measurement", timings.RunTag, StringComparer.Ordinal);
        Assert.Equal(4.25, timings.IndexingSeconds);
        Assert.Equal(7, timings.EmbeddingCacheHits);
        Assert.Equal(3, timings.EmbeddingCacheMisses);
        Assert.Equal(5, timings.UnitCount);
        Assert.Equal(2, timings.MaxUnitsPerDocument);
        Assert.Equal(3, timings.QueryLatenciesMilliseconds.Count);
        Assert.Equal(0.125, timings.QueryLatenciesMilliseconds["q-1"]);
        Assert.Equal(3.5, timings.QueryLatenciesMilliseconds["q-10"]);
        Assert.Equal(12.25, timings.QueryLatenciesMilliseconds["q-2"]);
    }

    [Fact]
    public void Read_AcceptsThePythonWrittenSidecar_UnderACommaCulture()
    {
        // The Python bytes carry "4.25"; a reader that parsed under the machine's culture would
        // fail (or worse, misread) on exactly the machines this repository develops on.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            Assert.Equal(4.25, TimingsSidecar.Read(FixtureRunPath).IndexingSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ThePythonWriter_PutsQueryIdsInOrdinalOrder_And_WritesDotDecimals()
    {
        // On the file's own bytes, not the parsed result: a reader would accept any key order and
        // any JSON number form, so only the bytes can show the Python writer holds the .NET
        // writer's conventions — ordinal id order ("q-10" before "q-2") and dot decimals.
        var content = File.ReadAllText(TimingsSidecar.PathFor(FixtureRunPath));

        var q1 = content.IndexOf("\"q-1\":", StringComparison.Ordinal);
        var q10 = content.IndexOf("\"q-10\":", StringComparison.Ordinal);
        var q2 = content.IndexOf("\"q-2\":", StringComparison.Ordinal);
        Assert.True(q1 >= 0 && q10 > q1 && q2 > q10,
            $"Expected ordinal id order q-1 < q-10 < q-2 in the bytes, found offsets {q1}, {q10}, {q2}.");

        Assert.Contains("\"indexingSeconds\": 4.25", content, StringComparison.Ordinal);
        Assert.DoesNotContain("4,25", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AcceptsThePythonWrittenSidecar_BesideAMatchingRunFile()
    {
        // The full pairing path across the language boundary: a .NET-written run file whose tag
        // and query-id set match the Python-written sidecar's must be accepted by ReadPaired —
        // this is exactly the pair a real Python entrant's run produces.
        var directory = Path.Combine(
            Path.GetTempPath(), "ragnet-python-fixture-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var runPath = Path.Combine(directory, "python-written-format-fixture.trec");
            TrecRunFile.Write(
                runPath,
                new Dictionary<string, IReadOnlyList<ScoredDocument>>(StringComparer.Ordinal)
                {
                    ["q-1"] = [new ScoredDocument("doc-a", 0.9)],
                    ["q-10"] = [new ScoredDocument("doc-b", 0.8)],
                    ["q-2"] = [new ScoredDocument("doc-c", 0.7)],
                },
                "format-fixture-not-a-measurement");
            File.Copy(
                TimingsSidecar.PathFor(FixtureRunPath), TimingsSidecar.PathFor(runPath));

            var timings = TimingsSidecar.ReadPaired(runPath);

            Assert.Equal(12.25, timings.QueryLatenciesMilliseconds["q-2"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> to the directory holding
    /// <c>Rag.NET.slnx</c> — the repo-conventions suite's convention, copied rather than paralleled
    /// so the two never disagree about where the repository is.
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
            $"Could not find 'Rag.NET.slnx' in any ancestor of '{AppContext.BaseDirectory}'. The " +
            "cross-language fixture is read from the working tree, so this suite cannot run " +
            "without it.");
    }
}
