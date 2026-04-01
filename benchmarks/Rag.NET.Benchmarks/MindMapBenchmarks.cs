using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Graph;
using Rag.NET.GraphRag;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the overhead of MindMapExtractor:
/// - JSON parse and tree building cost (no graph store)
/// - Persistence write cost (SQLite in-memory)
/// Parameterised by tree depth: 1 (root only), 2 (root + 3 children), 3 (root + 3 + 9).
/// All LLM calls are stubbed via FakeChatClient.
/// </summary>
[MemoryDiagnoser]
public class MindMapBenchmarks
{
    [Params(1, 2, 3)]
    public int Depth { get; set; }

    private MindMapExtractor _extractorNoStore = null!;
    private MindMapExtractor _extractorWithStore = null!;
    private SqliteGraphStore _graphStore = null!;

    [GlobalSetup]
    public void Setup()
    {
        // fakeClient is stateless and can be shared; options are value-like. Separate instances for safety.
        _graphStore = new SqliteGraphStore(":memory:");
        _extractorNoStore = new MindMapExtractor(
            new FakeMindMapChatClient(Depth),
            graphStore: null,
            new MindMapOptions { MaxDepth = Depth });
    }

    [IterationSetup(Target = nameof(Extract_WithGraphStore))]
    public void IterationSetupWithStore()
    {
        // Recreate store + extractor so the relationship table starts empty each iteration.
        _graphStore?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _graphStore = new SqliteGraphStore(":memory:");
        _extractorWithStore = new MindMapExtractor(
            new FakeMindMapChatClient(Depth),
            _graphStore,
            new MindMapOptions { MaxDepth = Depth });
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _graphStore.DisposeAsync().ConfigureAwait(false);

    [Benchmark(Baseline = true)]
    public async Task<MindMapNode> Extract_InMemoryOnly()
        => await _extractorNoStore.ExtractAsync("benchmark document text", "bench-doc", default)
            .ConfigureAwait(false);

    [Benchmark]
    public async Task<MindMapNode> Extract_WithGraphStore()
        => await _extractorWithStore.ExtractAsync("benchmark document text", "bench-doc", default)
            .ConfigureAwait(false);

    // ── Fake chat client ────────────────────────────────────────────────

    private sealed class FakeMindMapChatClient(int depth) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, BuildJson(depth))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private static string BuildJson(int remainingDepth)
        {
            if (remainingDepth <= 1)
                return """{"title":"Leaf","summary":"Leaf node.","children":[]}""";

            var children = string.Join(",",
                Enumerable.Range(1, 3).Select(i =>
                    $"{{\"title\":\"Child {i}\",\"summary\":\"Child {i} summary.\",\"children\":[{BuildChildrenJson(remainingDepth - 2)}]}}"));
            return $"{{\"title\":\"Root\",\"summary\":\"Root summary.\",\"children\":[{children}]}}";
        }

        private static string BuildChildrenJson(int remainingDepth)
        {
            if (remainingDepth <= 0) return string.Empty;
            return string.Join(",",
                Enumerable.Range(1, 3).Select(i =>
                    $"{{\"title\":\"Node {i}\",\"summary\":\"Node {i}.\",\"children\":[]}}"));
        }
    }
}
