using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Holds the shipped HyDE path to the harness's <see cref="HydeAblationRow"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Phase 6.2.1 measured HyDE on three corpora — SciFact +0.03647, FiQA
/// −0.00886, ArguAna −0.02053 — and none of those runs executed a line of
/// <c>Rag.NET.QueryTechniques</c>. <see cref="HydeAblationRow"/> imports <c>HydeOptions</c> and
/// nothing else from the package: its hypotheticals come from <c>HypotheticalCache</c>, written by
/// a separate generation tool, and the shipped <c>LlmHypotheticalDocumentGenerator</c> is exercised
/// only by unit tests. <b>Three corpora of figures characterise the technique and touch none of the
/// shipped code.</b>
/// </para>
/// <para>
/// <b>What made that a live hazard rather than a pedantic one.</b> Both sides mean-pool the
/// hypothesis vectors and L2-normalise, and the two implementations —
/// <see cref="HydeAblationRow.BuildSearchVector"/> and <c>HydeBehavior.AverageAndNormalize</c> —
/// are arithmetically identical line for line: same summation, same divide-by-count, same
/// double-accumulated norm, same <c>Math.Sqrt</c>. They agree today by inspection, in two
/// assemblies, with nothing tying them together. That is the exact shape of the gap the
/// <see cref="PipelineParityTests"/> pair was built to close for dense retrieval, and it closes the
/// same way: not by measuring again, but by making a divergence fail a test.
/// </para>
/// <para>
/// <b>What this proves, and what it does not.</b> It proves that given the same hypotheses over the
/// same store, the shipped pipeline and the harness row return the same ranking — so the three
/// measured figures describe the shipped behaviour and not merely a re-implementation of it. It
/// does <i>not</i> measure HyDE, check the generator's prompt, or say anything about the quality of
/// generated hypotheses; the fake generator below supplies fixed text precisely so that generation
/// is not part of what is under test.
/// </para>
/// <para>
/// <b>Fast tier, no provisioning.</b> No ONNX model, no BEIR corpus, no network — a synthetic
/// corpus and a fixture embedder, so this runs on every push rather than only where the cache is
/// warm. A parity guard that ran only on a provisioned machine would be absent from exactly the
/// job that catches drift.
/// </para>
/// </remarks>
public sealed class HydePipelineParityTests
{
    /// <summary>How many hypotheses the fake generator returns, and what <c>HydeOptions</c> asks for.</summary>
    /// <remarks>
    /// Three, matching the shipped default and the count every measured cell ran with, so the
    /// pooling path under test is the one the figures came from. More than one is the load-bearing
    /// part: a single hypothesis makes mean-pooling a no-op and the two implementations would agree
    /// trivially.
    /// </remarks>
    private const int HypothesisCount = 3;

    /// <summary>The search depth, strictly below the corpus size so truncation is observable.</summary>
    private const int TopK = 4;

    [Fact]
    public async Task ShippedHydePipeline_ReturnsWhatTheHarnessHydeRowReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new HydeParityEmbedder();

        using var store = new InMemoryVectorStore();
        await IndexAsync(store, embedder, ct);

        var harness = await HarnessHydeRowAsync(store, embedder, ct);

        // The harness side actually retrieved. The corpus is longer than TopK and the default
        // MinScore is 0, so a full page always comes back; a short list would mean retrieval
        // produced nothing and AssertSame would agree on the empty prefix and pass.
        Assert.Equal(TopK, harness.Count);

        var pipeline = await ShippedHydePipelineAsync(store, embedder, ct);

