using Rag.NET.Benchmarks.Quality;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;
using Xunit.Sdk;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The hybrid row's fusion and its contribution guard, exercised offline against a hand-built
/// four-document fixture — no ONNX model, no downloaded corpus.
/// <para>
/// Offline is possible because the row's contract routes every embedding through
/// <see cref="EmbeddingCache"/>: with the query vector pre-warmed into the cache, the generator is
/// never consulted, so these tests pass <see langword="null"/> for it and the fixture stays a unit
/// test. The RRF arithmetic is pinned to hand-computed values, the way <c>IrMetricsTests</c> pins
/// nDCG — a fusion that is checked only against itself would pass with any constant in it.
/// </para>
/// <para>
/// The guard tests matter more than the arithmetic ones. If <c>InMemoryBm25Index</c> returned
/// nothing — wrong initialisation, an empty index, a tokenizer that drops everything — RRF
/// degrades to exactly the dense ranking and the hybrid row displays the dense number under a
/// hybrid label. <see cref="HybridBm25AblationRow.AssertBm25Contributed"/> exists to turn that
/// silence into a failure, and these tests prove it fails when it should and stays quiet when it
/// should not.
/// </para>
/// </summary>
public sealed class HybridBm25AblationRowTests : IDisposable
{
    /// <summary>What RRF divides by: 1-based rank plus this. See <see cref="HybridBm25AblationRow"/>.</summary>
    private const double K = 60;

    private const string ModelIdentity = "fixture-model/hand-warmed-vectors";

    private readonly string _root;
    private readonly EmbeddingCache _embeddings;
    private readonly InMemoryVectorStore _store = new();

