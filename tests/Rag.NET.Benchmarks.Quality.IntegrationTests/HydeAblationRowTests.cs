using Rag.NET.Benchmarks.Quality;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;
using Xunit.Sdk;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The HyDE row's vector building, its refuse-on-miss discipline and its divergence guard,
/// exercised offline against a hand-built three-document fixture — no ONNX model, no downloaded
/// corpus, and above all <b>no LLM</b>.
/// <para>
/// Offline is possible for the same reason it was for <see cref="RerankedAblationRowTests"/>: every
/// embedding goes through <see cref="EmbeddingCache"/>, so pre-warming the query's and each
/// hypothetical's vector means the generator is never consulted, and the hypotheticals themselves
/// are seeded through a fill-mode <see cref="HypotheticalCache"/> exactly the way the generation
/// tool seeds the real one.
/// </para>
/// <para>
/// <see cref="HydeAblationRow.BuildSearchVector"/> is pinned to hand-computed values rather than
/// only observed through retrieval, because the store computes true cosine: a search cannot tell a
/// normalised vector from an unnormalised one, so the L2 step the library performs in
/// <c>HydeBehavior</c> is only checkable here, on the vector itself.
/// </para>
/// <para>
/// The guard tests matter more than the arithmetic ones. If reading the hypotheticals silently
/// fell back to the query text, or the mean collapsed onto the query vector, this row would search
/// with the dense row's vector and display the dense number under a HyDE label.
/// <see cref="HydeAblationRow.AssertHydeDiverged"/> exists to turn that silence into a failure,
/// and these tests prove it fails when it should and stays quiet when it should not.
/// </para>
/// </summary>
public sealed class HydeAblationRowTests : IDisposable
{
    private const string ModelIdentity = "fixture-model/hand-warmed-vectors";
    private const string HypotheticalIdentity = "fixture-llm@t0.8";
    private const string PromptTemplate = "Answer: {query}";

    /// <summary>The dense leg's vector for every query text: ranks d1, then d2, then d3.</summary>
    private static readonly float[] QueryVector = [0.9f, 0.5f, 0.3f, 0.05f];

    private readonly string _root;
    private readonly EmbeddingCache _embeddings;
    private readonly InMemoryVectorStore _store = new();

