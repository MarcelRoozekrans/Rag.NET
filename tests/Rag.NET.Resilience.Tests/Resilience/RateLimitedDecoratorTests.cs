using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

public class RateLimitedDecoratorTests
{
    private static IList<ChatMessage> AnyMessages() => [new ChatMessage(ChatRole.User, "hi")];

    // ── Chat: acquire ordering ───────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_AcquiresOnePermitBeforeInnerCall()
    {
        var log = new List<string>();
        var limiter = new RecordingRateLimiter(log);
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                log.Add("inner");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });

        var sut = new RateLimitedChatClient(inner, limiter);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Text);
        Assert.Equal(new[] { "acquire", "inner" }, log, StringComparer.Ordinal);
        Assert.Equal(1, limiter.LastPermits);
    }

    [Fact]
    public async Task GetResponseAsync_LimiterBlocked_InnerNotCalledUntilPermitGranted()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var limiter = new RecordingRateLimiter(gate: gate.Task);
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new RateLimitedChatClient(inner, limiter);
        var pending = sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, limiter.AcquireCount);
        await inner.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        gate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await inner.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_AcquireCancelled_InnerNeverCalled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var limiter = new RecordingRateLimiter(gate: gate.Task);
        var inner = Substitute.For<IChatClient>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var sut = new RateLimitedChatClient(inner, limiter);
        var pending = sut.GetResponseAsync(AnyMessages(), cancellationToken: cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await inner.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_QueueFullRejection_PropagatesAndInnerNeverCalled()
    {
        var limiter = new RecordingRateLimiter(throwOnAcquire: new InvalidOperationException("queue full"));
        var inner = Substitute.For<IChatClient>();

        var sut = new RateLimitedChatClient(inner, limiter);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));

        await inner.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Chat: streaming ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetStreamingResponseAsync_AcquiresOncePerStreamBeforeIteration()
    {
        var log = new List<string>();
        var limiter = new RecordingRateLimiter(log);
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                log.Add("inner");
                return YieldUpdates(
                    new ChatResponseUpdate { Contents = [new TextContent("a")] },
                    new ChatResponseUpdate { Contents = [new TextContent("b")] });
            });

        var sut = new RateLimitedChatClient(inner, limiter);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Equal(1, limiter.AcquireCount); // one permit covers the whole stream
        Assert.Equal(new[] { "acquire", "inner" }, log, StringComparer.Ordinal);
    }

    // ── Chat: delegation and ownership ───────────────────────────────────────

    [Fact]
    public void GetService_DelegatesToInner()
    {
        var inner = Substitute.For<IChatClient>();
        var sentinel = new object();
        inner.GetService(typeof(string), "key").Returns(sentinel);

        var sut = new RateLimitedChatClient(inner, new RecordingRateLimiter());

        Assert.Same(sentinel, sut.GetService(typeof(string), "key"));
    }

    [Fact]
    public void Dispose_DoesNotDisposeInnerOrLimiter()
    {
        var inner = Substitute.For<IChatClient>();
        var limiter = new RecordingRateLimiter();

        new RateLimitedChatClient(inner, limiter).Dispose();

        inner.DidNotReceive().Dispose();
        Assert.False(limiter.Disposed);
    }

    // ── Embedding generator ──────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_AcquiresOnePermitPerCallRegardlessOfValueCount()
    {
        var log = new List<string>();
        var limiter = new RecordingRateLimiter(log);
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                log.Add("inner");
                return new GeneratedEmbeddings<Embedding<float>>(
                    [new Embedding<float>(new float[] { 1f }), new Embedding<float>(new float[] { 2f }), new Embedding<float>(new float[] { 3f })]);
            });

        var sut = new RateLimitedEmbeddingGenerator(inner, limiter);
        var result = await sut.GenerateAsync(["one", "two", "three"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, limiter.AcquireCount); // per request, not per value
        Assert.Equal(1, limiter.LastPermits);
        Assert.Equal(new[] { "acquire", "inner" }, log, StringComparer.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_AcquireCancelled_InnerNeverCalled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var limiter = new RecordingRateLimiter(gate: gate.Task);
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var sut = new RateLimitedEmbeddingGenerator(inner, limiter);
        var pending = sut.GenerateAsync(["x"], cancellationToken: cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await inner.DidNotReceive().GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EmbeddingGetService_DelegatesToInner()
    {
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var sentinel = new object();
        inner.GetService(typeof(string), "key").Returns(sentinel);

        var sut = new RateLimitedEmbeddingGenerator(inner, new RecordingRateLimiter());

        Assert.Same(sentinel, sut.GetService(typeof(string), "key"));
    }

    [Fact]
    public void EmbeddingDispose_DoesNotDisposeInnerOrLimiter()
    {
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var limiter = new RecordingRateLimiter();

        new RateLimitedEmbeddingGenerator(inner, limiter).Dispose();

        inner.DidNotReceive().Dispose();
        Assert.False(limiter.Disposed);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<T> YieldUpdates<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    /// <summary>
    /// Deterministic <see cref="IRateLimiter"/> fake: records acquisitions (and their order via
    /// the shared <paramref name="log"/>), optionally waits on <paramref name="gate"/> so tests
    /// can hold callers at the limiter, and optionally throws <paramref name="throwOnAcquire"/>.
    /// </summary>
    private sealed class RecordingRateLimiter(
        List<string>? log = null,
        Task? gate = null,
        Exception? throwOnAcquire = null) : IRateLimiter
    {
        public int AcquireCount { get; private set; }
        public int LastPermits { get; private set; }
        public bool Disposed { get; private set; }

        public async ValueTask AcquireAsync(int permits = 1, CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            LastPermits = permits;
            log?.Add("acquire");
            if (throwOnAcquire is not null)
                throw throwOnAcquire;
            if (gate is not null)
                await gate.WaitAsync(cancellationToken);
        }

        public void Dispose() => Disposed = true;
    }
}
