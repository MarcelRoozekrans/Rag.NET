using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="BeirLoader"/> against hand-written fixtures — three documents, two queries and a
/// qrels file with a header — written to a temporary directory. <b>No network.</b> The real download
/// is exercised only by the env-gated parity test.
/// <para>
/// Two details in here move the parity number rather than breaking anything visibly: how title and
/// text are joined, and whether the TSV's header row is treated as a header. Both get their own
/// tests for that reason.
/// </para>
/// </summary>
public sealed class BeirLoaderTests : IDisposable
{
    private const string CorpusJsonl = """
        {"_id": "doc-1", "title": "Cerebral white matter", "text": "Alterations of the architecture.", "metadata": {}}
        {"_id": "doc-2", "title": "", "text": "An abstract that carries no title.", "metadata": {}}
        {"_id": "doc-3", "title": "Third", "text": "Third body.", "metadata": {}}
        """;

    private const string QueriesJsonl = """
        {"_id": "q-1", "text": "0-dimensional biomaterials lack inductive properties.", "metadata": {}}
        {"_id": "q-2", "text": "A second claim.", "metadata": {}}
        """;

    private const string QrelsTsv = "query-id\tcorpus-id\tscore\nq-1\tdoc-1\t1\nq-2\tdoc-2\t1\nq-2\tdoc-3\t0\n";

    private readonly string _directory;

