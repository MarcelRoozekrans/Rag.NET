using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Resilience;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures the overhead of <see cref="FallbackChatClient"/> in two scenarios:
/// the primary client succeeds (no fallback), and the primary client fails transiently
/// forcing a retry against the secondary.
/// </summary>
[MemoryDiagnoser]
public class FallbackChatClientBenchmarks
{
    private FallbackChatClient _noFallback = null!;
    private FallbackChatClient _withFallback = null!;
    private IList<ChatMessage> _messages = null!;

    [GlobalSetup]
    public void Setup()
    {
        _messages = [new ChatMessage(ChatRole.User, "Hello")];

        // Baseline: single fast client, no fallback path exercised
        _noFallback = new FallbackChatClient([new InstantChatClient()]);

        // Fallback: primary throws transiently, secondary succeeds
        _withFallback = new FallbackChatClient(
        [
            new TransientFailingChatClient(),
            new InstantChatClient(),
        ]);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _noFallback.Dispose();
        _withFallback.Dispose();
    }

    /// <summary>Baseline: primary succeeds, no fallback overhead.</summary>
    [Benchmark(Baseline = true)]
    public Task<ChatResponse> GetResponseAsync_NoFallback()
        => _noFallback.GetResponseAsync(_messages);

    /// <summary>Primary fails transiently, secondary is invoked.</summary>
    [Benchmark]
    public Task<ChatResponse> GetResponseAsync_WithFallback()
        => _withFallback.GetResponseAsync(_messages);

    private sealed class InstantChatClient : IChatClient
    {
        private static readonly ChatResponse s_response =
            new(new ChatMessage(ChatRole.Assistant, "ok"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(s_response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TransientFailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(
                new HttpRequestException("rate limit", null, System.Net.HttpStatusCode.TooManyRequests));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
