using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Rag.NET.GraphRag;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU cost of GraphRAG algorithms (Leiden, PageRank) and the overhead
/// of entity extraction and retrieval behaviors. All LLM and embedding calls are stubbed
/// to isolate algorithmic and pipeline cost.
/// </summary>
[MemoryDiagnoser]
public class GraphRagBenchmarks
{
    private const int EmbeddingDimensions = 384;

    // ── Graph algorithm parameters ──────────────────────────────────────
    [Params(50, 200, 1000)]
    public int NodeCount { get; set; }

    // ── Pre-built data ──────────────────────────────────────────────────
    private GraphSnapshot _graph = null!;

    // ── Extraction behavior ─────────────────────────────────────────────
    private GraphEntityExtractionBehavior _extractionBehavior = null!;
    private List<TextChunk> _chunks = null!;

    // ── Retrieval behaviors ─────────────────────────────────────────────
    private GraphLocalSearchBehavior _localSearchBehavior = null!;
    private GraphGlobalSearchBehavior _globalSearchBehavior = null!;
    private IReadOnlyList<SearchResult> _localSearchResults = null!;
    private IReadOnlyList<SearchResult> _globalSearchResults = null!;

    [GlobalSetup]
    public void Setup()
    {
        _graph = BuildRandomGraph(NodeCount);
        _chunks = BuildChunks(Math.Min(NodeCount, 20));
        _localSearchResults = BuildEntitySearchResults(NodeCount);
        _globalSearchResults = BuildCommunitySearchResults(Math.Max(NodeCount / 10, 3));
        _globalSearchBehavior = new GraphGlobalSearchBehavior(
            new FakeChatClient(),
            new GraphRagRetrievalOptions());
        // Behaviors that use the NSubstitute IGraphStore mock are rebuilt each
        // iteration to prevent call-record accumulation from inflating timings.
        RebuildMockDependentBehaviors();
    }

    [IterationSetup]
    public void IterationSetup() => RebuildMockDependentBehaviors();

    private void RebuildMockDependentBehaviors()
    {
        var graphStore = BuildFakeGraphStore(_graph);
        _extractionBehavior = new GraphEntityExtractionBehavior(
            new FakeChatClient(),
            new FakeEmbeddingGenerator(EmbeddingDimensions),
            graphStore,
            new GraphRagOptions { Enabled = true, GleaningPasses = 0 });
        _localSearchBehavior = new GraphLocalSearchBehavior(
            graphStore,
            new GraphRagRetrievalOptions { LocalTopEntities = 5, LocalSearchDepth = 1, PageRankWeight = 0.3 });
    }

    // ════════════════════════════════════════════════════════════════════
    // A) Leiden community detection — CPU cost
    // ════════════════════════════════════════════════════════════════════

    [Benchmark]
    public IReadOnlyList<Community> Leiden_Detect()
    {
        return Leiden.Detect(_graph, new LeidenOptions { MaxLevels = 5 });
    }

    // ════════════════════════════════════════════════════════════════════
    // B) PageRank — CPU cost
    // ════════════════════════════════════════════════════════════════════

    [Benchmark]
    public IReadOnlyDictionary<string, double> PageRank_Compute()
    {
        return PageRank.Compute(_graph);
    }

    // ════════════════════════════════════════════════════════════════════
    // C) Entity extraction overhead
    // ════════════════════════════════════════════════════════════════════

    [Benchmark(Baseline = true)]
    public async Task<int> Ingestion_WithoutGraphRag()
    {
        var ctx = CreateIngestionContext();
        ctx.Chunks.AddRange(_chunks);
        var result = await TerminalIngestionNext(ctx, default).ConfigureAwait(false);
        return result.ChunksStored;
    }

    [Benchmark]
    public async Task<int> Ingestion_WithGraphEntityExtraction()
    {
        var ctx = CreateIngestionContext();
        ctx.Chunks.AddRange(_chunks);
        var result = await _extractionBehavior.HandleAsync(ctx, default, TerminalIngestionNext).ConfigureAwait(false);
        return result.ChunksStored;
    }

    // ════════════════════════════════════════════════════════════════════
    // D) Retrieval modes
    // ════════════════════════════════════════════════════════════════════

