using System.Globalization;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="TrecRunFile"/> — the run-file boundary every Phase 3.14 entrant crosses. Two
/// properties carry the comparison and get the most tests: rank order survives a round trip
/// unchanged (including ties, which a score-sorting reader would silently flip), and a malformed
/// line fails loudly rather than being skipped, because a silently dropped line is a silently
/// different nDCG.
/// <para>
/// The culture tests exist because this machine's decimal separator is a comma: a writer using the
/// current culture would publish <c>0,5</c>, and a reader using it would accept that file — and the
/// two machines would disagree about every score while agreeing about nothing loudly.
/// </para>
/// </summary>
public sealed class TrecRunFileTests : IDisposable
{
    private readonly string _directory;

    public TrecRunFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ragnet-trec-run-" + Guid.NewGuid().ToString("N"));
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

    private static IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> Runs(
        params (string QueryId, IReadOnlyList<ScoredDocument> Documents)[] entries)
    {
        var runs = new Dictionary<string, IReadOnlyList<ScoredDocument>>(StringComparer.Ordinal);
        foreach (var (queryId, documents) in entries)
        {
            runs[queryId] = documents;
        }

        return runs;
    }

    // ----------------------------------------------------------------- writing

    [Fact]
    public void Write_EmitsTrecLinesSortedByQueryIdWithOneBasedRanks()
    {
        // Pinned as an exact string: six space-separated fields, the literal Q0, 1-based ranks,
        // invariant decimal points, '\n' newlines, queries in ordinal id order so the same runs
        // produce a byte-identical published file on every machine.
        TrecRunFile.Write(
            RunPath,
            Runs(
                ("q-2", [new ScoredDocument("doc-b", 0.75), new ScoredDocument("doc-a", 0.75), new ScoredDocument("doc-c", 0.5)]),
                ("q-1", [new ScoredDocument("doc-z", 1.0)])),
            "ragnet-default");

        Assert.Equal(
            "q-1 Q0 doc-z 1 1 ragnet-default\n" +
            "q-2 Q0 doc-b 1 0.75 ragnet-default\n" +
            "q-2 Q0 doc-a 2 0.75 ragnet-default\n" +
            "q-2 Q0 doc-c 3 0.5 ragnet-default\n",
            File.ReadAllText(RunPath),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Write_AnEmptyRunEmitsTheEmptyRunMarkerLine()
    {
        // TREC's one-line-per-document format has no native way to say "this query retrieved
        // nothing" — zero lines is indistinguishable from a query the entrant never saw. The
        // marker keeps the distinction on disk.
        TrecRunFile.Write(RunPath, Runs(("q-empty", [])), "ragnet-default");

        Assert.Equal(
            $"q-empty Q0 {TrecRunFile.EmptyRunDocumentId} 1 0 ragnet-default\n",
            File.ReadAllText(RunPath),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Write_UsesTheInvariantDecimalSeparatorUnderACommaCulture()
    {
        // This machine's culture writes 0,5. A run file with commas either breaks the reader's
        // field count expectations downstream or silently reads as a different score — so the
        // writer is pinned to '.' regardless of where it runs.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            TrecRunFile.Write(RunPath, Runs(("q-1", [new ScoredDocument("doc-1", 0.5)])), "tag");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var content = File.ReadAllText(RunPath);
        Assert.Contains("0.5", content, StringComparison.Ordinal);
        Assert.DoesNotContain(",", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RejectsARunTagContainingWhitespace()
    {
        // A tag with a space adds a seventh field to every line; trec_eval and our own reader
        // would both misparse the file.
        _ = Assert.Throws<ArgumentException>(() =>
            TrecRunFile.Write(RunPath, Runs(("q-1", [new ScoredDocument("doc-1", 0.5)])), "two words"));
    }

    [Fact]
    public void Write_RejectsADocumentIdContainingWhitespace()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            TrecRunFile.Write(RunPath, Runs(("q-1", [new ScoredDocument("doc 1", 0.5)])), "tag"));
    }

    [Fact]
    public void Write_RejectsADuplicateDocumentIdWithinAQuery()
    {
        // IrMetrics would award the document's gain twice. DocumentRanking dedupes, so a
        // duplicate here means the entrant bypassed pooling — a harness bug, not a ranking.
        _ = Assert.Throws<ArgumentException>(() =>
            TrecRunFile.Write(
                RunPath,
                Runs(("q-1", [new ScoredDocument("doc-1", 0.9), new ScoredDocument("doc-1", 0.5)])),
                "tag"));
    }

    [Fact]
    public void Write_RejectsAScoreThatRisesBelowAnEarlierRank()
    {
        // Rank order is authoritative, but trec_eval ignores the rank column and sorts by score.
        // A file whose scores rise where its ranks fall would be scored one way by IrMetrics and
        // another by trec_eval — the exact divergence the shared boundary exists to prevent.
        _ = Assert.Throws<ArgumentException>(() =>
            TrecRunFile.Write(
                RunPath,
                Runs(("q-1", [new ScoredDocument("doc-1", 0.5), new ScoredDocument("doc-2", 0.9)])),
                "tag"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Write_RejectsANonFiniteScore(double score)
    {
        _ = Assert.Throws<ArgumentException>(() =>
            TrecRunFile.Write(RunPath, Runs(("q-1", [new ScoredDocument("doc-1", score)])), "tag"));
    }

    [Fact]
    public void Write_RejectsTheEmptyRunMarkerAsARealDocumentId()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            TrecRunFile.Write(
                RunPath,
                Runs(("q-1", [new ScoredDocument(TrecRunFile.EmptyRunDocumentId, 0.5)])),
                "tag"));
    }

    [Fact]
    public void Write_RejectsAnEntrantWithNoQueries()
    {
        // Zero queries is not an empty ranking, it is a harness that lost its query set — the
        // same corruption-over-emptiness stance BeirLoader takes on an empty corpus.
        _ = Assert.Throws<ArgumentException>(() => TrecRunFile.Write(RunPath, Runs(), "tag"));
    }

    // ------------------------------------------------------------- round trips

    [Fact]
    public void RoundTrip_PreservesRankOrder()
    {
        TrecRunFile.Write(
            RunPath,
            Runs(
                ("q-1", [new ScoredDocument("doc-3", 0.9), new ScoredDocument("doc-1", 0.7), new ScoredDocument("doc-2", 0.1)]),
                ("q-2", [new ScoredDocument("doc-2", 0.4)])),
            "tag");

        var runs = TrecRunFile.Read(RunPath);

        Assert.Equal(2, runs.Count);
        Assert.Equal(["doc-3", "doc-1", "doc-2"], runs["q-1"]);
        Assert.Equal(["doc-2"], runs["q-2"]);
    }

    [Fact]
    public void RoundTrip_TiedScoresKeepTheirEmittedOrder()
    {
        // doc-b is ranked above doc-a at the same score, so a reader that re-sorts by score —
        // most break ties by document id — would flip them, and that flip changes nDCG. The
        // emitted order is the ranking; the score column must not be able to override it.
        TrecRunFile.Write(
            RunPath,
            Runs(("q-1", [new ScoredDocument("doc-b", 0.5), new ScoredDocument("doc-a", 0.5)])),
            "tag");

        Assert.Equal(["doc-b", "doc-a"], TrecRunFile.Read(RunPath)["q-1"]);
    }

    [Fact]
    public void RoundTrip_AnEmptyRunIsDistinctFromAMissingQuery()
    {
        // IrMetrics.Evaluate distinguishes a query present with an empty ranking from a query
        // absent from the dictionary — the empty run counts toward the run set it was part of.
        // A library that legitimately returned nothing must not read back as one that was never
        // asked.
        TrecRunFile.Write(
            RunPath,
            Runs(("q-empty", []), ("q-full", [new ScoredDocument("doc-1", 0.5)])),
            "tag");

        var runs = TrecRunFile.Read(RunPath);

        Assert.Equal(2, runs.Count);
        Assert.Empty(runs["q-empty"]);
        Assert.Equal(["doc-1"], runs["q-full"]);
        Assert.False(runs.ContainsKey("q-ghost"));
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public void Read_ReadsADottedScoreUnderACommaCulture()
    {
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 1 0.5 tag\n");

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            Assert.Equal(["doc-1"], TrecRunFile.Read(RunPath)["q-1"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Read_AcceptsTabSeparatedFields()
    {
        // The TREC format is whitespace-separated, and Python's '\t'.join is the most common
        // writer on the Stage 2 side of this boundary.
        File.WriteAllText(RunPath, "q-1\tQ0\tdoc-1\t1\t0.5\ttag\n");

        Assert.Equal(["doc-1"], TrecRunFile.Read(RunPath)["q-1"]);
    }

    [Fact]
    public void Read_TooFewFieldsFailsLoudlyNamingFileAndLine()
    {
        // The central failure mode: a reader that skips this line reports a ranking one document
        // short, which is a different nDCG with no error anywhere.
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 1 0.9 tag\nq-1 Q0 doc-2 2 0.5\n");

        var ex = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));

        Assert.Contains(RunPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ANonNumericRankFailsLoudly()
    {
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 first 0.9 tag\n");

        var ex = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));

        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Read_ANonPositiveRankFailsLoudly(string rank)
    {
        // TREC ranks are 1-based; a 0 usually means a writer emitted its 0-based loop index,
        // which would shift every rank in the file by one.
        File.WriteAllText(RunPath, $"q-1 Q0 doc-1 {rank} 0.9 tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_ARankGapFailsLoudly()
    {
        // Ranks 1, 2, 4: the reader's output is a list whose position IS the rank, so the
        // document claiming rank 4 would silently become rank 3. A gap means the file disagrees
        // with itself about the ranking — usually a filtered or truncated write — and the honest
        // response is to refuse, not to renumber.
        File.WriteAllText(
            RunPath,
            "q-1 Q0 doc-1 1 0.9 tag\nq-1 Q0 doc-2 2 0.8 tag\nq-1 Q0 doc-4 4 0.6 tag\n");

        var ex = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));

        Assert.Contains("line 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AFirstRankOtherThanOneFailsLoudly()
    {
        File.WriteAllText(RunPath, "q-1 Q0 doc-2 2 0.9 tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_AMissingQ0LiteralFailsLoudly()
    {
        // Q0 is a fixed literal; anything else in that column means the columns are not what
        // this reader thinks they are — likely a qrels file, whose third column is a judgement.
        File.WriteAllText(RunPath, "q-1 0 doc-1 1 0.9 tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_ADuplicateDocumentIdFailsLoudly()
    {
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 1 0.9 tag\nq-1 Q0 doc-1 2 0.8 tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0,5")]
    public void Read_ANonNumericScoreFailsLoudly(string score)
    {
        // "0,5" is what a culture-sensitive writer on this machine would emit. It must be
        // rejected, not read as 5 — parsing is pinned to the invariant culture.
        File.WriteAllText(RunPath, $"q-1 Q0 doc-1 1 {score} tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_AScoreThatRisesAgainstTheRanksFailsLoudly()
    {
        // Same self-disagreement the writer refuses to produce: IrMetrics would score the rank
        // order, trec_eval would score the score order, and the published file would mean two
        // different rankings depending on who reads it.
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 1 0.5 tag\nq-1 Q0 doc-2 2 0.9 tag\n");

        var ex = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));

        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AMixedRunTagFailsLoudly()
    {
        // Two tags in one file is the signature of two concatenated run files — two entrants'
        // rankings scored as one.
        File.WriteAllText(RunPath, "q-1 Q0 doc-1 1 0.9 tag-a\nq-2 Q0 doc-2 1 0.8 tag-b\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_TheEmptyRunMarkerBesideRealDocumentsFailsLoudly()
    {
        // The marker asserts "this query retrieved nothing"; a real document for the same query
        // contradicts it in either order.
        File.WriteAllText(
            RunPath,
            $"q-1 Q0 {TrecRunFile.EmptyRunDocumentId} 1 0 tag\nq-1 Q0 doc-1 1 0.9 tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));

        File.WriteAllText(
            RunPath,
            $"q-1 Q0 doc-1 1 0.9 tag\nq-1 Q0 {TrecRunFile.EmptyRunDocumentId} 2 0 tag\n");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_AnEmptyFileFailsLoudly()
    {
        // An empty run file scores zero on every judged query, which reads as total retrieval
        // failure rather than as the harness having written nothing — BeirLoader's stance on an
        // empty corpus, applied to the other side of the boundary.
        File.WriteAllText(RunPath, "");

        _ = Assert.Throws<InvalidDataException>(() => TrecRunFile.Read(RunPath));
    }

    [Fact]
    public void Read_InterleavedQueriesKeepPerQueryRankOrder()
    {
        // The format does not require a query's lines to be contiguous, and trec_eval does not
        // care — only each query's own rank sequence is constrained.
        File.WriteAllText(
            RunPath,
            "q-1 Q0 doc-1 1 0.9 tag\nq-2 Q0 doc-9 1 0.8 tag\nq-1 Q0 doc-2 2 0.7 tag\n");

        var runs = TrecRunFile.Read(RunPath);

        Assert.Equal(["doc-1", "doc-2"], runs["q-1"]);
        Assert.Equal(["doc-9"], runs["q-2"]);
    }
}
