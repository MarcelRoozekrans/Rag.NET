using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Holds the hybrid cell's hand-composed reciprocal rank fusion to the library's own, as
/// <c>EnsembleBehavior</c> applies it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cell composes fusion by hand at all.</b> <see cref="HybridBm25AblationRow"/> ranks a
/// dense leg and a BM25 leg and merges them itself, because the harness measures rows against a
/// store rather than driving a pipeline. That is reasonable for a benchmark and it is exactly the
/// gap this phase keeps finding elsewhere: a figure produced by a re-implementation describes the
/// re-implementation until something ties the two together.
/// </para>
/// <para>
/// <b>What this proves.</b> Given the same dense ranking and the same BM25 index, the harness's
/// fusion and the shipped <c>EnsembleBehavior</c> return the same documents in the same order — so
/// the pinned +BM25 figures describe the library's fusion rather than the harness's copy of it.
/// </para>
/// <para>
/// <b>Scores as well as order, and that is the half worth reading.</b> The row weighted both legs
/// at 1.0 while <c>EnsembleOptions</c> defaults <c>DenseWeight</c> and <c>Bm25Weight</c> to 0.5
/// each, so every harness score was exactly twice the library's. That is a uniform factor on an RRF
/// sum — it moves no rank, nDCG cannot see it, and the pinned figures did not move when the row was
/// brought onto the library's default. It was still worth removing: it let this test assert score
/// equality outright, and an order-only assertion turned out to be too weak to be worth having.
/// Mutating the row's rank constant from 60 to 10 left an order-only version of this test green,
/// because RRF rankings barely move with k. The score assertion catches both that and an
/// off-by-one in the rank base.
/// </para>
/// </remarks>
public sealed class HybridFusionParityTests
{
    private const int TopK = 4;



    /// <remarks>
    /// Only <c>doc-2</c> carries either query term, so BM25 returns exactly one document and every
    /// other fused score comes from a single leg. That is deliberate: it keeps the fused scores far
    /// apart (one doc scores ~2/61, the rest ~1/6x) so this test cannot fail on a tie-break, which
    /// the two implementations are under no obligation to resolve the same way.
    /// </remarks>
    private static readonly string[] Corpus =
    [
        "alpha orchestration platform",
        "beta docker container runtime",
        "gamma kubernetes scheduling internals",
        "delta mesos cluster manager",
        "epsilon nomad workload queue",
        "zeta serverless function runtime",
    ];

    /// <summary>
    /// The dense leg's preference order, by corpus index — deliberately ranking <c>doc-2</c>, the
    /// one document BM25 will find, in fourth place. The legs must disagree or the comparison is
    /// dense retrieval against itself.
    /// </summary>
    private static readonly int[] DensePreference = [1, 3, 5, 2, 0, 4];

    private const string Query = "kubernetes scheduling";

    [Fact]
    public async Task TheCellsHandComposedFusion_RanksAsTheLibrarysEnsembleDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new KeywordOverlapEmbedder(Corpus, Query);

        using var store = new InMemoryVectorStore();
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

        var queryVector = (await embedder.GenerateAsync([Query], cancellationToken: ct))[0].Vector;
        var index = BuildIndex();

        var harness = (await HarnessFusionAsync(store, index, queryVector, ct)).Take(TopK).ToArray();

