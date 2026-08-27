using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
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
