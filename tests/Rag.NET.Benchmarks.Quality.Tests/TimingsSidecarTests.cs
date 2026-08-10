using System.Globalization;
using System.Text;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="TimingsSidecar"/> — the cost half of the boundary every Phase 5.1 entrant
/// crosses, sitting beside the run file <see cref="TrecRunFile"/> already pins. The properties that
/// carry the comparison get the most tests: a raw per-query latency survives the round trip
/// bit-for-bit (percentiles are computed once, on the .NET side, from exactly these samples), and a
/// malformed or missing field fails loudly rather than defaulting, because a latency that silently
/// became zero is a p50 that reads as a win.
/// <para>
/// The culture tests exist because this machine's decimal separator is a comma. A writer using the
/// current culture would emit <c>12,5</c>, which is not a mangled number in JSON but two members —
/// <c>12</c> followed by a stray <c>5</c> — so the failure would land on whoever read the file
/// next rather than on whoever wrote it.
/// </para>
/// </summary>
public sealed class TimingsSidecarTests : IDisposable
{
    /// <summary>
    /// A well-formed sidecar as field/raw-value pairs, so a case can drop or replace exactly one
    /// field without a fragile string edit.
    /// </summary>
    private static readonly (string Field, string Value)[] ValidFields =
    [
        ("runTag", "\"tag\""),
        ("indexingSeconds", "12.5"),
        ("embeddingCacheHits", "7"),
        ("embeddingCacheMisses", "0"),
        ("unitCount", "5183"),
        ("maxUnitsPerDocument", "3"),
        ("queryLatenciesMilliseconds", "{\n    \"q-1\": 4.25,\n    \"q-2\": 3.5\n  }"),
    ];

    private readonly string _directory;

    public TimingsSidecarTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ragnet-timings-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string RunPath => Path.Combine(_directory, "entrant.run");

    private string SidecarPath => RunPath + ".timings.json";

    /// <summary>A valid set of timings whose latency map the caller supplies.</summary>
    private static EntrantTimings Timings(params (string QueryId, double LatencyMilliseconds)[] latencies) =>
        new(
            RunTag: "tag",
            IndexingSeconds: 12.5,
            QueryLatenciesMilliseconds: Latencies(latencies),
            EmbeddingCacheHits: 7,
            EmbeddingCacheMisses: 0,
            UnitCount: 5183,
            MaxUnitsPerDocument: 3);

    private static IReadOnlyDictionary<string, double> Latencies(
        params (string QueryId, double LatencyMilliseconds)[] latencies)
    {
        var byQuery = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (queryId, latency) in latencies)
        {
            byQuery[queryId] = latency;
        }

