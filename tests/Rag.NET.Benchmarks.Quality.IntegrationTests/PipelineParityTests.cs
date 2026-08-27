using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Holds a real <c>AddRagNet</c> pipeline to the harness's dense row. See
/// <see cref="PipelineParity"/> for why this is not the same sense of "parity" the BEIR legs use.
/// </summary>
public sealed class PipelineParityTests
{
    /// <summary>
    /// The synthetic corpus. <see langword="internal"/> so
    /// <see cref="OrderingEmbeddingGeneratorTests"/> guards <i>this</i> corpus rather than a
    /// look-alike of its own that would only coincidentally match it.
    /// </summary>
    internal static readonly string[] Corpus =
    [
        "the first document, nearest the query",
        "the second document",
        "the third document",
        "the fourth document",
        "the fifth document",
        "the sixth document, furthest from the query",
    ];

    /// <summary>
    /// The synthetic leg's search depth. Strictly below <see cref="Corpus"/>'s length, so
    /// truncation is observable. The real leg uses <see cref="RealLegDepth"/> instead — this
    /// constant describes only the synthetic corpus above.
    /// </summary>
    private const int TopK = 4;

    /// <summary>
    /// Every query the fixture offers, not just the one at angle 0. A default behaviour that
    /// stopped no-opping need not do so uniformly — MMR, for one, is a provable no-op on
    /// <see cref="OrderingEmbeddingGenerator.QueryText"/> because that query sits exactly on
    /// document 0 — so a single-query leg can be green while the pipeline has already drifted.
    /// </summary>
    [Fact]
    public async Task DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnASyntheticCorpus()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new OrderingEmbeddingGenerator(Corpus);

        using var store = new InMemoryVectorStore();
        await IndexAsync(store, embedder, ct);

        Assert.True(
            embedder.QueryTexts.Count > 1,
            "the fast leg is back to a single query; a divergence affecting only some queries " +
            "would be invisible.");

        foreach (var query in embedder.QueryTexts)
        {
            var harness = await HarnessDenseRowAsync(store, embedder, query, ct);

            // The harness side actually retrieved. The corpus is longer than TopK and MinScore is 0,
            // so a full page always comes back; a short list means retrieval silently produced
            // nothing and every AssertSame below it would agree on the empty prefix and pass.
            Assert.Equal(TopK, harness.Count);

            var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
                store, embedder, query, TopK, ct);

            PipelineParity.AssertSame(harness, pipeline, query);

