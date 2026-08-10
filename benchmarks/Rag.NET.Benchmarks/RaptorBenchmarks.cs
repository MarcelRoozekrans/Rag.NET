using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor;
using Rag.NET.Raptor.Math;
using Rag.NET.Retrieval;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU overhead of RAPTOR ingestion (tree building) and retrieval (score adjustment).
/// All LLM and embedding calls are stubbed to isolate the algorithmic cost of UMAP, GMM,
/// tree construction, and retrieval score adjustments.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HLQ013:Use foreach loop", Justification = "Index-based access required for array initialization")]
public class RaptorBenchmarks
{
    private const int EmbeddingDimensions = 384;

    [Params(10, 50, 200)]
    public int ChunkCount { get; set; }

    // ── Prebuilt data for each iteration ─────────────────────────────────
    private float[][] _embeddings = null!;
    private List<EmbeddedChunk> _embeddedChunks = null!;
    private IReadOnlyList<SearchResult> _searchResultsWithRaptor = null!;

    // ── Behaviors under test ─────────────────────────────────────────────
    private RaptorIngestionBehavior _raptorIngestionBehavior = null!;
    private RaptorRetrievalBehavior _blendBehavior = null!;
    private RaptorRetrievalBehavior _boostBehavior = null!;
    private RaptorRetrievalBehavior _filterBehavior = null!;

    [GlobalSetup]
    public void Setup()
    {
        _embeddings = BuildEmbeddings(ChunkCount, EmbeddingDimensions);
        _embeddedChunks = BuildEmbeddedChunks(_embeddings);
        _searchResultsWithRaptor = BuildSearchResults(ChunkCount);
        _raptorIngestionBehavior = BuildIngestionBehavior();
        _blendBehavior = new RaptorRetrievalBehavior(new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Blend });
        _boostBehavior = new RaptorRetrievalBehavior(new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 1.3 });
        _filterBehavior = new RaptorRetrievalBehavior(new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MinRaptorLevel = 0, MaxRaptorLevel = 1 });
    }

    private static float[][] BuildEmbeddings(int count, int dims)
    {
        var rng = new Random(42);
        var embeddings = new float[count][];
        foreach (ref var embedding in embeddings.AsSpan())
        {
            embedding = new float[dims];
            for (int d = 0; d < dims; d++)
                embedding[d] = (float)(rng.NextDouble() * 2 - 1);
        }

        return embeddings;
    }

    private static List<EmbeddedChunk> BuildEmbeddedChunks(float[][] embeddings)
    {
        var chunks = new List<EmbeddedChunk>(embeddings.Length);
        for (int i = 0; i < embeddings.Length; i++)
        {
            chunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = $"Chunk {i}: The quick brown fox jumps over the lazy dog.",
                    DocumentId = new DocumentId("bench-doc"),
                    ChunkIndex = i,
                },
                Embedding = embeddings[i],
            });
        }

        return chunks;
    }

    private static IReadOnlyList<SearchResult> BuildSearchResults(int count)
    {
        var results = new List<SearchResult>(count);
        for (int i = 0; i < count; i++)
        {
            int raptorLevel = i % 3; // 0 = leaf, 1 = level-1 summary, 2 = level-2 summary
            var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            if (raptorLevel > 0)
            {
                metadata["raptor_level"] = raptorLevel.ToString(CultureInfo.InvariantCulture);
                metadata["raptor_cluster_id"] = (i % 5).ToString(CultureInfo.InvariantCulture);
            }

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = $"Result {i}",
                    DocumentId = new DocumentId("bench-doc"),
                    ChunkIndex = i,
                    Metadata = metadata,
                },
                Score = 1.0 - (i * 0.005),
            });
        }

        return results.AsReadOnly();
    }

    private static RaptorIngestionBehavior BuildIngestionBehavior()
    {
        var options = new RaptorOptions
        {
            Enabled = true,
            MinChunksForRaptor = 3,
            MaxTreeDepth = 2,
        };

        return new RaptorIngestionBehavior(new FakeChatClient(), new FakeEmbeddingGenerator(EmbeddingDimensions), options);
    }

    // ════════════════════════════════════════════════════════════════════
    // A) UMAP + GMM math layer (CPU cost)
    // ════════════════════════════════════════════════════════════════════

    [Benchmark]
    public float[][] Umap_Fit()
    {
        int targetDims = System.Math.Min(10, _embeddings[0].Length);
        return Umap.Fit(_embeddings, targetDims);
    }

    [Benchmark]
    public int Gmm_SelectK()
    {
        // Reduce first (as the real pipeline does) to keep GMM cost realistic
        int targetDims = System.Math.Min(10, _embeddings[0].Length);
        var reduced = Umap.Fit(_embeddings, targetDims);
        return GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(ChunkCount, 10));
    }

    // ════════════════════════════════════════════════════════════════════
    // B) Ingestion overhead (tree building)
    // ════════════════════════════════════════════════════════════════════

    [Benchmark(Baseline = true)]
    public async Task<int> Ingestion_WithoutRaptor()
    {
        var ctx = CreateIngestionContext();
        ctx.EmbeddedChunks.AddRange(_embeddedChunks);

        // Simulate pipeline without RAPTOR: just pass through
        var result = await TerminalNext(ctx, default).ConfigureAwait(false);
        return result.ChunksStored;
    }

    [Benchmark]
    public async Task<int> Ingestion_WithRaptor()
    {
        var ctx = CreateIngestionContext();
        ctx.EmbeddedChunks.AddRange(_embeddedChunks);

        var result = await _raptorIngestionBehavior.HandleAsync(ctx, default, TerminalNext).ConfigureAwait(false);
        return result.ChunksStored;
    }

    // ════════════════════════════════════════════════════════════════════
    // C) Retrieval overhead (score adjustment)
    // ════════════════════════════════════════════════════════════════════

    [Benchmark]
    public async Task<int> Retrieval_Blend()
    {
        var ctx = CreateRetrievalContext();
        var results = await _blendBehavior.HandleAsync(ctx, default, TerminalRetrievalNext).ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> Retrieval_Boost()
    {
        var ctx = CreateRetrievalContext();
        var results = await _boostBehavior.HandleAsync(ctx, default, TerminalRetrievalNext).ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> Retrieval_Filter()
    {
        var ctx = CreateRetrievalContext();
        var results = await _filterBehavior.HandleAsync(ctx, default, TerminalRetrievalNext).ConfigureAwait(false);
        return results.Count;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IngestionContext CreateIngestionContext() => new()
    {
        Stream = Stream.Null,
        Metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("bench-doc"),
            FileName = "bench.txt",
            ContentType = "text/plain",
        },
        GetNextBm25DocId = () => 0,
    };

    private RetrievalContext CreateRetrievalContext() => new()
    {
        Query = "quick brown fox",
        Options = new Models.Options.RetrievalOptions { TopK = 10 },
    };

    private static ValueTask<IngestionResult> TerminalNext(IngestionContext ctx, CancellationToken _)
        => new(new IngestionResult
        {
            DocumentId = ctx.Metadata.DocumentId,
            ChunksStored = ctx.EmbeddedChunks.Count,
        });

    private ValueTask<IReadOnlyList<SearchResult>> TerminalRetrievalNext(RetrievalContext ctx, CancellationToken _)
        => new(_searchResultsWithRaptor);

    // ── Fake implementations ────────────────────────────────────────────

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of the cluster.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeEmbeddingGenerator(int dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _fakeEmbedding = new float[dimensions];

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
