using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the overhead of the caching decorators.
/// Both the vector store and embedder are mocked (zero I/O) to isolate
/// cache hit vs miss overhead. Real-world benefit is skipping embedding
/// API calls (~10-50 ms) and vector store queries (~10-100 ms).
/// </summary>
[MemoryDiagnoser]
public class CachingBenchmarks
{
    private RagPipeline _pipeline = null!;
    private byte[] _documentData = null!;
    private IHost _host = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId  = "bench-doc",
        FileName    = "bench.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));

        var vectorStore = new NoOpVectorStore();
        var embedder = new FakeEmbeddingGenerator(dimensions: 384);
        var bm25Index = new InMemoryBm25Index();

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Services.AddHybridCache();
        _host = builder.Build();
        var cache = _host.Services.GetRequiredService<HybridCache>();
        var cachingOptions = new CachingOptions();

        IRetriever retriever = new VectorStoreRetriever(vectorStore, embedder, bm25Index);
        retriever = new EmbeddingCacheRetriever(retriever, cache, cachingOptions);
        retriever = new LostInTheMiddleRetriever(retriever);
        retriever = new ResultCacheRetriever(retriever, cache, cachingOptions);

        var ingestor = new DocumentIngestor(
            [new TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 50 },
            bm25Index);

        _pipeline = new RagPipeline(retriever, ingestor);

        using var stream = new MemoryStream(_documentData);
        await _pipeline.IngestAsync(stream, Metadata);

        // Warm the cache
        await _pipeline.RetrieveAsync("quick brown fox", new RetrievalOptions { TopK = 5 });
    }

    [GlobalCleanup]
    public void Cleanup() => _host?.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<int> CacheMiss_NoCaching()
    {
        var results = await _pipeline.RetrieveAsync(
            $"quick brown fox {Random.Shared.Next()}", // unique query = cache miss
            new RetrievalOptions { TopK = 5, UseCacheResult = false, UseCacheEmbedding = false });
        return results.Count;
    }

    [Benchmark]
    public async Task<int> CacheHit_EmbeddingOnly()
    {
        // Result cache miss (unique random suffix), but embedding cache hit (same base query warmed in setup)
        // This isolates the embedding cache benefit: skips IEmbeddingGenerator but still queries the vector store.
        var results = await _pipeline.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseCacheResult = false });
        return results.Count;
    }

    [Benchmark]
    public async Task<int> CacheHit_ResultCache()
    {
        var results = await _pipeline.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5 });
        return results.Count;
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph =
            "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
            "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
            sb.Append(paragraph);

        return sb.ToString();
    }

    private sealed class NoOpVectorStore : IVectorStore
    {
        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>([]);

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
