using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the overhead of different answer engines.
/// Both pipelines use the same fake infrastructure (no real LLM calls).
/// The baseline uses the default <c>ChatAnswerEngine</c>; the second uses
/// <c>MapReduceAnswerEngine</c> which fans out one call per retrieved chunk
/// and then reduces the results.
/// </summary>
[MemoryDiagnoser]
public class AnswerEngineBenchmarks
{
    private IRagPipeline _chatPipeline      = null!;
    private IRagPipeline _mapReducePipeline = null!;
    private ServiceProvider _chatSp         = null!;
    private ServiceProvider _mapReduceSp    = null!;
    private byte[] _documentData           = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId  = new DocumentId("bench-doc-ae"),
        FileName    = "bench-ae.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));

        // --- baseline: default ChatAnswerEngine ---
        var chatServices = new ServiceCollection();
        chatServices.AddSingleton<IVectorStore, NoOpVectorStore>();
        chatServices.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        chatServices.AddSingleton<IChatClient>(new FakeChatClient());
        chatServices.AddRagNet();

        _chatSp = chatServices.BuildServiceProvider();
        _chatPipeline = _chatSp.GetRequiredService<IRagPipeline>();

        using var chatStream = new MemoryStream(_documentData);
        _ = await _chatPipeline.IngestAsync(chatStream, Metadata);

        // --- MapReduceAnswerEngine ---
        var mapReduceServices = new ServiceCollection();
        mapReduceServices.AddSingleton<IVectorStore, NoOpVectorStore>();
        mapReduceServices.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        mapReduceServices.AddSingleton<IChatClient>(new FakeChatClient());
        mapReduceServices.AddRagNet(rag => rag.UseMapReduceAnswerEngine());

        _mapReduceSp = mapReduceServices.BuildServiceProvider();
        _mapReducePipeline = _mapReduceSp.GetRequiredService<IRagPipeline>();

        using var mapReduceStream = new MemoryStream(_documentData);
        _ = await _mapReducePipeline.IngestAsync(mapReduceStream, Metadata);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _chatSp?.Dispose();
        _mapReduceSp?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<string> ChatAnswerEngine_Ask()
    {
        var result = await _chatPipeline.AskAsync("What is the quick brown fox?");
        return result.Answer;
    }

    [Benchmark]
    public async Task<string> MapReduceAnswerEngine_Ask()
    {
        var result = await _mapReducePipeline.AskAsync("What is the quick brown fox?");
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