        var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
            store,
            embedder,
            Query,
            TopK,
            ct,
            configureServices: services => services.AddSingleton<IBm25Index>(index),
            tuneOptions: static o => o with { UseHybridSearch = true });

        Assert.Equal(
            harness.Select(static h => h.ChunkId).ToArray(),
            pipeline.Take(TopK).Select(static h => h.ChunkId).ToArray());

        AssertScoresMatchTheLibrarys(harness, pipeline);
        await AssertFusionMovedSomethingAsync(harness, store, queryVector, ct);
    }

    /// <summary>
    /// Holds the fused SCORES to the library's, exactly — both now weight each leg at the
    /// <c>EnsembleOptions</c> default of 0.5.
    /// </summary>
    /// <remarks>
    /// <b>This is the assertion that makes the test sensitive to the arithmetic</b> rather than only
    /// to the outcome. Order alone was not enough, and this is not a hypothetical: mutating the
    /// row's rank constant from 60 to 10 left an order-only version of this test green, because RRF
    /// rankings barely move with k. A test that cannot see the constant cannot claim the two
    /// implementations share it.
    /// </remarks>
    private static void AssertScoresMatchTheLibrarys(
        IReadOnlyList<ChunkHit> harness, IReadOnlyList<ChunkHit> pipeline)
    {
        var pipelineScores = pipeline.ToDictionary(
            static h => h.ChunkId, static h => h.Score, StringComparer.Ordinal);

        foreach (var hit in harness)
        {
            Assert.True(
                pipelineScores.TryGetValue(hit.ChunkId, out var libraryScore),
                $"{hit.ChunkId} was fused by the row but is absent from the library's ranking.");

            Assert.Equal(hit.Score, libraryScore, precision: 9);
        }
    }

    /// <summary>Asserts the lexical leg changed the ranking, so the comparison tested fusion.</summary>
    /// <remarks>
    /// If BM25 contributed nothing, both sides would be the dense ranking and the parity assertion
    /// would be comparing dense retrieval with itself — green, and testing nothing. The first
    /// version of this fixture did exactly that: its embedder scored by term overlap, which ranks
    /// much as BM25 does, so the two legs agreed and fusion was a no-op. This guard caught it.
    /// </remarks>
    private static async Task AssertFusionMovedSomethingAsync(
        IReadOnlyList<ChunkHit> harness,
        InMemoryVectorStore store,
        ReadOnlyMemory<float> queryVector,
        CancellationToken ct)
    {
        var dense = PipelineParity.ToChunkHits(
            await store.SearchAsync(queryVector, new SearchOptions { TopK = TopK }, ct));

        Assert.NotEqual(
            dense.Select(static h => h.ChunkId).ToArray(),
            harness.Select(static h => h.ChunkId).ToArray());
    }

    /// <summary>
    /// The harness side: the row's own fusion over the same two rankings the pipeline fuses.
    /// </summary>
    /// <remarks>
    /// It calls <c>FuseByReciprocalRank</c> rather than driving the row, because
    /// <c>HybridBm25AblationRow.RetrieveAsync</c> embeds the query through
    /// <c>BeirHarness.EmbedAsync</c> and would drag a 90 MB ONNX model into a test about
    /// arithmetic. The fusion is a pure function of two rankings; those rankings are what this
    /// supplies, and they are produced by the same store and the same index the pipeline uses.
    /// </remarks>
    private static async Task<IReadOnlyList<ChunkHit>> HarnessFusionAsync(
        InMemoryVectorStore store,
        InMemoryBm25Index index,
        ReadOnlyMemory<float> queryVector,
        CancellationToken ct)
    {
        var dense = PipelineParity.ToChunkHits(
            await store.SearchAsync(queryVector, new SearchOptions { TopK = TopK }, ct));
        var lexical = HybridBm25AblationRow.ToChunkHits(index.Search(Query, TopK));

        return HybridBm25AblationRow.FuseByReciprocalRank(dense, lexical);
    }

    private static InMemoryBm25Index BuildIndex()
    {
        var index = new InMemoryBm25Index();
        for (var i = 0; i < Corpus.Length; i++)
        {
            index.Add(i, new TextChunk
            {
                Text = Corpus[i],
                DocumentId = new DocumentId(FormattableString.Invariant($"doc-{i}")),
                ChunkIndex = 0,
            });
        }

        return index;
    }

    /// <summary>
    /// A deterministic embedder scoring by shared-word overlap with the query, so the dense ranking
    /// is known by construction and differs from the BM25 ranking enough for fusion to matter.
    /// </summary>
    private sealed class KeywordOverlapEmbedder(IReadOnlyList<string> corpus, string query)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Dictionary<string, float[]> _vectors = Build(corpus, query);

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fusion-parity-fixture");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                if (!_vectors.TryGetValue(value, out var vector))
                {
                    throw new ArgumentException(
                        $"'{value}' is not a text this fixture knows. A default vector here would " +
                        "make both sides agree about nothing.",
                        nameof(values));
                }

                generated.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType?.IsInstanceOfType(this) is true ? this : null;

        public void Dispose()
        {
        }

        private static Dictionary<string, float[]> Build(IReadOnlyList<string> corpus, string query)
        {
            // Two dimensions: the query is [1, 0], and each document is [x, sqrt(1 - x^2)], so its
            // cosine with the query is exactly x. Assigning x down DensePreference makes the dense
            // ranking that array, by construction, with no dependence on what an embedder happens
            // to think two strings mean.
            var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
            for (var position = 0; position < DensePreference.Length; position++)
            {
                var x = 0.9f - (0.1f * position);
                var y = (float)Math.Sqrt(Math.Max(0, 1 - (x * x)));
                map[corpus[DensePreference[position]]] = [x, y];
            }

            map[query] = [1f, 0f];
            return map;
        }
    }
}
