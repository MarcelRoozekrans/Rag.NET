using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of the persistent memory decorator.
/// Both pipelines use fake infrastructure (no real LLM or vector store I/O)
/// to isolate the decorator's pass-through cost from real workloads.
/// </summary>
[MemoryDiagnoser]
public class MemoryBenchmarks
{
    private IRagPipeline _plainPipeline  = null!;
    private IRagPipeline _memoryPipeline = null!;
    private ServiceProvider _plainSp     = null!;
    private ServiceProvider _memorySp    = null!;
    private byte[] _documentData        = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId  = new DocumentId("bench-doc-mem"),
        FileName    = "bench-mem.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));

        // --- baseline: default pipeline, no memory ---
        var plainServices = new ServiceCollection();
        plainServices.AddSingleton<IVectorStore, NoOpVectorStore>();
        plainServices.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        plainServices.AddSingleton<IChatClient>(new FakeChatClient());
        plainServices.AddRagNet();

        _plainSp = plainServices.BuildServiceProvider();
        _plainPipeline = _plainSp.GetRequiredService<IRagPipeline>();

        using var plainStream = new MemoryStream(_documentData);
        _ = await _plainPipeline.IngestAsync(plainStream, Metadata);

        // --- pipeline with persistent memory decorator ---
        var memoryServices = new ServiceCollection();
        memoryServices.AddSingleton<IVectorStore, NoOpVectorStore>();
        memoryServices.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        memoryServices.AddSingleton<IChatClient>(new FakeChatClient());
        memoryServices.AddRagNet(rag =>
            rag.UseConversationMemory(configure: mem => mem.UsePersistentMemory()));

        _memorySp = memoryServices.BuildServiceProvider();
        _memoryPipeline = _memorySp.GetRequiredService<IRagPipeline>();

        using var memoryStream = new MemoryStream(_documentData);
        _ = await _memoryPipeline.IngestAsync(memoryStream, Metadata);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plainSp?.Dispose();
        _memorySp?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<string> Ask_WithoutMemory()
    {
        var result = await _plainPipeline.AskAsync("What is the quick brown fox?");
        return result.Answer;
    }

    [Benchmark]
    public async Task<string> Ask_WithPersistentMemory()
    {
        var result = await _memoryPipeline.AskAsync("What is the quick brown fox?");
        return result.Answer;
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

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Benchmark answer.")));

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