    public HybridBm25AblationRowTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "ragnet-hybrid-row-" + Guid.NewGuid().ToString("N"));
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
    public void Name_SaysTheRowIsIncomparableToPublishedBm25()
    {
        // The label IS the deliverable: design §0 publishes this row as an internal comparison
        // only, because Anserini (the source of BEIR's published BM25 figures) stems and applies
        // stopwords at k1=0.9, b=0.4 while InMemoryBm25Index lowercases and splits at k1=1.5,
        // b=0.75. A reader who sees "+bm25" without the flag will reach for a published figure.
        using var row = HybridBm25AblationRow.Over(FourUnits());

        Assert.Contains("incomparable", row.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrieveAsync_FusesDenseAndLexicalByReciprocalRank_PinnedByHand()
    {
        // Dense (cosine against e1..e4 for q = [0.9, 0.5, 0.3, 0.05], TopK 3): d1, d2, d3.
        // Lexical ("zebra quartz"): d4 (tf 2 for "quartz") above d3 (tf 1 for "zebra").
        // RRF at k=60, 1-based ranks, each leg weighted 0.5 -- the EnsembleOptions default the
        // library's own ensemble applies, which this row carries so its scores match the shipped
        // fusion outright rather than up to a factor (see HybridFusionParityTests):
        //   d3 = 0.5/63 + 0.5/62   (dense rank 3, lexical rank 2)
        //   d1 = 0.5/61            (dense rank 1)
        //   d4 = 0.5/61            (lexical rank 1)
        //   d2 = 0.5/62            (dense rank 2)
        // d1 and d4 tie exactly; the dense rank breaks the tie, so d1 precedes d4. The weight is a
        // uniform factor, so it halves every score here and reorders nothing -- which is why the
        // pinned nDCG figures did not move when it changed.
        var units = FourUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        using var row = HybridBm25AblationRow.Over(units);

        var hits = await RetrieveAsync(row, "zebra quartz", topK: 3);

        Assert.Equal(["d3#0", "d1#0", "d4#0", "d2#0"], ChunkIds(hits));
        Assert.Equal((0.5 / 63) + (0.5 / 62), hits[0].Score, 10);
        Assert.Equal(0.5 / 61, hits[1].Score, 10);
        Assert.Equal(0.5 / 61, hits[2].Score, 10);
        Assert.Equal(0.5 / 62, hits[3].Score, 10);
        Assert.Equal("d3", hits[0].DocumentId);
        Assert.Equal(1, row.QueryCount);
        Assert.Equal(1, row.Bm25ProductiveQueryCount);
        Assert.Equal(1, row.DivergedQueryCount);
    }

    [Fact]
    public async Task RetrieveAsync_WhenBm25ReturnsNothing_DegradesToExactlyTheDenseRanking()
    {
        // No fixture text contains "xyzzy" or "plugh", so the lexical list is empty and RRF over
        // one list preserves its order. This is the degradation the guard exists to catch: the
        // ranking below is the dense row's, bit for bit, under a hybrid label.
        var units = FourUnits();
        await IndexAsync(units);
        await WarmQueryAsync("xyzzy plugh");
        using var row = HybridBm25AblationRow.Over(units);

        var hits = await RetrieveAsync(row, "xyzzy plugh", topK: 3);

        Assert.Equal(["d1#0", "d2#0", "d3#0"], ChunkIds(hits));
        Assert.Equal(1, row.QueryCount);
        Assert.Equal(0, row.Bm25ProductiveQueryCount);
        Assert.Equal(0, row.DivergedQueryCount);
    }

    [Fact]
    public async Task AssertBm25Contributed_FailsNamingTheDegradation_WhenBm25NeverReturned()
    {
        var units = FourUnits();
        await IndexAsync(units);
        await WarmQueryAsync("xyzzy plugh");
        using var row = HybridBm25AblationRow.Over(units);
        _ = await RetrieveAsync(row, "xyzzy plugh", topK: 3);

        var failure = Record.Exception(() => row.AssertBm25Contributed("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("BM25 RETURNED NOTHING", assertion.Message, StringComparison.Ordinal);
        Assert.Contains("0 of 1", assertion.Message, StringComparison.Ordinal);
        Assert.Contains("dense", assertion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertBm25Contributed_FailsWhenFewerThanHalfTheQueriesHadLexicalHits()
    {
        // One productive query out of three is below the meaningful-fraction threshold of half.
        // A corpus-scale run where BM25 finds terms for only a sliver of the queries is a broken
        // index that happened to catch a few, not a contributing retriever.
        var units = FourUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        await WarmQueryAsync("xyzzy plugh");
        await WarmQueryAsync("waldo fred");
        using var row = HybridBm25AblationRow.Over(units);
        _ = await RetrieveAsync(row, "zebra quartz", topK: 3);
        _ = await RetrieveAsync(row, "xyzzy plugh", topK: 3);
        _ = await RetrieveAsync(row, "waldo fred", topK: 3);

        var failure = Record.Exception(() => row.AssertBm25Contributed("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("1 of 3", assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertBm25Contributed_FailsWhenFusionNeverChangedTheDenseRanking()
    {
        // BM25 returns something, but only what dense already ranked first, so the fused order
        // equals the dense order on every query. Productive alone is not contribution: the row's
        // number would still be the dense number.
        TextChunk[] units =
        [
            Unit("d1", "alpha"),
            Unit("d2", "beta"),
        ];
        await IndexAsync(units, [[0.9f, 0.4f, 0f, 0f], [0.4f, 0.9f, 0f, 0f]]);
        await WarmQueryAsync("alpha");
        using var row = HybridBm25AblationRow.Over(units);

        var hits = await RetrieveAsync(row, "alpha", topK: 2);

        Assert.Equal(["d1#0", "d2#0"], ChunkIds(hits));
        var failure = Record.Exception(() => row.AssertBm25Contributed("fixture"));
        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("FUSION CHANGED NOTHING", assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertBm25Contributed_StaysQuietWhenLexicalHitsMovedTheRanking()
    {
        // The green path, and the test the mutation check leans on: break the index construction
        // in HybridBm25AblationRow.Over and this must go red through the guard's own message.
        var units = FourUnits();
        await IndexAsync(units);
        await WarmQueryAsync("zebra quartz");
        using var row = HybridBm25AblationRow.Over(units);
        _ = await RetrieveAsync(row, "zebra quartz", topK: 3);

        row.AssertBm25Contributed("fixture");
    }

    /// <summary>Four one-chunk documents whose texts share no tokens with each other.</summary>
    private static TextChunk[] FourUnits() =>
    [
        Unit("d1", "alpha beta gamma"),
        Unit("d2", "delta epsilon"),
        Unit("d3", "zebra habitat notes"),
        Unit("d4", "quartz crystal quartz"),
    ];

    private static TextChunk Unit(string documentId, string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId(documentId),
        ChunkIndex = 0,
    };

    /// <summary>Stores one basis vector per unit, so the dense ranking is set by the query vector.</summary>
    private Task IndexAsync(IReadOnlyList<TextChunk> units) => IndexAsync(
        units,
        [[1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], [0f, 0f, 1f, 0f], [0f, 0f, 0f, 1f]]);

    private async Task IndexAsync(IReadOnlyList<TextChunk> units, float[][] vectors)
    {
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
    /// needs a deterministic ranking, and the lexical leg never sees vectors at all.
    /// </summary>
    private Task WarmQueryAsync(string text) => _embeddings.GetOrAddAsync(
        [text],
        static (_, _) => Task.FromResult<IReadOnlyList<float[]>>([[0.9f, 0.5f, 0.3f, 0.05f]]),
        TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        HybridBm25AblationRow row, string queryText, int topK) => row.RetrieveAsync(
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
}