        PipelineParity.AssertSame(harness, pipeline, HydeParityEmbedder.QueryText);
        AssertHypothesesActuallyMovedTheRanking(harness, store, embedder, ct);
    }

    /// <summary>
    /// Pins what both sides should have returned, so two identically-wrong rankings cannot agree
    /// and pass.
    /// </summary>
    /// <remarks>
    /// The fixture places the three hypotheses at 3.8, 4.3 and 5.1 angle steps and the query at 0.
    /// Their mean resolves to 4.399, so the ranking leads with document 4 where the dense ranking
    /// leads with document 0 — which is both the pinned expectation here and the reason the
    /// divergence check below cannot pass by accident. Derived from the geometry, not read off a run.
    /// </remarks>
    private static void AssertPinnedRanking(IReadOnlyList<ChunkHit> hits)
    {
        var ids = new string[hits.Count];
        for (var i = 0; i < hits.Count; i++)
        {
            ids[i] = hits[i].ChunkId;
        }

        Assert.Equal(["doc-4#0", "doc-5#0", "doc-3#0", "doc-2#0"], ids);
    }

    /// <summary>
    /// Asserts the hypothesis vector is not the query vector, before either ranking is read as
    /// evidence about HyDE.
    /// </summary>
    /// <remarks>
    /// Without this the test would pass if HyDE silently fell back to embedding the plain query on
    /// both sides — two dense searches agreeing perfectly, under a HyDE name. That fallback is real
    /// behaviour, not a hypothetical: <c>HydeBehavior</c> falls back to the query on generator
    /// failure by design, and the shipped path is wired here through a fake generator that could
    /// one day be misregistered. The same reasoning as
    /// <see cref="HydeAblationRow.AssertHydeDiverged"/>, in the fast tier.
    /// </remarks>
    private static void AssertHypothesesActuallyMovedTheRanking(
        IReadOnlyList<ChunkHit> hyde,
        InMemoryVectorStore store,
        HydeParityEmbedder embedder,
        CancellationToken ct)
    {
        AssertPinnedRanking(hyde);

        var queryVector = embedder.GenerateAsync([HydeParityEmbedder.QueryText], cancellationToken: ct)
            .GetAwaiter().GetResult()[0].Vector;
        var dense = PipelineParity.ToChunkHits(
            store.SearchAsync(queryVector, new SearchOptions { TopK = TopK }, ct)
                .GetAwaiter().GetResult());

        var denseFirst = dense.Count > 0 ? dense[0].ChunkId : string.Empty;
        Assert.False(
            string.Equals(denseFirst, hyde[0].ChunkId, StringComparison.Ordinal),
            "the HyDE ranking leads with the same chunk as the plain dense ranking, so the " +
            "hypothesis vector may be the query vector — which is what a silent fallback to the " +
            "query looks like, and it would make this parity test agree about nothing.");
    }

    /// <summary>
    /// The harness side, expressed as <see cref="HydeAblationRow"/> expresses it: embed each
    /// hypothesis, mean-pool and L2-normalise through the row's own
    /// <see cref="HydeAblationRow.BuildSearchVector"/>, one cosine search.
    /// </summary>
    /// <remarks>
    /// The row itself cannot be called here — <c>RetrieveAsync</c> takes a concrete
    /// <c>OnnxEmbeddingGenerator</c> and a <c>HypotheticalCache</c>, neither of which a fixture can
    /// supply — so this calls the pooling the row calls rather than re-deriving it. That is the
    /// distinction that makes the test meaningful: if <c>BuildSearchVector</c> ever diverges from
    /// the shipped implementation, this side moves with the row and the assertion fires.
    /// </remarks>
    private static async Task<IReadOnlyList<ChunkHit>> HarnessHydeRowAsync(
        InMemoryVectorStore store,
        HydeParityEmbedder embedder,
        CancellationToken ct)
    {
        var embeddings = await embedder.GenerateAsync(HydeParityEmbedder.Hypotheses, cancellationToken: ct);
        var vectors = new List<float[]>(embeddings.Count);
        for (var i = 0; i < embeddings.Count; i++)
        {
            vectors.Add(embeddings[i].Vector.ToArray());
        }

        var results = await store.SearchAsync(
            HydeAblationRow.BuildSearchVector(vectors), new SearchOptions { TopK = TopK }, ct);

        return PipelineParity.ToChunkHits(results);
    }

    /// <summary>The shipped side: a real <c>AddRagNet</c> pipeline with <c>UseHyde</c>.</summary>
    /// <remarks>
    /// The fake generator is registered after <c>AddRagNet</c> so it replaces the
    /// <c>LlmHypotheticalDocumentGenerator</c> that <c>UseHyde</c> registers — the last registration
    /// wins, and this is what keeps generation out of the comparison while leaving every other line
    /// of the shipped path in it.
    /// </remarks>
    private static Task<IReadOnlyList<ChunkHit>> ShippedHydePipelineAsync(
        IVectorStore store,
        HydeParityEmbedder embedder,
        CancellationToken ct) =>
        PipelineParity.RetrieveThroughPipelineAsync(
            store,
            embedder,
            HydeParityEmbedder.QueryText,
            TopK,
            ct,
            rag => rag.UseHyde(options => options.HypothesisCount = HypothesisCount),
            services => services.AddSingleton<IHypotheticalDocumentGenerator>(
                new FixedHypotheticalDocumentGenerator(HydeParityEmbedder.Hypotheses)));

    private static async Task IndexAsync(
        IVectorStore store,
        HydeParityEmbedder embedder,
        CancellationToken ct)
    {
        var vectors = await embedder.GenerateAsync(HydeParityEmbedder.Corpus, cancellationToken: ct);
        var chunks = new List<EmbeddedChunk>(HydeParityEmbedder.Corpus.Length);
        for (var i = 0; i < HydeParityEmbedder.Corpus.Length; i++)
        {
            chunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = HydeParityEmbedder.Corpus[i],
                    DocumentId = new DocumentId(FormattableString.Invariant($"doc-{i}")),
                    ChunkIndex = 0,
                },
                Embedding = vectors[i].Vector,
            });
        }

        await store.StoreAsync(chunks, ct);
    }
}