        return byQuery;
    }

    /// <summary>Writes raw sidecar bytes, for the cases where the file is not ours to have made.</summary>
    private void WriteSidecar(string json) => File.WriteAllText(SidecarPath, json);

    // ------------------------------------------------------------------- pairing by path

    [Fact]
    public void PathFor_AppendsTheSuffixToTheRunFilePath()
    {
        // Derived, never passed in: a caller free to name the sidecar could pair scifact's run
        // file with fiqa's timings, and every number in the resulting row would look plausible.
        Assert.Equal(SidecarPath, TimingsSidecar.PathFor(RunPath), StringComparer.Ordinal);
    }

    // ------------------------------------------------------------- repeat runs, by run index

    [Fact]
    public void PathFor_RunIndexOne_IsTheUnindexedPath()
    {
        // The first run keeps the historical name, so a single-run pairing stays exactly where
        // every existing reader — ReadPaired, the Python fixture test — already looks.
        Assert.Equal(TimingsSidecar.PathFor(RunPath), TimingsSidecar.PathFor(RunPath, 1), StringComparer.Ordinal);
    }

    [Fact]
    public void PathFor_ALaterRunIndex_PutsTheIndexBeforeTheJsonExtension()
    {
        // Same convention extended, not a second format: the run file's own name stays visible,
        // and the family sorts together in a directory listing.
        Assert.Equal(RunPath + ".timings.2.json", TimingsSidecar.PathFor(RunPath, 2), StringComparer.Ordinal);
        Assert.Equal(RunPath + ".timings.10.json", TimingsSidecar.PathFor(RunPath, 10), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PathFor_RejectsANonPositiveRunIndex(int runIndex)
    {
        // Run indices are 1-based: "run 0" would silently alias run 1 under any convention that
        // maps both to a real path, and a negative index is a loop written backwards.
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => TimingsSidecar.PathFor(RunPath, runIndex));
    }

    [Fact]
    public void WriteAndRead_WithARunIndex_KeepEveryRepeatOnDisk()
    {
        // The reason the index exists: without it a repeat run overwrites the last, and the
        // reproducibility gate has one sidecar where it needs at least two.
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)), runIndex: 1);
        TimingsSidecar.Write(RunPath, Timings(("q-1", 5.0)) with { IndexingSeconds = 2.4 }, runIndex: 2);

        Assert.True(File.Exists(SidecarPath));
        Assert.True(File.Exists(RunPath + ".timings.2.json"));
        Assert.Equal(12.5, TimingsSidecar.Read(RunPath, 1).IndexingSeconds);
        Assert.Equal(2.4, TimingsSidecar.Read(RunPath, 2).IndexingSeconds);
    }

    [Fact]
    public void ReadPaired_WithARunIndex_ChecksTheIndexedSidecarAgainstTheRunFile()
    {
        // Every repeat gets the full pairing check, not just the first: a repeat whose sidecar
        // came from another entrant is exactly as wrong as a first run whose sidecar did.
        WriteRunFile("tag", "q-1");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)) with { RunTag = "other" }, runIndex: 2);

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.ReadPaired(RunPath, 2));

        Assert.Contains("other", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AMissingIndexedSidecarNamesTheIndexedPath()
    {
        // The refuse-on-miss failure has to point at the repeat that is absent, not at the first
        // run's sidecar — the two differ only in the index, and that is the whole message.
        WriteRunFile("tag", "q-1");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)));

        var ex = Assert.Throws<FileNotFoundException>(() => TimingsSidecar.ReadPaired(RunPath, 2));

        Assert.Contains(RunPath + ".timings.2.json", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------- writing

    [Fact]
    public void Write_EmitsFixedKeyOrderWithQueryIdsInOrdinalOrder()
    {
        // Pinned as an exact string. Field order is the writer's, query ids are ordinal — note
        // that puts "q-10" before "q-2", which is the point: ordinal is the one order that does
        // not depend on the machine's culture or on anyone's idea of a natural sort.
        TimingsSidecar.Write(RunPath, Timings(("q-2", 3.5), ("q-10", 1.0), ("q-1", 4.25)));

        Assert.Equal(
            "{\n" +
            "  \"runTag\": \"tag\",\n" +
            "  \"indexingSeconds\": 12.5,\n" +
            "  \"embeddingCacheHits\": 7,\n" +
            "  \"embeddingCacheMisses\": 0,\n" +
            "  \"unitCount\": 5183,\n" +
            "  \"maxUnitsPerDocument\": 3,\n" +
            "  \"queryLatenciesMilliseconds\": {\n" +
            "    \"q-1\": 4.25,\n" +
            "    \"q-10\": 1,\n" +
            "    \"q-2\": 3.5\n" +
            "  }\n" +
            "}\n",
            File.ReadAllText(SidecarPath),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Write_UsesTheInvariantDecimalSeparatorUnderACommaCulture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            TimingsSidecar.Write(RunPath, Timings(("q-1", 0.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var content = File.ReadAllText(SidecarPath);

        Assert.Contains("\"indexingSeconds\": 12.5,", content, StringComparison.Ordinal);
        Assert.Contains("\"q-1\": 0.5", content, StringComparison.Ordinal);
        Assert.DoesNotContain("12,5", content, StringComparison.Ordinal);
        Assert.DoesNotContain("0,5", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RejectsAnEmptyLatencyMap()
    {
        // An entrant that timed zero queries did not run fast; it lost its query set, exactly as
        // TrecRunFile.Write reads an empty run set. Percentiles over nothing are 0/0, and a p50 of
        // zero milliseconds would be the best row in the table.
        _ = Assert.Throws<ArgumentException>(() => TimingsSidecar.Write(RunPath, Timings()));
    }

    [Fact]
    public void Write_RejectsANegativeLatency()
    {
        // No clock runs backwards; a negative sample means the span was measured across a clock
        // change or the subtraction was the wrong way round, and it would drag a p50 down.
        _ = Assert.Throws<ArgumentException>(() => TimingsSidecar.Write(RunPath, Timings(("q-1", -0.001))));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Write_RejectsANonFiniteLatency(double latency)
    {
        // NaN has to be refused here rather than downstream: it does not compare, so it can sort
        // anywhere and quietly become whichever percentile the sort algorithm lands it on.
        _ = Assert.Throws<ArgumentException>(() => TimingsSidecar.Write(RunPath, Timings(("q-1", latency))));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Write_RejectsANegativeOrNonFiniteIndexingTime(double indexingSeconds)
    {
        _ = Assert.Throws<ArgumentException>(() =>
            TimingsSidecar.Write(RunPath, Timings(("q-1", 1.0)) with { IndexingSeconds = indexingSeconds }));
    }

    [Fact]
    public void Write_RejectsARunTagContainingWhitespace()
    {
        // The tag has to match the run file's, and that file is whitespace-separated: a tag the
        // sidecar can carry but the run file cannot could never be paired with anything.
        _ = Assert.Throws<ArgumentException>(() =>
            TimingsSidecar.Write(RunPath, Timings(("q-1", 1.0)) with { RunTag = "two words" }));
    }

    [Fact]
    public void Write_RejectsAQueryIdContainingWhitespace()
    {
        _ = Assert.Throws<ArgumentException>(() => TimingsSidecar.Write(RunPath, Timings(("q 1", 1.0))));
    }

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("has\\backslash")]
    public void Write_RejectsAQueryIdNeedingJsonEscaping(string queryId)
    {
        // Refused rather than escaped. No BEIR query id contains either character, so an escaping
        // path here would be code no measurement ever exercises — and an id that survives JSON
        // escaping still could not survive the run file's whitespace split, so it could never be
        // paired with a ranking anyway.
        _ = Assert.Throws<ArgumentException>(() => TimingsSidecar.Write(RunPath, Timings((queryId, 1.0))));
    }

    [Fact]
    public void Write_RejectsANegativeCacheCount()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            TimingsSidecar.Write(RunPath, Timings(("q-1", 1.0)) with { EmbeddingCacheMisses = -1 }));
    }

    [Fact]
    public void Write_RejectsANonPositiveUnitCount()
    {
        // Zero units indexed is an entrant that lost its corpus, and its indexing wall-clock would
        // be the fastest number in the table.
        _ = Assert.Throws<ArgumentException>(() =>
            TimingsSidecar.Write(RunPath, Timings(("q-1", 1.0)) with { UnitCount = 0 }));
    }

    [Fact]
    public void Write_RejectsANonPositiveMaxUnitsPerDocument()
    {
        // The harness multiplies retrieval depth by this factor; a zero would mean depth 0.
        _ = Assert.Throws<ArgumentException>(() =>
            TimingsSidecar.Write(RunPath, Timings(("q-1", 1.0)) with { MaxUnitsPerDocument = 0 }));
    }

    // -------------------------------------------------------------------------- round trips

    [Fact]
    public void RoundTrip_PreservesEveryPerQueryLatencyExactly()
    {
        // Exactly, not to five places: these are the raw samples every percentile is read off, and
        // a writer that rounded to one decimal would collapse a whole run's tail onto one value.
        var latencies = Latencies(
            ("q-1", 0.1234567890123),
            ("q-2", 1234.5678901234),
            ("q-3", 1e-7),
            ("q-4", 0.0),
            ("q-5", 98765.43210987654));

        TimingsSidecar.Write(RunPath, Timings(("ignored", 1.0)) with { QueryLatenciesMilliseconds = latencies });

        var read = TimingsSidecar.Read(RunPath).QueryLatenciesMilliseconds;

        Assert.Equal(latencies.Count, read.Count);
        foreach (var (queryId, latency) in latencies)
        {
            Assert.True(
                latency.Equals(read[queryId]),
                FormattableString.Invariant(
                    $"Query '{queryId}' was written as {latency:R} and read back as {read[queryId]:R}."));
        }
    }

    [Fact]
    public void RoundTrip_PreservesTheTagCountsAndIndexingTime()
    {
        var written = Timings(("q-1", 4.25), ("q-2", 3.5)) with
        {
            RunTag = "langchain-core-1.5.3",
            EmbeddingCacheHits = 5183,
            EmbeddingCacheMisses = 12,
        };

        TimingsSidecar.Write(RunPath, written);

        var read = TimingsSidecar.Read(RunPath);

        Assert.Equal("langchain-core-1.5.3", read.RunTag, StringComparer.Ordinal);
        Assert.Equal(12.5, read.IndexingSeconds);
        Assert.Equal(5183, read.EmbeddingCacheHits);
        Assert.Equal(12, read.EmbeddingCacheMisses);
        Assert.Equal(5183, read.UnitCount);
        Assert.Equal(3, read.MaxUnitsPerDocument);
    }

    [Fact]
    public void Read_ExposesTheQueryIdsSoThePairingIsCheckable()
    {
        // The set of ids is the thing the run file has to agree with, so it has to be reachable
        // from the read result rather than folded into a statistic on the way out.
        TimingsSidecar.Write(RunPath, Timings(("q-2", 3.5), ("q-1", 4.25)));

        var read = TimingsSidecar.Read(RunPath);

        Assert.Equal(["q-1", "q-2"], read.QueryLatenciesMilliseconds.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------------------ reading

    [Fact]
    public void Read_ReadsADottedLatencyUnderACommaCulture()
    {
        WriteSidecar(Valid());

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            Assert.Equal(4.25, TimingsSidecar.Read(RunPath).QueryLatenciesMilliseconds["q-1"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Read_ACommaDecimalSeparatorFailsLoudlyNamingTheFile()
    {
        // "12,5" is what a culture-sensitive writer on this machine emits. It must not be read as
        // 12 with a stray 5 beside it, and it must not be read as 125 either.
        WriteSidecar(WithField("indexingSeconds", "12,5"));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains(SidecarPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MalformedJsonFailsLoudlyNamingTheFileAndLine()
    {
        WriteSidecar("{\n  \"runTag\": \"tag\",\n  oops\n}\n");

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains(SidecarPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runTag")]
    [InlineData("indexingSeconds")]
    [InlineData("embeddingCacheHits")]
    [InlineData("embeddingCacheMisses")]
    [InlineData("unitCount")]
    [InlineData("maxUnitsPerDocument")]
    [InlineData("queryLatenciesMilliseconds")]
    public void Read_AMissingFieldFailsLoudlyNamingItAndTheFile(string field)
    {
        // Every field is required, because the alternative to failing is defaulting: a missing
        // embeddingCacheMisses read as 0 would assert a warm cache for a run that was cold, and
        // that is the one distinction the design says this file exists to carry.
        WriteSidecar(WithoutField(field));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains(SidecarPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AnUnknownFieldFailsLoudly()
    {
        // An extra field means the writer knows something this reader does not. The likeliest
        // extra is a percentile the entrant computed for itself — precisely what this boundary
        // exists to keep out of the table — so it is refused rather than ignored.
        WriteSidecar(Valid().Replace(
            "  \"runTag\":", "  \"p99Milliseconds\": 4.2,\n  \"runTag\":", StringComparison.Ordinal));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains("p99Milliseconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AnEmptyLatencyMapFailsLoudly()
    {
        WriteSidecar(WithField("queryLatenciesMilliseconds", "{}"));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains(SidecarPath, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1.5")]
    [InlineData("\"4.25\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void Read_ALatencyThatIsNotANonNegativeNumberFailsLoudlyNamingTheQuery(string latency)
    {
        WriteSidecar(WithField(
            "queryLatenciesMilliseconds", "{\n    \"q-1\": 4.25,\n    \"q-2\": " + latency + "\n  }"));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains("q-2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ANegativeIndexingTimeFailsLoudly()
    {
        WriteSidecar(WithField("indexingSeconds", "-12.5"));

        _ = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));
    }

    [Fact]
    public void Read_ANegativeCacheCountFailsLoudly()
    {
        WriteSidecar(WithField("embeddingCacheMisses", "-1"));

        _ = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));
    }

    [Fact]
    public void Read_ANonIntegerCountFailsLoudly()
    {
        // 5183.5 units is not a rounding artefact, it is a field holding something other than a
        // count — and silently truncating it would hide whatever produced it.
        WriteSidecar(WithField("unitCount", "5183.5"));

        _ = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));
    }

    [Fact]
    public void Read_ARunTagContainingWhitespaceFailsLoudly()
    {
        WriteSidecar(WithField("runTag", "\"two words\""));

        _ = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));
    }

    [Fact]
    public void Read_ATopLevelValueThatIsNotAnObjectFailsLoudly()
    {
        WriteSidecar("[]\n");

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.Read(RunPath));

        Assert.Contains(SidecarPath, ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- the pairing with the run file

    [Fact]
    public void ReadPaired_ReturnsTheTimingsWhenTheTagAndQueryIdsAgree()
    {
        WriteRunFile("tag", "q-1", "q-2");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25), ("q-2", 3.5)));

        var timings = TimingsSidecar.ReadPaired(RunPath);

        Assert.Equal("tag", timings.RunTag, StringComparer.Ordinal);
        Assert.Equal(4.25, timings.QueryLatenciesMilliseconds["q-1"]);
    }

    [Fact]
    public void ReadPaired_AMissingSidecarRefusesRatherThanReturningNothing()
    {
        // The refuse-on-miss rule the opted-in BEIR cases already use for a missing run file. A
        // cost measurement that returned nothing when the sidecar was absent would read as a pass,
        // and this repository has shipped inert green tests before.
        WriteRunFile("tag", "q-1");

        var ex = Assert.Throws<FileNotFoundException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains(SidecarPath, ex.Message, StringComparison.Ordinal);
        // Named, not merely implied: the run file's path is a prefix of the sidecar's, so asserting
        // on the path alone would pass against the runtime's own "could not find file" message.
        Assert.Contains("refuse-on-miss", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AMissingRunFileIsBlamedOnTheRunFileNotTheSidecar()
    {
        // Both absent means the entrant never ran. Naming the sidecar there would send whoever
        // reads it looking for a timing bug in a run that does not exist.
        var ex = Assert.Throws<FileNotFoundException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains(RunPath, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SidecarPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_ADisagreeingRunTagFailsLoudlyNamingBothTags()
    {
        // The pairing error that produces a complete, plausible, wrong row: one entrant's latencies
        // published under another's name, with every number present and every number attributed to
        // the wrong library.
        WriteRunFile("langchain-core-1.5.3", "q-1");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)) with { RunTag = "haystack-ai-3.0.0" });

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains("langchain-core-1.5.3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("haystack-ai-3.0.0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AQueryRankedButNotTimedFailsLoudlyNamingIt()
    {
        // A sidecar short of a query is a percentile over a subset. It reads as a faster entrant if
        // the untimed queries were the slow ones, which is exactly the correlation to expect when
        // an entrant stops timing partway through.
        WriteRunFile("tag", "q-1", "q-2");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains("q-2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AQueryTimedButNotRankedFailsLoudlyNamingIt()
    {
        // The other direction: latencies for queries this run file never answered, which is a
        // sidecar left over from a different dataset or a different query split.
        WriteRunFile("tag", "q-1");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25), ("q-9", 3.5)));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains("q-9", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AQueryThatRetrievedNothingStillHasToBeTimed()
    {
        // TrecRunFile keeps a query that retrieved nothing as a present, empty ranking — so it is
        // one of the run's queries and it cost time. An entrant that timed only the queries with
        // results would drop its own worst-case retrievals out of the samples.
        TrecRunFile.Write(
            RunPath,
            new Dictionary<string, IReadOnlyList<ScoredDocument>>(StringComparer.Ordinal)
            {
                ["q-1"] = [new ScoredDocument("doc-1", 0.9)],
                ["q-empty"] = [],
            },
            "tag");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains("q-empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPaired_AMalformedRunFileFailsThroughTrecRunFilesOwnChecks()
    {
        // The pairing does not get its own lenient parser: the run file is read by the reader every
        // published quality figure goes through, so a file this accepts is a file that scores.
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 1 0.9\n");
        TimingsSidecar.Write(RunPath, Timings(("q-1", 4.25)));

        var ex = Assert.Throws<InvalidDataException>(() => TimingsSidecar.ReadPaired(RunPath));

        Assert.Contains(RunPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- sidecar fixtures

    /// <summary>A run file tagged <paramref name="runTag"/> ranking one document per query.</summary>
    private void WriteRunFile(string runTag, params string[] queryIds)
    {
        var runs = new Dictionary<string, IReadOnlyList<ScoredDocument>>(StringComparer.Ordinal);
        foreach (var queryId in queryIds)
        {
            runs[queryId] = [new ScoredDocument("doc-" + queryId, 0.9)];
        }

        TrecRunFile.Write(RunPath, runs, runTag);
    }


    /// <summary>A well-formed sidecar.</summary>
    private static string Valid() => Json(ValidFields);

    /// <summary>The well-formed sidecar with one field removed.</summary>
    private static string WithoutField(string field)
    {
        var kept = new List<(string Field, string Value)>(ValidFields.Length);
        foreach (var pair in ValidFields)
        {
            if (!string.Equals(pair.Field, field, StringComparison.Ordinal))
            {
                kept.Add(pair);
            }
        }

        return Json(kept);
    }

    /// <summary>The well-formed sidecar with one field's raw JSON value replaced.</summary>
    private static string WithField(string field, string value)
    {
        var replaced = new List<(string Field, string Value)>(ValidFields.Length);
        foreach (var pair in ValidFields)
        {
            replaced.Add(string.Equals(pair.Field, field, StringComparison.Ordinal)
                ? (field, value)
                : pair);
        }

        return Json(replaced);
    }

    private static string Json(IReadOnlyList<(string Field, string Value)> fields)
    {
        var builder = new StringBuilder("{\n");
        for (var i = 0; i < fields.Count; i++)
        {
            _ = builder.Append("  \"").Append(fields[i].Field).Append("\": ").Append(fields[i].Value)
                .Append(i == fields.Count - 1 ? "\n" : ",\n");
        }

        return builder.Append("}\n").ToString();
    }
}
