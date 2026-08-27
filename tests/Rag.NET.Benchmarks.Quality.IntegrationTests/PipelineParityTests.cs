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
    private static readonly string[] Corpus =
    [
        "the first document, nearest the query",
        "the second document",
        "the third document",
        "the fourth document",
        "the fifth document",
        "the sixth document, furthest from the query",
    ];

    /// <summary>Strictly below <see cref="Corpus"/>'s length, so truncation is observable.</summary>
    private const int TopK = 4;

    [Fact]
    public async Task DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnASyntheticCorpus()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new OrderingEmbeddingGenerator(Corpus);

        using var store = new InMemoryVectorStore();
        await IndexAsync(store, embedder, ct);

        // The harness side, expressed as DenseRow expresses it: one query embedding, one cosine
        // search. AblationRow.Dense itself takes a concrete OnnxEmbeddingGenerator, so it cannot be
        // called with a fixture embedder — the real leg calls it directly.
        var queryVectors = await embedder.GenerateAsync(
            [OrderingEmbeddingGenerator.QueryText], cancellationToken: ct);
        var harnessResults = await store.SearchAsync(
            queryVectors[0].Vector, new SearchOptions { TopK = TopK }, ct);
        var harness = PipelineParity.ToChunkHits(harnessResults);

        var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
            store, embedder, OrderingEmbeddingGenerator.QueryText, TopK, ct);

        PipelineParity.AssertSame(harness, pipeline, OrderingEmbeddingGenerator.QueryText);

        // The fixture's ordering is known by construction, so this pins what BOTH sides should have
        // returned. Without it, two identically-wrong rankings would agree and pass.
        Assert.Equal(
            ["doc-0#0", "doc-1#0", "doc-2#0", "doc-3#0"],
            harness.Select(h => h.ChunkId).ToArray());
    }

    /// <summary>How many queries the real leg compares — fixed, so the run is seconds.</summary>
    private const int RealLegQueryCount = 20;

    /// <summary>
    /// The same claim on the corpus the pinned figures come from, against the harness's own dense
    /// row rather than a restatement of it.
    /// </summary>
    /// <remarks>
    /// Gated on provisioning only, deliberately — not on <c>RAGNET_BEIR_LONG_RUNS</c>. The
    /// embeddings are cached and twenty queries are seconds; the long-run gate exists for
    /// hour-scale sweeps, and putting the honest leg behind it would mean it effectively never
    /// runs.
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
        var unitTexts = units.Select(u => u.Text).ToArray();
        var unitVectors = await BeirHarness.EmbedAsync(generator, embeddings, unitTexts, ct);

        var chunks = new List<EmbeddedChunk>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            chunks.Add(new EmbeddedChunk { Chunk = units[i], Embedding = unitVectors[i] });
        }

        await store.StoreAsync(chunks, ct);

        // The pipeline reads the identical cached vector rather than calling the generator live: a
        // cache populated under a different model revision would otherwise disagree with a live
        // generator, and that difference is not the one this test is about.
        var pipelineEmbedder = new CachingEmbeddingGenerator(generator, embeddings);

        var queries = dataset.Queries
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .Take(RealLegQueryCount)
            .ToArray();

        Assert.Equal(RealLegQueryCount, queries.Length);

        var searchOptions = new SearchOptions { TopK = TopK };
        foreach (var query in queries)
        {
            var harness = await AblationRow.Dense.RetrieveAsync(
                query, generator, embeddings, store, searchOptions, ct);

            var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
                store, pipelineEmbedder, query.Text, TopK, ct);

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
}
