using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;
using Xunit.Sdk;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The reranked row's ordering and its reordering guard, exercised offline against a hand-built
/// three-document fixture — no ONNX model, no downloaded corpus.
/// <para>
/// Offline is possible for the same reason it was for <see cref="HybridBm25AblationRowTests"/>:
/// the query vector is pre-warmed into <see cref="EmbeddingCache"/> so the generator is never
/// consulted, and the cross-encoder is a scripted <see cref="IReranker"/> so the sorting, the
/// score-carrying and the counters — the row's own logic — are what these tests pin. The real
/// <c>OnnxReranker</c> is exercised by <see cref="BeirAblationTests"/> under the measurement gate.
/// </para>
/// <para>
/// The guard tests matter more than the ordering ones. A cross-encoder that returns its input
/// order — misconfigured, wrong output tensor, scores all equal — produces a row identical to the
/// dense row above it: a passing test displaying a meaningless number.
/// <see cref="RerankedAblationRow.AssertRerankerReordered"/> exists to turn that silence into a
/// failure, and these tests prove it fails when it should and stays quiet when it should not.
/// </para>
/// </summary>
public sealed class RerankedAblationRowTests : IDisposable
{
    private const string ModelIdentity = "fixture-model/hand-warmed-vectors";

    private readonly string _root;
    private readonly EmbeddingCache _embeddings;
    private readonly InMemoryVectorStore _store = new();

