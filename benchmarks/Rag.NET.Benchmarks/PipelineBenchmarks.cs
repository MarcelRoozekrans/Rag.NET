using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;
    private byte[] _documentData = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId = new DocumentId("bench-doc"),
        FileName = "bench.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore, NoOpVectorStore>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        services.AddRagNet();

        _sp = services.BuildServiceProvider();
        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        _documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));

        // Pre-ingest so BM25 index is populated for retrieval benchmark
        using var stream = new MemoryStream(_documentData);
        await _pipeline.IngestAsync(stream, Metadata);
    }

    [GlobalCleanup]
    public void Cleanup() => _sp?.Dispose();

    [Benchmark]
    public async Task<int> RetrieveAsync_HybridBm25()
    {
        var results = await _pipeline.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseHybridSearch = true });
        return results.Count;
    }

    [Benchmark]
    public async Task<int> IngestAsync_50KB()
    {
        using var stream = new MemoryStream(_documentData);
        var result = await _pipeline.IngestAsync(stream, Metadata).ConfigureAwait(false);
        return result.ChunksStored;
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph = "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
            "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
        {
            sb.Append(paragraph);
        }

        return sb.ToString();
    }

    private sealed class NoOpVectorStore : IVectorStore
    {
        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, Models.Options.SearchOptions options,
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
            {
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