    public HydeAblationRowTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "ragnet-hyde-row-" + Guid.NewGuid().ToString("N"));
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
    public void Name_NamesTheCachedModelAndSaysNoLlmIsCalled()
    {
        // The label IS the deliverable: it must say whose text the vectors came from — the cache's
        // model identity, verbatim, so it cannot drift from what is actually read — and that the
        // row never calls an LLM, because "HyDE" without that flag reads as a live generation.
        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);

        Assert.Contains(HypotheticalIdentity, row.Name, StringComparison.Ordinal);
        Assert.Contains("hyde", row.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no LLM", row.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RefusesAFillModeCache()
    {
        // A fill-mode cache would generate on a miss — the one thing this row must never do,
        // because regenerated text blends a second sampling run into a table labelled as one.
        var failure = Assert.Throws<ArgumentException>(
            () => new HydeAblationRow(FillCache(), hypothesisCount: 3));

        Assert.Contains(nameof(HypotheticalCacheMode.RefuseOnMiss), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSearchVector_IsTheL2NormalisedMeanOfAllHypotheses()
    {
        // Three orthogonal unit vectors: mean = [1/3, 1/3, 1/3, 0], L2-normalised to 1/sqrt(3) per
        // non-zero component — HydeBehavior.AverageAndNormalize's arithmetic exactly. This is the
        // test that catches an averaging mutation the searches cannot: using only hypothesis 0
        // gives [1, 0, 0, 0], and skipping the normalisation gives [1/3, 1/3, 1/3, 0].
        var vector = HydeAblationRow.BuildSearchVector(
            [[1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], [0f, 0f, 1f, 0f]]);

        var expected = (float)(1.0 / Math.Sqrt(3));
        Assert.Equal(4, vector.Length);
        Assert.Equal(expected, vector[0], 1e-6f);
        Assert.Equal(expected, vector[1], 1e-6f);
        Assert.Equal(expected, vector[2], 1e-6f);
        Assert.Equal(0f, vector[3], 1e-6f);
    }

    [Fact]
    public void BuildSearchVector_ThrowsOnADimensionMismatch()
    {
        // The library falls back to the single-doc text path here; this row has no fallback to
        // fall to — a mismatch means the embedding cache served vectors from two different models,
        // and averaging them would produce a number with no meaning at all.
        _ = Assert.Throws<InvalidOperationException>(
            () => HydeAblationRow.BuildSearchVector([[1f, 0f], [1f, 0f, 0f]]));
    }

    [Fact]
    public void BuildSearchVector_ThrowsOnAZeroNormMean()
    {
        _ = Assert.Throws<InvalidOperationException>(
            () => HydeAblationRow.BuildSearchVector([[1f, 0f], [-1f, 0f]]));
    }

    [Fact]
    public async Task RetrieveAsync_SearchesWithTheHypotheticalMean_NotTheQueryVector()
    {
        // Dense (cosine against e1..e3 for q = [0.9, 0.5, 0.3, 0.05], TopK 3): d1, d2, d3.
        // The seeded hypothesis vectors are e3, e2, e3, so the normalised mean is
        // [0, 1/sqrt(5), 2/sqrt(5), 0] and the HyDE ranking is the reversal: d3, d2, d1 — with the
        // HYDE search's cosine scores on the hits, which is what lets DocumentRanking's max-pool
        // preserve this order downstream instead of re-sorting it back into the dense one.
        await IndexAsync(ThreeUnits());
        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);

        var hits = await RunQueryAsync(row, "zebra quartz", DivergingVectors());

        Assert.Equal(["d3#0", "d2#0", "d1#0"], ChunkIds(hits));
        Assert.Equal(2.0 / Math.Sqrt(5), hits[0].Score, 1e-6);
        Assert.Equal(1.0 / Math.Sqrt(5), hits[1].Score, 1e-6);
        Assert.Equal(0.0, hits[2].Score, 1e-6);
        Assert.Equal(1, row.QueryCount);
        Assert.Equal(1, row.DivergedQueryCount);
    }

    [Fact]
    public async Task RetrieveAsync_WhenAHypotheticalIsMissing_FailsNamingTheKeyAndTheTool()
    {
        // The row's single most important property: a missing entry fails loudly, naming what is
        // missing and how to fill it. Any quiet alternative — empty text, the query text — would
        // search with (approximately) the dense vector and publish the dense number as HyDE.
        await IndexAsync(ThreeUnits());
        await WarmQueryAsync("zebra quartz");
        var fill = FillCache();
        for (var i = 0; i < 2; i++)
        {
            await SeedHypothesisAsync(fill, "zebra quartz", i, QueryVector);
        }

        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RetrieveAsync(row, "zebra quartz", topK: 3));

        Assert.Contains("hypothesis 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("generation tool", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertHydeDiverged_FailsWhenTheRowRetrievedNothing()
    {
        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);

        var failure = Record.Exception(() => row.AssertHydeDiverged("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("RETRIEVED NOTHING", assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertHydeDiverged_FailsNamingTheDegradation_WhenNoQueryDiverged()
    {
        // Every hypothesis vector IS the query vector, so the mean is the query vector and every
        // HyDE ranking is the dense ranking — exactly what a silent fallback to the query text
        // looks like from outside, and exactly what the guard must refuse to publish.
        await IndexAsync(ThreeUnits());
        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);
        _ = await RunQueryAsync(row, "zebra quartz", EchoVectors());

        var failure = Record.Exception(() => row.AssertHydeDiverged("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("HYDE CHANGED NOTHING", assertion.Message, StringComparison.Ordinal);
        Assert.Contains("0 of 1", assertion.Message, StringComparison.Ordinal);
        Assert.Contains("dense", assertion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertHydeDiverged_FailsWhenFewerThanATenthOfQueriesDiverged()
    {
        // 1 diverged of 11 is just under the tenth the guard requires. The threshold is
        // deliberately this mild — ArguAna's hypotheticals restate the query's argument, so its
        // HyDE vectors legitimately land near the query vectors — but a row where over nine in ten
        // rankings are bit-identical to dense is searching with the query vector, not near it.
        await IndexAsync(ThreeUnits());
        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);
        _ = await RunQueryAsync(row, "diverging query", DivergingVectors());
        for (var i = 0; i < 10; i++)
        {
            _ = await RunQueryAsync(
                row, FormattableString.Invariant($"echo query {i}"), EchoVectors());
        }

        var failure = Record.Exception(() => row.AssertHydeDiverged("fixture"));

        var assertion = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("1 of 11", assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertHydeDiverged_StaysQuietWhenHydeMovedRankings()
    {
        // The green path, and the test the mutation checks lean on: make the row read the query
        // text instead of the cache, or collapse the mean, and this must go red through the
        // guard's own message.
        await IndexAsync(ThreeUnits());
        var row = new HydeAblationRow(RefuseCache(), hypothesisCount: 3);
        _ = await RunQueryAsync(row, "zebra quartz", DivergingVectors());

        row.AssertHydeDiverged("fixture");
    }

    /// <summary>Hypothesis vectors e3, e2, e3 — their mean ranks d3, d2, d1, reversing dense.</summary>
    private static float[][] DivergingVectors() =>
        [[0f, 0f, 1f, 0f], [0f, 1f, 0f, 0f], [0f, 0f, 1f, 0f]];

    /// <summary>Every hypothesis vector is the query vector — the mean IS the dense direction.</summary>
    private static float[][] EchoVectors() => [QueryVector, QueryVector, QueryVector];

    private HypotheticalCache FillCache() =>
        new(_root, HypotheticalIdentity, PromptTemplate, HypotheticalCacheMode.Fill);

    private HypotheticalCache RefuseCache() =>
        new(_root, HypotheticalIdentity, PromptTemplate, HypotheticalCacheMode.RefuseOnMiss);

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

    /// <summary>Stores one basis vector per unit, so rankings are set by the search vector.</summary>
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
    /// Seeds one query end to end — its query vector, its three cached hypotheticals and their
    /// vectors — and retrieves for it, the whole offline path in one call.
    /// </summary>
    private async Task<IReadOnlyList<ChunkHit>> RunQueryAsync(
        HydeAblationRow row, string queryText, float[][] hypothesisVectors)
    {
        await WarmQueryAsync(queryText);
        var fill = FillCache();
        for (var i = 0; i < hypothesisVectors.Length; i++)
        {
            await SeedHypothesisAsync(fill, queryText, i, hypothesisVectors[i]);
        }

        return await RetrieveAsync(row, queryText, topK: 3);
    }

    /// <summary>
    /// Writes one hypothetical the way the generation tool does — through a fill-mode cache — and
    /// warms its vector, so the refuse-on-miss cache and the embedding cache both hit.
    /// </summary>
    private async Task SeedHypothesisAsync(
        HypotheticalCache fill, string queryText, int hypothesisIndex, float[] vector)
    {
        var text = FormattableString.Invariant($"{queryText} :: hypothesis {hypothesisIndex}");
        _ = await fill.GetOrAddAsync(
            queryText,
            hypothesisIndex,
            _ => Task.FromResult(text),
            TestContext.Current.CancellationToken);
        await WarmAsync(text, vector);
    }

    /// <summary>Puts the query's vector into the cache, so the generator is never consulted.</summary>
    private Task WarmQueryAsync(string text) => WarmAsync(text, QueryVector);

    private Task WarmAsync(string text, float[] vector) => _embeddings.GetOrAddAsync(
        [text],
        (_, _) => Task.FromResult<IReadOnlyList<float[]>>([vector]),
        TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        HydeAblationRow row, string queryText, int topK) => row.RetrieveAsync(
            new BeirQuery("q1", queryText),
            generator: null!, // never consulted: every vector is pre-warmed into the cache
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
