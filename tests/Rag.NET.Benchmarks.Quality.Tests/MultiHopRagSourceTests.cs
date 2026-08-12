using System.Globalization;
using System.Text;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="MultiHopRagConversion"/> — the half of <see cref="MultiHopRagSource"/> that turns
/// two HuggingFace JSON files into BEIR's on-disk layout — against <b>small synthetic fixtures built
/// here</b>. Nothing in this file touches the network and no dataset byte is committed.
/// <para>
/// Every test below is about a way the conversion could produce a smaller dataset than it was given
/// and still look plausible. A dropped evidence row, a short corpus or a half-written directory all
/// end as a believable retrieval figure computed from less data than the number claims, which is the
/// most expensive kind of wrong this harness can be.
/// </para>
/// </summary>
public sealed class MultiHopRagSourceTests : IDisposable
{
    private const string Alpha = "https://example.invalid/alpha";
    private const string Beta = "https://example.invalid/beta";
    private const string Gamma = "https://example.invalid/gamma";
    private const string Missing = "https://example.invalid/never-published";

    private readonly string _root;

    public MultiHopRagSourceTests() =>
        _root = Path.Combine(Path.GetTempPath(), "ragnet-multihop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void PublishedFiles_AreThePinnedRevisionsTwoJsonFiles()
    {
        // The revision is pinned rather than tracking main: MultiHop-RAG is a live HuggingFace
        // repository, and a re-published corpus.json would silently change every figure measured
        // against it. The lengths and digests below were measured from these exact URLs.
        Assert.Equal(
            "https://huggingface.co/datasets/yixuantt/MultiHopRAG/resolve/" +
            "71ac0d0bd1f951d2d6b70311f7d2ae404e1ffa82/corpus.json",
            MultiHopRagSource.CorpusUrl.ToString(),
            StringComparer.Ordinal);

        Assert.Equal(
            "https://huggingface.co/datasets/yixuantt/MultiHopRAG/resolve/" +
            "71ac0d0bd1f951d2d6b70311f7d2ae404e1ffa82/MultiHopRAG.json",
            MultiHopRagSource.QueriesUrl.ToString(),
            StringComparer.Ordinal);
    }

    [Fact]
    public void PublishedCounts_AreWhatTheTwoFilesActuallyContain()
    {
        // Measured from the pinned revision on 2026-08-12, not read off the paper: 609 articles,
        // 2,556 queries of which 301 are null_query and therefore judged by nothing, and 5,908
        // distinct (query, document) pairs over the remaining 2,255.
        Assert.Equal(new MultiHopRagCounts(609, 2556, 2255, 5908), MultiHopRagSource.PublishedCounts);
    }

    [Fact]
    public async Task ConvertAsync_ANullQuery_IsWrittenToQueriesButJudgedByNothing()
    {
        // BEIR's own convention, not an accommodation: SciFact ships 1,109 queries and judges 300.
        // The 301 null queries stay in queries.jsonl so an id still maps to a line in the source
        // file; they simply earn no qrels row, and the harness retrieves only judged queries.
        var directory = await ConvertAsync(
            Corpus((Alpha, "Alpha", "Alpha body."), (Beta, "Beta", "Beta body.")),
            Queries(
                ("Who bought what?", "inference_query", [Alpha, Beta]),
                ("Insufficient information.", "null_query", [])),
            new MultiHopRagCounts(2, 2, 1, 2));

        var queries = BeirLoader.LoadQueries(Path.Combine(directory, "queries.jsonl"));
        var qrels = BeirLoader.LoadQrels(Path.Combine(directory, "qrels", "test.tsv"));

        Assert.Equal(2, queries.Count);
        Assert.Contains(queries, query => string.Equals(query.Id, "mhr-0001", StringComparison.Ordinal));
        Assert.False(qrels.ContainsKey("mhr-0001"));
        Assert.Equal(2, qrels["mhr-0000"].Count);
    }

    [Fact]
    public async Task ConvertAsync_AnEvidenceRowCitingNoDocument_ThrowsNamingTheUrl()
    {
        // Skipping the row instead would write a smaller qrels file and report a plausible, wrong
        // number: recall computed against judgements the dataset does not actually contain.
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ConvertAsync(
            Corpus((Alpha, "Alpha", "Alpha body.")),
            Queries(("Who bought what?", "inference_query", [Alpha, Missing])),
            new MultiHopRagCounts(1, 1, 1, 2)));

        Assert.Contains(Missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertAsync_ACorpusShorterThanPublished_ThrowsNamingExpectedAndActual()
    {
        // The self-assertion exists because a converter that silently writes less still writes
        // something that loads, scores and gets published.
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ConvertAsync(
            Corpus((Alpha, "Alpha", "Alpha body.")),
            Queries(("Who bought what?", "inference_query", [Alpha])),
            new MultiHopRagCounts(609, 1, 1, 1)));

        Assert.Contains("609", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertAsync_TwoCorpusEntriesSharingAUrl_ThrowsNamingIt()
    {
        // The document id is the url, so a repeated url is two documents sharing one _id: every
        // judgement naming it becomes ambiguous and one of the two articles is unreachable.
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ConvertAsync(
            Corpus((Alpha, "Alpha", "Alpha body."), (Alpha, "Alpha again", "Different body.")),
            Queries(("Who bought what?", "inference_query", [Alpha])),
            new MultiHopRagCounts(2, 1, 1, 1)));

        Assert.Contains(Alpha, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertAsync_ItsOwnOutput_LoadsAsABeirDataset()
    {
        var directory = await ConvertAsync(
            Corpus((Alpha, "Alpha", "Alpha body."), (Beta, "Beta", "Beta body."), (Gamma, "Gamma", "Gamma body.")),
            Queries(
                ("Who bought what?", "inference_query", [Alpha, Beta]),
                ("Which two agree?", "comparison_query", [Beta, Gamma]),
                ("Insufficient information.", "null_query", []),
                ("When did it happen?", "temporal_query", [Gamma])),
            new MultiHopRagCounts(3, 4, 3, 5));

        var dataset = BeirLoader.Load(directory);

        Assert.Equal(3, dataset.Documents.Count);
        Assert.Equal(4, dataset.Queries.Count);
        Assert.Equal(3, dataset.JudgedQueryCount);
        Assert.Equal("Alpha Alpha body.", dataset.Documents[0].RetrievalText, StringComparer.Ordinal);
        Assert.Equal(Alpha, dataset.Documents[0].Id, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ConvertAsync_AQueryCitingOneDocumentTwice_WritesOneQrelsRow()
    {
        // 6,084 evidence rows collapse to 5,908 distinct pairs upstream, because a query can cite
        // two different facts from the same article. The judgement is binary, so the second row
        // would be a duplicate _id in the same query's judgements rather than a stronger one.
        var directory = await ConvertAsync(
            Corpus((Alpha, "Alpha", "Alpha body.")),
            Queries(("Who bought what?", "inference_query", [Alpha, Alpha])),
            new MultiHopRagCounts(1, 1, 1, 1));

        var rows = File.ReadAllLines(Path.Combine(directory, "qrels", "test.tsv"));

        Assert.Equal(BeirLoader.QrelsHeader, rows[0], StringComparer.Ordinal);
        Assert.Equal("mhr-0000\t" + Alpha + "\t1", rows[1], StringComparer.Ordinal);
        Assert.Equal(2, rows.Length);
    }

    [Fact]
    public async Task ConvertAsync_AFailurePartWayThrough_LeavesNothingTheCacheWouldAccept()
    {
        // BeirDatasetCache.IsPresent checks corpus.jsonl, queries.jsonl and that qrels/ is a
        // directory — it does NOT check qrels/test.tsv. Writing the three files straight into the
        // dataset directory and failing after the first two would therefore make every later run
        // see a cache hit, skip acquisition and fail inside BeirLoader, permanently. So the
        // conversion stages and renames, and a failure publishes nothing at all.
        var datasetDirectory = Path.Combine(_root, "multihop-rag");

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => MultiHopRagConversion.ConvertAsync(
            WriteFixture("corpus.json", Corpus((Alpha, "Alpha", "Alpha body."))),
            WriteFixture("MultiHopRAG.json", Queries(("Who bought what?", "inference_query", [Missing]))),
            datasetDirectory,
            new MultiHopRagCounts(1, 1, 1, 1),
            TestContext.Current.CancellationToken));

        var cache = new BeirDatasetCache(_root);

        Assert.False(cache.IsPresent(Descriptor("multihop-rag")));
        Assert.False(Directory.Exists(datasetDirectory));
        Assert.Empty(Directory.GetDirectories(_root, "*.converting"));
    }

    /// <summary>
    /// Converts one pair of fixture files into a dataset directory under <see cref="_root"/>.
    /// </summary>
    /// <returns>The dataset directory the conversion published.</returns>
    private async Task<string> ConvertAsync(string corpus, string queries, MultiHopRagCounts expected)
    {
        var datasetDirectory = Path.Combine(_root, "multihop-rag");

        await MultiHopRagConversion.ConvertAsync(
            WriteFixture("corpus.json", corpus),
            WriteFixture("MultiHopRAG.json", queries),
            datasetDirectory,
            expected,
            TestContext.Current.CancellationToken);

        return datasetDirectory;
    }

    private string WriteFixture(string fileName, string content)
    {
        var directory = Path.Combine(_root, "published");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// Builds a <c>corpus.json</c> in MultiHop-RAG's shape: an array of articles carrying
    /// <c>author</c>, <c>body</c>, <c>category</c>, <c>published_at</c>, <c>source</c>, <c>title</c>
    /// and <c>url</c>. Only the last three are read; the rest are present so the fixture is the
    /// shape the converter will actually meet.
    /// </summary>
    private static string Corpus(params (string Url, string Title, string Body)[] articles)
    {
        var builder = new StringBuilder("[");

        for (var index = 0; index < articles.Length; index++)
        {
            var (url, title, body) = articles[index];
            _ = builder
                .Append(index == 0 ? "\n" : ",\n")
                .Append(CultureInfo.InvariantCulture, $$"""
                    {"author":"A. Writer","body":"{{body}}","category":"technology","published_at":"2023-09-01T00:00:00Z","source":"The Wire","title":"{{title}}","url":"{{url}}"}
                    """);
        }

        return builder.Append("\n]").ToString();
    }

    /// <summary>
    /// Builds a <c>MultiHopRAG.json</c> in MultiHop-RAG's shape: an array of queries carrying
    /// <c>answer</c>, <c>evidence_list</c>, <c>query</c> and <c>question_type</c>, where each
    /// evidence row repeats an article's keys and adds <c>fact</c>.
    /// </summary>
    private static string Queries(params (string Query, string Type, string[] EvidenceUrls)[] queries)
    {
        var builder = new StringBuilder("[");

        for (var index = 0; index < queries.Length; index++)
        {
            var (query, type, evidenceUrls) = queries[index];
            _ = builder
                .Append(index == 0 ? "\n" : ",\n")
                .Append(CultureInfo.InvariantCulture, $$"""
                    {"query":"{{query}}","answer":"An answer.","question_type":"{{type}}","evidence_list":[{{Evidence(evidenceUrls)}}]}
                    """);
        }

        return builder.Append("\n]").ToString();
    }

    private static string Evidence(string[] urls)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < urls.Length; index++)
        {
            _ = builder
                .Append(index == 0 ? "" : ",")
                .Append(CultureInfo.InvariantCulture, $$"""
                    {"author":"A. Writer","body":"Cited body.","category":"technology","fact":"A cited fact.","published_at":"2023-09-01T00:00:00Z","source":"The Wire","title":"Cited","url":"{{urls[index]}}"}
                    """);
        }

        return builder.ToString();
    }

    /// <summary>A descriptor whose only job is to give <see cref="BeirDatasetCache"/> a name.</summary>
    private static BeirDatasetDescriptor Descriptor(string name) => new(
        name,
        new Uri("https://example.invalid/datasets/" + name + ".zip"),
        "00000000000000000000000000000000",
        "test fixture",
        DocumentCount: 1,
        QueryCount: 1,
        TestQueryCount: 1,
        TitledDocumentCount: 1,
        ExcludesSelfRetrievedDocument: false,
        ParityTarget: new BeirParityTarget(0.5, "test fixture; never measured"));
}