    public BeirLoaderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ragnet-beir-loader-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(_directory, "scifact", "qrels"));

        Write("scifact/corpus.jsonl", CorpusJsonl);
        Write("scifact/queries.jsonl", QueriesJsonl);
        Write("scifact/qrels/test.tsv", QrelsTsv);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void LoadCorpus_JoinsTitleAndTextWithASingleSpace()
    {
        // BEIR's concatenation, and it decides what gets embedded — so it shifts the parity number
        // rather than failing. Pinned as an exact string, not as "contains the title".
        //
        // A SPACE, not the newline this phase's design and plan both assert: BEIR's own
        // SentenceBERT declares sep = " " and computes (title + sep + text).strip(), and the
        // published ≈ 0.645 was produced that way. The design was checked against upstream and
        // corrected rather than followed.
        var documents = BeirLoader.LoadCorpus(At("scifact/corpus.jsonl"));

        Assert.Equal(
            "Cerebral white matter Alterations of the architecture.",
            documents[0].RetrievalText,
            StringComparer.Ordinal);
    }

    [Fact]
    public void LoadCorpus_AnEmptyTitleLeavesNoLeadingSeparator()
    {
        // BEIR computes (title + sep + text).strip(), so a document with no title embeds its text
        // alone. A stray leading space would be a difference in every such document's input.
        var documents = BeirLoader.LoadCorpus(At("scifact/corpus.jsonl"));

        Assert.Equal("An abstract that carries no title.", documents[1].RetrievalText, StringComparer.Ordinal);
    }

    [Fact]
    public void LoadCorpus_TheSeparatorCanBeOverridden()
    {
        // The parameter survives the correction of the default because the counterfactual is worth
        // being able to measure: the SciFact parity run was scored under both separators. Phase 3.13
        // then made them agree — the 0.00314 the newline used to cost was the tokenizer deleting it
        // and merging each title's last word into its abstract's first, not the separator — so both
        // now give 0.64593. Recorded on BeirLoader.DefaultTitleTextSeparator. Datasets with longer,
        // more structured titles are where the choice starts to matter.
        var documents = BeirLoader.LoadCorpus(At("scifact/corpus.jsonl"), titleTextSeparator: "\n");

        Assert.Equal(
            "Cerebral white matter\nAlterations of the architecture.",
            documents[0].RetrievalText,
            StringComparer.Ordinal);
    }

    [Fact]
    public void LoadCorpus_ReadsEveryDocumentInFileOrderWithItsIdTitleAndText()
    {
        var documents = BeirLoader.LoadCorpus(At("scifact/corpus.jsonl"));

        Assert.Equal(["doc-1", "doc-2", "doc-3"], documents.Select(static document => document.Id), StringComparer.Ordinal);
        Assert.Equal("Cerebral white matter", documents[0].Title, StringComparer.Ordinal);
        Assert.Equal("Alterations of the architecture.", documents[0].Text, StringComparer.Ordinal);
    }

    [Fact]
    public void LoadCorpus_ALineWithoutTextThrowsAndNamesTheLine()
    {
        Write("broken.jsonl", """{"_id": "doc-1", "title": "No body"}""");

        var ex = Assert.Throws<InvalidDataException>(() => BeirLoader.LoadCorpus(At("broken.jsonl")));

        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCorpus_AnEmptyFileThrowsRatherThanReturningAnEmptyCorpus()
    {
        // The truncated-download symptom seen from the loader end. An empty corpus scores zero on
        // every query, which reads as a retrieval failure; this names the real cause instead.
        Write("empty.jsonl", "");

        var ex = Assert.Throws<InvalidDataException>(() => BeirLoader.LoadCorpus(At("empty.jsonl")));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadCorpus_ALineThatIsNotJsonThrows()
    {
        Write("garbage.jsonl", "<!DOCTYPE html><html>not a dataset</html>");

        Assert.Throws<InvalidDataException>(() => BeirLoader.LoadCorpus(At("garbage.jsonl")));
    }

    [Fact]
    public void LoadQueries_ReadsEveryQueryWithItsIdAndText()
    {
        var queries = BeirLoader.LoadQueries(At("scifact/queries.jsonl"));

        Assert.Equal(["q-1", "q-2"], queries.Select(static query => query.Id), StringComparer.Ordinal);
        Assert.Equal(
            "0-dimensional biomaterials lack inductive properties.", queries[0].Text, StringComparer.Ordinal);
    }

    [Fact]
    public void LoadQrels_SkipsTheHeaderRowSoTheFirstJudgementIsNotOffByOne()
    {
        // The TSV really does have a header — 'query-id\tcorpus-id\tscore'. Keeping it would make the
        // literal text 'query-id' a query id, and would push every judgement's meaning one row out of
        // place for anyone reading positionally. Both mistakes are worth exactly one query out of
        // 300, which no mean would show.
        var qrels = BeirLoader.LoadQrels(At("scifact/qrels/test.tsv"));

        Assert.DoesNotContain("query-id", qrels.Keys, StringComparer.Ordinal);
        Assert.Equal(2, qrels.Count);
        Assert.Equal(1, qrels["q-1"]["doc-1"]);
    }

    [Fact]
    public void LoadQrels_AFileWithNoHeaderThrowsRatherThanEatingItsFirstJudgement()
    {
        // The other half of the same off-by-one. Blindly dropping line 1 would silently discard
        // q-1/doc-1 here and leave a plausible-looking result behind.
        Write("headerless.tsv", "q-1\tdoc-1\t1\nq-2\tdoc-2\t1\n");

        var ex = Assert.Throws<InvalidDataException>(() => BeirLoader.LoadQrels(At("headerless.tsv")));

        Assert.Contains("header", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("query-id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadQrels_AMisspeltHeaderThrows()
    {
        Write("wrong-header.tsv", "queryid\tcorpusid\tscore\nq-1\tdoc-1\t1\n");

        Assert.Throws<InvalidDataException>(() => BeirLoader.LoadQrels(At("wrong-header.tsv")));
    }

    [Fact]
    public void LoadQrels_GroupsEveryJudgementForAQueryUnderThatQuery()
    {
        var qrels = BeirLoader.LoadQrels(At("scifact/qrels/test.tsv"));

        Assert.Equal(2, qrels["q-2"].Count);
        Assert.Equal(1, qrels["q-2"]["doc-2"]);
    }

    [Fact]
    public void LoadQrels_KeepsZeroScoredJudgements()
    {
        // A 0 says "judged, and not relevant", which is different from "never judged". IrMetrics is
        // what decides it earns no gain; dropping it here would erase the distinction upstream of
        // every metric.
        var qrels = BeirLoader.LoadQrels(At("scifact/qrels/test.tsv"));

        Assert.Equal(0, qrels["q-2"]["doc-3"]);
    }

    [Fact]
    public void LoadQrels_PreservesGradedScores()
    {
        Write("graded.tsv", "query-id\tcorpus-id\tscore\nq-1\tdoc-1\t2\nq-1\tdoc-2\t1\n");

        var qrels = BeirLoader.LoadQrels(At("graded.tsv"));

        Assert.Equal(2, qrels["q-1"]["doc-1"]);
        Assert.Equal(1, qrels["q-1"]["doc-2"]);
    }

    [Fact]
    public void LoadQrels_ANonIntegerScoreThrowsAndNamesTheLine()
    {
        Write("bad-score.tsv", "query-id\tcorpus-id\tscore\nq-1\tdoc-1\trelevant\n");

        var ex = Assert.Throws<InvalidDataException>(() => BeirLoader.LoadQrels(At("bad-score.tsv")));

        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadQrels_ARowWithoutThreeColumnsThrows()
    {
        Write("short-row.tsv", "query-id\tcorpus-id\tscore\nq-1\tdoc-1\n");

        var ex = Assert.Throws<InvalidDataException>(() => BeirLoader.LoadQrels(At("short-row.tsv")));

        Assert.Contains("three", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadQrels_AHeaderWithNoJudgementsThrows()
    {
        Write("header-only.tsv", "query-id\tcorpus-id\tscore\n");

        Assert.Throws<InvalidDataException>(() => BeirLoader.LoadQrels(At("header-only.tsv")));
    }

    [Fact]
    public void Load_ReadsCorpusQueriesAndQrelsFromABeirDirectoryLayout()
    {
        var dataset = BeirLoader.Load(At("scifact"));

        Assert.Equal("scifact", dataset.Name, StringComparer.Ordinal);
        Assert.Equal("test", dataset.Split, StringComparer.Ordinal);
        Assert.Equal(3, dataset.Documents.Count);
        Assert.Equal(2, dataset.Queries.Count);
        Assert.Equal(2, dataset.JudgedQueryCount);
    }

    [Fact]
    public void Load_QrelsPlugStraightIntoIrMetrics()
    {
        // The two halves of the harness meet here: whatever LoadQrels returns must be the shape
        // Evaluate takes, or the seam would only be discovered during the parity run.
        var dataset = BeirLoader.Load(At("scifact"));

        var runs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["q-1"] = ["doc-1"],
            ["q-2"] = ["doc-2"],
        };

        var evaluation = IrMetrics.Evaluate(runs, dataset.Qrels, k: 10);

        Assert.Equal(1.0, evaluation.NormalizedDiscountedCumulativeGain, 5);
        Assert.Equal(2, evaluation.EvaluatedQueryCount);
    }

    // The descriptor's own assertions — checksum, counts, licence and, since Phase 3.12, the parity
    // target — moved to BeirDatasetDescriptorTests when the parity test became a theory over
    // datasets. Nothing about them belongs next to the loader's fixtures.

    private string At(string relativePath) =>
        Path.Combine(_directory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relativePath, string content) =>
        File.WriteAllText(At(relativePath), content);
}