    public RerankedAblationRowTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "ragnet-reranked-row-" + Guid.NewGuid().ToString("N"));
        _embeddings = new EmbeddingCache(_root, ModelIdentity);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Name_NamesTheCrossEncoderAndBothTokenLimits()
    {
        // The label IS the deliverable: it names the model the cell was scored by, and it states
        // both truncation lengths — the reranker scores pairs truncated at 512 tokens while the
        // dense leg's embedder truncates at 256 — so a reader comparing the two rows does not
        // conclude that one of the limits is a bug. Different models doing different jobs.
        var row = new RerankedAblationRow(new ScriptedReranker(ConstantScores));

        Assert.Contains("ms-marco-MiniLM-L6-v2", row.Name, StringComparison.Ordinal);
        Assert.Contains("512", row.Name, StringComparison.Ordinal);
        Assert.Contains("256", row.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrieveAsync_ReordersByCrossEncoderScore_AndCarriesItsScores()
    {
        // Dense (cosine against e1..e3 for q = [0.9, 0.5, 0.3, 0.05], TopK 3): d1, d2, d3.
        // The scripted cross-encoder scores by ascending input index — 0, 1, 2 — so the reranked
        // order is the exact reversal: d3, d2, d1. The hits must carry the CROSS-ENCODER scores,
        // not the cosine ones: DocumentRanking max-pools by hit score, so hits that kept their
        // cosine scores would be re-sorted straight back into the dense order downstream.
        var units = ThreeUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        var row = new RerankedAblationRow(new ScriptedReranker(AscendingScores));

        var hits = await RetrieveAsync(row, "zebra quartz", topK: 3);

        Assert.Equal(["d3#0", "d2#0", "d1#0"], ChunkIds(hits));
        Assert.Equal(2.0, hits[0].Score);
        Assert.Equal(1.0, hits[1].Score);
        Assert.Equal(0.0, hits[2].Score);
        Assert.Equal("d3", hits[0].DocumentId);
        Assert.Equal(1, row.QueryCount);
        Assert.Equal(1, row.ReorderedQueryCount);
    }

    [Fact]
    public async Task RetrieveAsync_WhenScoresAllEqual_PreservesTheDenseOrder_AndCountsNoReorder()
    {
        // All-equal scores are the guard's target degradation: the tie-break is the input (dense)
        // rank, deterministically, so the reranked list IS the dense list and the row records that
        // no reordering happened rather than letting an unstable sort disguise it as one.
        var units = ThreeUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        var row = new RerankedAblationRow(new ScriptedReranker(ConstantScores));

        var hits = await RetrieveAsync(row, "zebra quartz", topK: 3);

        Assert.Equal(["d1#0", "d2#0", "d3#0"], ChunkIds(hits));
        Assert.Equal(0.5, hits[0].Score);
        Assert.Equal(1, row.QueryCount);
        Assert.Equal(0, row.ReorderedQueryCount);
    }

    [Fact]
    public void AssertRerankerReordered_FailsWhenTheRowRetrievedNothing()
    {
        var row = new RerankedAblationRow(new ScriptedReranker(AscendingScores));

        var failure = Record.Exception(() => row.AssertRerankerReordered("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("RETRIEVED NOTHING", assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertRerankerReordered_FailsNamingTheDegradation_WhenNothingReordered()
    {
        var units = ThreeUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        var row = new RerankedAblationRow(new ScriptedReranker(ConstantScores));
        _ = await RetrieveAsync(row, "zebra quartz", topK: 3);

        var failure = Record.Exception(() => row.AssertRerankerReordered("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("RERANKER CHANGED NOTHING", assertion.Message, StringComparison.Ordinal);
        Assert.Contains("0 of 1", assertion.Message, StringComparison.Ordinal);
        Assert.Contains("dense", assertion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertRerankerReordered_FailsWhenFewerThanHalfTheQueriesWereReordered()
    {
        // One reordered query out of three is below the meaningful-fraction threshold of half.
        // Two rankings of ten-plus candidates produced by different models agree exactly almost
        // never, so a cross-encoder that leaves most queries untouched is one whose scores are
        // nearly constant — broken in a way the zero check alone would not catch.
        var units = ThreeUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        await WarmQueryAsync("alpha beta");
        await WarmQueryAsync("delta gamma");
        var row = new RerankedAblationRow(
            new ScriptedReranker(static (query, results) => query.StartsWith("zebra", StringComparison.Ordinal)
                ? AscendingScores(query, results)
                : ConstantScores(query, results)));
        _ = await RetrieveAsync(row, "zebra quartz", topK: 3);
        _ = await RetrieveAsync(row, "alpha beta", topK: 3);
        _ = await RetrieveAsync(row, "delta gamma", topK: 3);

        var failure = Record.Exception(() => row.AssertRerankerReordered("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("1 of 3", assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertRerankerReordered_StaysQuietWhenTheRerankerMovedTheRanking()
    {
        // The green path, and the test the mutation check leans on: make the row return its input
        // order — discard the cross-encoder's scores, or skip the sort — and this must go red
        // through the guard's own message.
        var units = ThreeUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        var row = new RerankedAblationRow(new ScriptedReranker(AscendingScores));
        _ = await RetrieveAsync(row, "zebra quartz", topK: 3);

        row.AssertRerankerReordered("fixture");
    }

    /// <summary>Scores by ascending input index — 0, 1, 2 — so sorting reverses the input order.</summary>
    private static IReadOnlyList<double> AscendingScores(
        string query, IReadOnlyList<SearchResult> results)
    {
        var scores = new double[results.Count];
        for (var i = 0; i < scores.Length; i++)
        {
            scores[i] = i;
        }

        return scores;
    }

    /// <summary>Scores every pair 0.5 — the all-equal degradation the guard exists to catch.</summary>
    private static IReadOnlyList<double> ConstantScores(
        string query, IReadOnlyList<SearchResult> results)
    {
        var scores = new double[results.Count];
        Array.Fill(scores, 0.5);
        return scores;
    }

    /// <summary>Three one-chunk documents; the dense ranking is set by the warmed query vector.</summary>
    private static TextChunk[] ThreeUnits() =>
    [
        Unit("d1", "alpha beta gamma"),
        Unit("d2", "delta epsilon"),
        Unit("d3", "zebra habitat notes"),
    ];

    private static TextChunk Unit(string documentId, string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId(documentId),
        ChunkIndex = 0,
    };

    /// <summary>Stores one basis vector per unit, so the dense ranking is set by the query vector.</summary>
    private async Task IndexAsync(IReadOnlyList<TextChunk> units)
    {
        float[][] vectors = [[1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], [0f, 0f, 1f, 0f]];
        var stored = new EmbeddedChunk[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            stored[i] = new EmbeddedChunk { Chunk = units[i], Embedding = vectors[i] };
        }

        await _store.StoreAsync(stored, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Puts the query's vector into the cache, so <see cref="RetrieveAsync"/> never needs the
    /// generator. Every query text maps to the same vector — the dense leg of these fixtures only
    /// needs a deterministic ranking, and the reranker never sees vectors at all.
    /// </summary>
    private Task WarmQueryAsync(string text) => _embeddings.GetOrAddAsync(
        [text],
        static (_, _) => Task.FromResult<IReadOnlyList<float[]>>([[0.9f, 0.5f, 0.3f, 0.05f]]),
        TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        RerankedAblationRow row, string queryText, int topK) => row.RetrieveAsync(
            new BeirQuery("q1", queryText),
            generator: null!, // never consulted: the query vector is pre-warmed into the cache
            _embeddings,
            _store,
            new SearchOptions { TopK = topK },
            TestContext.Current.CancellationToken);

    private static string[] ChunkIds(IReadOnlyList<ChunkHit> hits)
    {
        var ids = new string[hits.Count];
        for (var i = 0; i < hits.Count; i++)
        {
            ids[i] = hits[i].ChunkId;
        }

        return ids;
    }

    /// <summary>
    /// An <see cref="IReranker"/> whose scores come from the test — the row's sorting, counters
    /// and guard are the code under test here, and the real cross-encoder needs a model file that
    /// offline tests must not. Results come back in input order, as <c>OnnxReranker</c>'s do.
    /// </summary>
    private sealed class ScriptedReranker : IReranker
    {
        private readonly Func<string, IReadOnlyList<SearchResult>, IReadOnlyList<double>> _score;

        public ScriptedReranker(
            Func<string, IReadOnlyList<SearchResult>, IReadOnlyList<double>> score)
        {
            _score = score;
        }

        public Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query,
            IReadOnlyList<SearchResult> results,
            CancellationToken cancellationToken = default)
        {
            var scores = _score(query, results);
            var reranked = new RerankResult[results.Count];
            for (var i = 0; i < results.Count; i++)
            {
                reranked[i] = new RerankResult
                {
                    SearchResult = results[i],
                    RelevanceScore = scores[i],
                };
            }

            return Task.FromResult<IReadOnlyList<RerankResult>>(reranked);
        }
    }
}