    [Benchmark]
    public async Task<int> Retrieval_LocalSearch()
    {
        var ctx = CreateRetrievalContext();
        var results = await _localSearchBehavior.HandleAsync(ctx, default, (_, _) => new ValueTask<IReadOnlyList<SearchResult>>(_localSearchResults)).ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> Retrieval_GlobalSearch()
    {
        var ctx = CreateRetrievalContext();
        var results = await _globalSearchBehavior.HandleAsync(ctx, default, (_, _) => new ValueTask<IReadOnlyList<SearchResult>>(_globalSearchResults)).ConfigureAwait(false);
        return results.Count;
    }

    // ── Graph generation ────────────────────────────────────────────────

    private static GraphSnapshot BuildRandomGraph(int nodeCount)
    {
        var rng = new Random(42);
        var entities = new List<GraphEntity>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            entities.Add(new GraphEntity($"Entity_{i}", "Concept", $"Description of entity {i}")
            {
                PageRankScore = 1.0 / nodeCount,
            });
        }

        int edgeCount = nodeCount * 3;
        var relationships = new List<GraphRelationship>(edgeCount);
        var edgeSet = new HashSet<(int, int)>();
        for (int i = 0; i < edgeCount; i++)
        {
            int src, tgt;
            do
            {
                src = rng.Next(nodeCount);
                tgt = rng.Next(nodeCount);
            } while (src == tgt || !edgeSet.Add((Math.Min(src, tgt), Math.Max(src, tgt))));

            relationships.Add(new GraphRelationship(
                entities[src].Name,
                entities[tgt].Name,
                $"Relates {src} to {tgt}",
                rng.NextDouble() * 2.0 + 0.5));
        }

        return new GraphSnapshot(entities, relationships, []);
    }

    private static List<TextChunk> BuildChunks(int count)
    {
        var chunks = new List<TextChunk>(count);
        for (int i = 0; i < count; i++)
        {
            chunks.Add(new TextChunk
            {
                Text = $"Chunk {i}: Alice works at Contoso. Bob is a manager at Fabrikam. They collaborate on Project X.",
                DocumentId = new DocumentId("bench-doc"),
                ChunkIndex = i,
            });
        }

        return chunks;
    }

    private static IReadOnlyList<SearchResult> BuildEntitySearchResults(int count)
    {
        var results = new List<SearchResult>(count);
        for (int i = 0; i < count; i++)
        {
            var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["graph_type"] = "entity",
                ["graph_entity_name"] = $"Entity_{i}",
                ["graph_entity_type"] = "Concept",
            };

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = $"Description of entity {i}",
                    DocumentId = new DocumentId("bench-doc"),
                    ChunkIndex = i,
                    Metadata = metadata,
                },
                Score = 1.0 - (i * 0.002),
            });
        }

        return results.AsReadOnly();
    }

    private static IReadOnlyList<SearchResult> BuildCommunitySearchResults(int count)
    {
        var results = new List<SearchResult>(count);
        for (int i = 0; i < count; i++)
        {
            var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["graph_type"] = "community_report",
                ["graph_community_id"] = i.ToString(CultureInfo.InvariantCulture),
            };

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = $"Community {i} report: This community consists of entities involved in topic {i}.",
                    DocumentId = new DocumentId("bench-doc"),
                    ChunkIndex = -(i + 1),
                    Metadata = metadata,
                },
                Score = 0.9 - (i * 0.05),
            });
        }

        return results.AsReadOnly();
    }

    private static IGraphStore BuildFakeGraphStore(GraphSnapshot graph)
    {
        var store = Substitute.For<IGraphStore>();

        store.GetNeighborsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var name = callInfo.ArgAt<string>(0);
                var neighbors = graph.Entities
                    .Where(e => !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();
                return Task.FromResult<IReadOnlyList<GraphEntity>>(neighbors);
            });

        store.GetRelationshipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GraphRelationship>>([]));

        store.GetCommunitiesForEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Community>>([]));

        store.AddEntitiesAsync(Arg.Any<IReadOnlyList<GraphEntity>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        store.AddRelationshipsAsync(Arg.Any<IReadOnlyList<GraphRelationship>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return store;
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

    private static RetrievalContext CreateRetrievalContext() => new()
    {
        Query = "What entities are related to Project X?",
        Options = new RetrievalOptions { TopK = 10 },
    };

    private static ValueTask<IngestionResult> TerminalIngestionNext(IngestionContext ctx, CancellationToken _)
        => new(new IngestionResult
        {
            DocumentId = ctx.Metadata.DocumentId,
            ChunksStored = ctx.EmbeddedChunks.Count + ctx.Chunks.Count,
        });

    // ── Fake implementations ────────────────────────────────────────────

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"entities": [{"name": "Alice", "type": "Person", "description": "A person"}], "relationships": [{"source": "Alice", "target": "Contoso", "description": "works at", "weight": 1.0}]}""")));

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
