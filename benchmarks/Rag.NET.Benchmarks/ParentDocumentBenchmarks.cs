using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Storage;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of the parent-document retrieval behavior.
/// The vector store is mocked (returns pre-built child results) and the parent store is
/// in-memory, so there is zero I/O — these numbers measure only dictionary lookup and
/// deduplication overhead.
/// </summary>
[MemoryDiagnoser]
public class ParentDocumentBenchmarks
{
    private IRagPipeline _pipeline = null!;
    private IRagPipeline _pipelineBaseline = null!;
    private ServiceProvider _spWithParent = null!;
    private ServiceProvider _spBaseline = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        // Build the child results list that the fake vector store will return every call.
        var childResults = Enumerable.Range(0, 5).Select(i => new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = $"child chunk {i}",
                DocumentId = new DocumentId("doc1"),
                ChunkIndex = i,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["_parentKey"] = $"doc1:{i}"
                }
            },
            Score = 0.9 - i * 0.1
        }).ToList();

        // Pipeline with parent-document retrieval
        {
            var services = new ServiceCollection();
            services.AddSingleton<IVectorStore>(new FakeVectorStore(childResults));
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new FakeEmbeddingGenerator(dimensions: 384));
            services.AddRagNet(configure: b => b.UseParentDocumentRetrieval());

            _spWithParent = services.BuildServiceProvider();

            // Pre-populate the parent store before the pipeline resolves it.
            var parentStore = _spWithParent.GetRequiredService<InMemoryParentChunkStore>();
            await parentStore.InitializeAsync();
            for (var i = 0; i < 5; i++)
                parentStore.Add("doc1", i, $"Parent chunk {i}: this is the larger parent context text that replaces the small child chunk.");

            _pipeline = _spWithParent.GetRequiredService<IRagPipeline>();
        }

        // Baseline pipeline without parent-document retrieval
        {
            var services = new ServiceCollection();
            services.AddSingleton<IVectorStore>(new FakeVectorStore(childResults));
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new FakeEmbeddingGenerator(dimensions: 384));
            services.AddRagNet();

            _spBaseline = services.BuildServiceProvider();
            _pipelineBaseline = _spBaseline.GetRequiredService<IRagPipeline>();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _spWithParent?.Dispose();
        _spBaseline?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> NoParentDocument_Baseline()
    {
        var result = await _pipelineBaseline.RetrieveAsync("test query", new RetrievalOptions { UseParentDocument = false });
        return result.IsSuccess ? result.Value.Count : 0;
    }

    [Benchmark]
    public async Task<int> WithParentDocument()
    {
        var result = await _pipeline.RetrieveAsync("test query", new RetrievalOptions { UseParentDocument = true });
        return result.IsSuccess ? result.Value.Count : 0;
    }

    /// <summary>
    /// Minimal <see cref="IVectorStore"/> that always returns the same pre-built search results.
    /// Simulates a vector store with zero I/O latency.
    /// </summary>
    private sealed class FakeVectorStore(IReadOnlyList<SearchResult> results) : IVectorStore
    {
        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(results);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
