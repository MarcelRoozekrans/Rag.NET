using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of adaptive retrieval query classification.
/// The LLM classifier is mocked (returns instantly) to isolate the heuristic and
/// option-rewriting cost. Real-world cost for ambiguous queries is dominated by the
/// LLM call (~50-200 ms) when the heuristic cannot classify the query.
/// </summary>
[MemoryDiagnoser]
public class AdaptiveRetrievalBenchmarks
{
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;
    private byte[] _documentData = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId = new DocumentId("bench-doc"),
        FileName    = "bench.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore, NoOpVectorStore>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        services.AddSingleton<IChatClient>(new FakeChatClient("complex"));
        services.AddRagNet();

        _sp = services.BuildServiceProvider();
        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        using var stream = new MemoryStream(_documentData);
        _ = await _pipeline.IngestAsync(stream, Metadata);
    }

    [GlobalCleanup]
    public void Cleanup() => _sp?.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<int> AdaptiveOff_Baseline()
    {
        var results = await _pipeline.RetrieveAsync(
            "What is RAG?",
            new RetrievalOptions { TopK = 5, UseAdaptiveRetrieval = false });
        return results.IsSuccess ? results.Value.Count : 0;
    }

    [Benchmark]
    public async Task<int> Adaptive_SimpleQuery()
    {
        // 3-word query → heuristic classifies as "simple" without LLM
        var results = await _pipeline.RetrieveAsync(
            "What is RAG?",
            new RetrievalOptions { TopK = 5, UseAdaptiveRetrieval = true });
        return results.IsSuccess ? results.Value.Count : 0;
    }

    [Benchmark]
    public async Task<int> Adaptive_ComplexQuery()
    {
        // "how" keyword → heuristic classifies as "complex" without LLM
        var results = await _pipeline.RetrieveAsync(
            "how does retrieval augmented generation improve accuracy",
            new RetrievalOptions { TopK = 5, UseAdaptiveRetrieval = true });
        return results.IsSuccess ? results.Value.Count : 0;
    }

    [Benchmark]
    public async Task<int> Adaptive_AmbiguousQuery_LlmFallback()
    {
        // Long query, no complex/multi_hop keywords → falls back to mocked LLM
        var results = await _pipeline.RetrieveAsync(
            "retrieval augmented generation semantic vector database embedding storage index",
            new RetrievalOptions { TopK = 5, UseAdaptiveRetrieval = true });
        return results.IsSuccess ? results.Value.Count : 0;
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

    private sealed class FakeChatClient(string response) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
