using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

public class FallbackChatClientTests
{
    private static IList<ChatMessage> AnyMessages() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task GetResponseAsync_PrimarySucceeds_SecondaryNeverCalled()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_PrimaryTransientFailure_SecondarySucceeds()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("upstream error", null, System.Net.HttpStatusCode.TooManyRequests));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback ok", result.Text);
    }

    [Fact]
    public async Task GetResponseAsync_NonTransientException_PropagatesImmediately()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("bad config"));

        var sut = new FallbackChatClient([primary, secondary]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("bad config", ex.Message, StringComparison.Ordinal);

        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_AllClientsFail_ThrowsLastException()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("rate limit", null, System.Net.HttpStatusCode.TooManyRequests));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var sut = new FallbackChatClient([primary, secondary]);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("unavailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_429StatusCode_IsTransient()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("429", null, System.Net.HttpStatusCode.TooManyRequests));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task GetResponseAsync_MessageContainsRateLimit_IsTransient()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("You hit the rate limit for this model"));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrimaryTransientFailure_SecondaryUsed()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ThrowAsync<ChatResponseUpdate>(new HttpRequestException("rate limit", null, System.Net.HttpStatusCode.TooManyRequests)));
        secondary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(YieldUpdates(new ChatResponseUpdate { Contents = [new TextContent("streamed")] }));

        var sut = new FallbackChatClient([primary, secondary]);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Single(updates);
        Assert.Equal("streamed", updates[0].Text);
    }

    // ── Per-client timeout ───────────────────────────────────────────────────

    [Fact]
    public void Ctor_NonPositivePerClientTimeout_Throws()
    {
        var client = Substitute.For<IChatClient>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FallbackChatClient([client], perClientTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FallbackChatClient([client], perClientTimeout: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task GetResponseAsync_HungPrimaryWithTimeout_SecondaryServes()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangUntilCancelledAsync(ci.Arg<CancellationToken>()));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback ok")));

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromMilliseconds(50));
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("fallback ok", result.Text);
    }

    [Fact]
    public async Task GetResponseAsync_CallerCancelsDuringHungPrimary_ThrowsOceSecondaryUntouched()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangUntilCancelledAsync(ci.Arg<CancellationToken>()));
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromSeconds(30));
        var task = sut.GetResponseAsync(AnyMessages(), cancellationToken: callerCts.Token);

        await callerCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_NoTimeoutConfigured_HungPrimaryStaysHung()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangUntilCancelledAsync(ci.Arg<CancellationToken>()));
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var sut = new FallbackChatClient([primary, secondary]);
        var task = sut.GetResponseAsync(AnyMessages(), cancellationToken: callerCts.Token);

        // Bounded observation window: the call must still be pending — no implicit timeout.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            task.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.False(task.IsCompleted);

        // Clean up deterministically: cancel the hung call.
        await callerCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_HungPrimaryWithTimeout_SecondaryStreams()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangingStreamAsync(ci.Arg<CancellationToken>()));
        secondary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(YieldUpdates(new ChatResponseUpdate { Contents = [new TextContent("streamed")] }));

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromMilliseconds(50));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Single(updates);
        Assert.Equal("streamed", updates[0].Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_MidStreamTimeout_NextClientRestartsFromBeginning()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => YieldThenHangAsync(
                new ChatResponseUpdate { Contents = [new TextContent("partial")] }, ci.Arg<CancellationToken>()));
        secondary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(YieldUpdates(new ChatResponseUpdate { Contents = [new TextContent("restarted")] }));

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromMilliseconds(50));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        // The already-yielded prefix is not retracted; the next client streams from the beginning.
        Assert.Equal(2, updates.Count);
        Assert.Equal("partial", updates[0].Text);
        Assert.Equal("restarted", updates[1].Text);
    }

    [Fact]
    public async Task GetResponseAsync_AllClientsTimeout_ThrowsTimeoutExceptionWithInnerOce()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangUntilCancelledAsync(ci.Arg<CancellationToken>()));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangUntilCancelledAsync(ci.Arg<CancellationToken>()));

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.Contains("per-client timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AllClientsTimeout_ThrowsTimeoutExceptionWithInnerOce()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangingStreamAsync(ci.Arg<CancellationToken>()));
        secondary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangingStreamAsync(ci.Arg<CancellationToken>()));

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task GetResponseAsync_SpontaneousClientOceWithTimeoutArmed_RethrowsWithoutFallback()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("client's own internal cancellation"));

        var sut = new FallbackChatClient([primary, secondary], perClientTimeout: TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));

        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // Helper: streaming call that yields one update, then hangs but honors cancellation
    private static async IAsyncEnumerable<ChatResponseUpdate> YieldThenHangAsync(
        ChatResponseUpdate first,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return first;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        await tcs.Task;
    }

    // Helper: chat call that never completes but honors cancellation (like a real HTTP client)
    private static async Task<ChatResponse> HangUntilCancelledAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }

    // Helper: streaming call that hangs before the first token but honors cancellation
    private static async IAsyncEnumerable<ChatResponseUpdate> HangingStreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        await tcs.Task;
        yield break;
    }

    // Helper: async enumerable that throws immediately
    private static async IAsyncEnumerable<T> ThrowAsync<T>(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // Unreachable code detected
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public void IsTransient_BudgetExceededException_IsNotTransient()
    {
        // Pinned by TYPE (mirroring the limiter's queue-full wording pins): a blown budget
        // must never trigger provider fallback — retrying against the next client would
        // keep spending past the limit.
        var ex = new Rag.NET.Models.BudgetExceededException(Rag.NET.Models.CostWindow.Day, limit: 10m, spend: 10m);

        Assert.False(FallbackChatClient.IsTransient(ex));
    }

    /// <summary>
    /// A provider returning <c>finish_reason: "error"</c> is a provider outage, so it must reach
    /// the next client rather than propagating.
    /// <para>
    /// The OpenAI SDK throws this while deserialising the response, before any Rag.NET code sees
    /// it, and it arrives as a bare <see cref="ArgumentOutOfRangeException"/> — matching none of
    /// the status codes and none of the transient keywords. So the one failure fallback exists for
    /// did not trigger fallback (issue #143). The exact exception the SDK constructs is
    /// reproduced here rather than approximated.
    /// </para>
    /// </summary>
    [Fact]
    public void IsTransient_ProviderReturnedErrorFinishReason_IsTransient()
    {
        Assert.True(FallbackChatClient.IsTransient(UnknownFinishReason("error")));
    }

    /// <summary>
    /// The match stays narrow: an ordinary argument error is a caller bug, and retrying it against
    /// every configured provider in turn would turn one mistake into N failed calls.
    /// </summary>
    [Fact]
    public void IsTransient_OrdinaryArgumentOutOfRange_IsNotTransient()
    {
        Assert.False(FallbackChatClient.IsTransient(TopKOutOfRange(0)));
    }

    /// <summary>The exact exception OpenAI's SDK constructs for an unrecognised finish reason.</summary>
    /// <param name="value">The unrecognised finish reason, e.g. <c>"error"</c>.</param>
    /// <returns>The SDK's exception.</returns>
    private static ArgumentOutOfRangeException UnknownFinishReason(string value) =>
        new(nameof(value), value, "Unknown ChatFinishReason value.");

    /// <summary>An ordinary caller mistake of the same exception type.</summary>
    /// <param name="topK">The offending value.</param>
    /// <returns>A run-of-the-mill argument error.</returns>
    private static ArgumentOutOfRangeException TopKOutOfRange(int topK) =>
        new(nameof(topK), topK, "TopK must be greater than 0.");

    // Helper: async enumerable yielding items
    private static async IAsyncEnumerable<T> YieldUpdates<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
