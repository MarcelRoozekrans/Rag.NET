using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
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
/// Benchmarks the CPU-only overhead of the HyDE decorator.
/// The hypothetical document generator is mocked (returns a fixed string) to isolate
/// the decorator's pass-through and option-rewriting cost. Real-world cost is dominated
/// by the LLM call (~50-500 ms) for generating the hypothetical document.
/// </summary>
[MemoryDiagnoser]
public class HydeBenchmarks
{
    private RagPipeline _pipeline = null!;
    private byte[] _documentData = null!;

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

        IRetriever retriever = new VectorStoreRetriever(vectorStore, embedder, bm25Index);
        retriever = new HydeRetriever(retriever, new FakeHydeGenerator());

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
    }

    [Benchmark(Baseline = true)]
    public async Task<int> NoHyde_Baseline()
    {
        var results = await _pipeline.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseHyde = false });
        return results.Count;
    }

    [Benchmark]
    public async Task<int> WithHyde()
    {
        var results = await _pipeline.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseHyde = true });
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

    private sealed class FakeHydeGenerator : IHypotheticalDocumentGenerator
    {
        public Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult("A hypothetical document that answers the query about " + query);
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