            if (string.Equals(query, OrderingEmbeddingGenerator.QueryText, StringComparison.Ordinal))
            {
                AssertPinnedRanking(harness);
            }
        }
    }

    /// <summary>
    /// The fixture's ordering is known by construction for the query at angle 0, so this pins what
    /// BOTH sides should have returned. Without it, two identically-wrong rankings would agree and
    /// pass.
    /// </summary>
    private static void AssertPinnedRanking(IReadOnlyList<ChunkHit> harness)
    {
        var ids = new string[harness.Count];
        for (var i = 0; i < harness.Count; i++)
        {
            ids[i] = harness[i].ChunkId;
        }

        Assert.Equal(["doc-0#0", "doc-1#0", "doc-2#0", "doc-3#0"], ids);
    }

    /// <summary>
    /// The harness side, expressed as <c>DenseRow</c> expresses it: one query embedding, one cosine
    /// search. <c>AblationRow.Dense</c> itself takes a concrete <c>OnnxEmbeddingGenerator</c>, so it
    /// cannot be called with a fixture embedder — the real leg calls it directly.
    /// </summary>
    private static async Task<IReadOnlyList<ChunkHit>> HarnessDenseRowAsync(
        IVectorStore store,
        OrderingEmbeddingGenerator embedder,
        string query,
        CancellationToken ct)
    {
        var queryVectors = await embedder.GenerateAsync([query], cancellationToken: ct);
        var results = await store.SearchAsync(
            queryVectors[0].Vector, new SearchOptions { TopK = TopK }, ct);

        return PipelineParity.ToChunkHits(results);
    }

    /// <summary>How many queries the real leg compares — fixed, so the run is seconds.</summary>
    private const int RealLegQueryCount = 20;

    /// <summary>
    /// The real leg's search depth: the rank cutoff every pinned figure in this project is quoted
    /// at, not the synthetic fixture's <see cref="TopK"/>.
    /// </summary>
    private const int RealLegDepth = BeirHarness.Cutoff;

    /// <summary>
    /// The same claim on the corpus the pinned figures come from, against the harness's own dense
    /// row rather than a restatement of it.
    /// </summary>
    /// <remarks>
    /// Gated on provisioning only, deliberately — not on <c>RAGNET_BEIR_LONG_RUNS</c>. The corpus
    /// embeddings are the expensive part and the other BEIR legs leave them warm in the cache. The
    /// query embeddings need not be: the first twenty query ids by ordinal sort are mostly
    /// unjudged, so no other leg has ever embedded them, and this leg may pay for up to twenty live
    /// ONNX embeds — still seconds. The long-run gate exists for hour-scale sweeps, and putting the
    /// honest leg behind it would mean it effectively never runs.
    /// </remarks>
    [Fact]
    public async Task DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnSciFact()
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var descriptor = BeirDatasetDescriptor.SciFact;

        // The separator is passed explicitly for the same reason BeirParityTests passes it: it
        // decides what is embedded, and the cached vectors were produced with a single space.
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        var units = BeirHarness.OneChunkPerDocument(dataset.Documents);

        // One store, indexed once, handed to both sides. This is what makes the sixteen behaviours
        // the only surviving variable.
        using var store = new InMemoryVectorStore();
        await IndexUnitsAsync(store, units, generator, embeddings, ct);

        // The pipeline reads the identical cached vector rather than calling the generator live: a
        // cache populated under a different model revision would otherwise disagree with a live
        // generator, and that difference is not the one this test is about.
        var pipelineEmbedder = new CachingEmbeddingGenerator(generator, embeddings);

        var queries = dataset.Queries
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .Take(RealLegQueryCount)
            .ToArray();

        Assert.Equal(RealLegQueryCount, queries.Length);

        var searchOptions = new SearchOptions { TopK = RealLegDepth };
        foreach (var query in queries)
        {
            var harness = await AblationRow.Dense.RetrieveAsync(
                query, generator, embeddings, store, searchOptions, ct);

            // The vacuous-pass guard, and the only assertion here that touches the store: with zero
            // hits on both sides AssertSame agrees on all twenty queries and the leg passes without
            // ever having retrieved anything. Demanding a full page is safe — SciFact has 5,183
            // documents and MinScore is 0 on both sides — so a short list means indexing or
            // retrieval silently produced nothing, not a legitimate empty result.
            Assert.Equal(RealLegDepth, harness.Count);

            var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
                store, pipelineEmbedder, query.Text, RealLegDepth, ct);

            PipelineParity.AssertSame(harness, pipeline, query.Text);
        }
    }

    private static async Task IndexAsync(
        IVectorStore store,
        OrderingEmbeddingGenerator embedder,
        CancellationToken ct)
    {
        var vectors = await embedder.GenerateAsync(Corpus, cancellationToken: ct);
        var chunks = new List<EmbeddedChunk>(Corpus.Length);
        for (var i = 0; i < Corpus.Length; i++)
        {
            chunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = Corpus[i],
                    DocumentId = new DocumentId(FormattableString.Invariant($"doc-{i}")),
                    ChunkIndex = 0,
                },
                Embedding = vectors[i].Vector,
            });
        }

        await store.StoreAsync(chunks, ct);
    }

    /// <summary>Embeds <paramref name="units"/> through the cache and stores them, verbatim.</summary>
    private static async Task IndexUnitsAsync(
        IVectorStore store,
        IReadOnlyList<TextChunk> units,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        CancellationToken ct)
    {
        var unitTexts = units.Select(u => u.Text).ToArray();
        var unitVectors = await BeirHarness.EmbedAsync(generator, embeddings, unitTexts, ct);

        var chunks = new List<EmbeddedChunk>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            chunks.Add(new EmbeddedChunk { Chunk = units[i], Embedding = unitVectors[i] });
        }

        await store.StoreAsync(chunks, ct);
    }
}
