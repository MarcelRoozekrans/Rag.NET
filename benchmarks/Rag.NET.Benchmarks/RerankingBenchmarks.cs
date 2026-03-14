using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of the reranking pipeline step.
/// The reranker is mocked (returns pre-computed scores) to isolate the sort/trim LINQ path.
/// Embedder and vector store are also mocked (zero I/O latency).
/// </summary>
[MemoryDiagnoser]
public class RerankingBenchmarks
{
    private RagPipeline _pipeline = null!;
    private RagPipeline _pipelineNoReranker = null!;

    [Params(5, 20)]
    public int TopK { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));

        var reranker = new FakeReranker();
        var vectorStore = new NoOpVectorStore();
        var embedder = new FakeEmbeddingGenerator(dimensions: 384);

        _pipeline = new RagPipeline(
            [new TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            chatClient: null,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 50 },
            reranker: reranker);

        _pipelineNoReranker = new RagPipeline(
            [new TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            chatClient: null,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 50 });

        var metadata = new DocumentMetadata
        {
            DocumentId = "bench-doc",
            FileName = "bench.txt",
            ContentType = "text/plain",
        };

        using var stream1 = new MemoryStream(documentData);
        await _pipeline.IngestAsync(stream1, metadata).ConfigureAwait(false);

        using var stream2 = new MemoryStream(documentData);
        await _pipelineNoReranker.IngestAsync(stream2, metadata).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true)]
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync_NoReranking()
    {
        return await _pipelineNoReranker.RetrieveAsync("test query", new RetrievalOptions
        {
            TopK = TopK,
        }).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync_WithReranking()
    {
        return await _pipeline.RetrieveAsync("test query", new RetrievalOptions
        {
            TopK = TopK,
            CandidateCount = TopK * 3,
        }).ConfigureAwait(false);
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

    /// <summary>
    /// Returns pre-computed descending scores — simulates model output with zero latency.
    /// </summary>
    private sealed class FakeReranker : IReranker
    {
        public Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query,
            IReadOnlyList<SearchResult> results,
            CancellationToken cancellationToken = default)
        {
            var reranked = new RerankResult[results.Count];
            for (var i = 0; i < results.Count; i++)
            {
                reranked[i] = new RerankResult
                {
                    SearchResult = results[i],
                    RelevanceScore = 1.0 - (i * 0.01),
                };
            }

            return Task.FromResult<IReadOnlyList<RerankResult>>(reranked);
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
