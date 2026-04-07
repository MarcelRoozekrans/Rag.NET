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
            .ThrowsAsync(new HttpRequestException("rate limit exceeded", null, System.Net.HttpStatusCode.TooManyRequests));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));

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

        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        await foreach (var u in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(u);

        Assert.Single(updates);
        Assert.Equal("streamed", updates[0].Text);
    }

    // Helper: async enumerable that throws immediately
    private static async IAsyncEnumerable<T> ThrowAsync<T>(Exception ex)
    {
        await Task.Yield();
        throw ex;
        yield break; // unreachable, satisfies compiler
    }

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
