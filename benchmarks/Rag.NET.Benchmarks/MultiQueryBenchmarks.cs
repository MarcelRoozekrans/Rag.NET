using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of multi-query fan-out + deduplication.
/// Both the query expander and vector store are mocked (zero I/O latency) to isolate
/// the LINQ merge/dedup path. Real-world cost is dominated by the LLM expansion call
/// (~50-200 ms) and N parallel vector store queries.
/// </summary>
[MemoryDiagnoser]
public class MultiQueryBenchmarks
{
    private RagPipeline _pipeline3Variants = null!;
    private RagPipeline _pipeline5Variants = null!;
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

        _pipeline3Variants = await BuildPipelineAsync(variantCount: 3);
        _pipeline5Variants = await BuildPipelineAsync(variantCount: 5);
    }

    [Benchmark]
    public async Task<int> MultiQuery_3Variants()
    {
        var results = await _pipeline3Variants.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseMultiQuery = true });
        return results.Count;
    }

    [Benchmark]
    public async Task<int> MultiQuery_5Variants()
    {
        var results = await _pipeline5Variants.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseMultiQuery = true });
        return results.Count;
    }

    [Benchmark(Baseline = true)]
    public async Task<int> SingleQuery_Baseline()
    {
        var results = await _pipeline3Variants.RetrieveAsync(
            "quick brown fox",
            new RetrievalOptions { TopK = 5, UseMultiQuery = false });
        return results.Count;
    }

    private async Task<RagPipeline> BuildPipelineAsync(int variantCount)
    {
        var vectorStore = new NoOpVectorStore();
        var embedder = new FakeEmbeddingGenerator(dimensions: 384);
        var bm25Index = new InMemoryBm25Index();

        IRetriever retriever = new VectorStoreRetriever(vectorStore, embedder, bm25Index);
        retriever = new MultiQueryRetriever(
            retriever,
            new FakeQueryExpander(variantCount),
            new MultiQueryOptions { VariantCount = variantCount });

        var ingestor = new DocumentIngestor(
            [new TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 50 },
            bm25Index);

        var pipeline = new RagPipeline(retriever, ingestor);

        using var stream = new MemoryStream(_documentData);
        await pipeline.IngestAsync(stream, Metadata);

        return pipeline;
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

    private sealed class FakeQueryExpander(int variantCount) : IQueryExpander
    {
        public Task<IReadOnlyList<string>> ExpandAsync(
            string query, int count, CancellationToken cancellationToken = default)
        {
            var variants = new string[variantCount];
            for (var i = 0; i < variantCount; i++)
                variants[i] = $"{query} variant {i + 1}";
            return Task.FromResult<IReadOnlyList<string>>(variants);
        }
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
