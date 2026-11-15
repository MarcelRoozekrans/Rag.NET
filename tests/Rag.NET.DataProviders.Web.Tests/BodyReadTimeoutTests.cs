using Rag.NET.Models;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// <see cref="HttpClient.Timeout"/> bounds a response body that stalls after its headers, and its
/// expiry reads as a timeout, not as the caller cancelling (#1009).
/// </summary>
/// <remarks>
/// Each test gives the caller a token that fires long after the client timeout. Before the fix the
/// read ran until that token fired, so the exception came from the caller and
/// <see cref="FetchFailure.IsSkippable"/> rejected it.
/// </remarks>
public sealed class BodyReadTimeoutTests
{
    private static readonly TimeSpan s_clientTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_callerDeadline = TimeSpan.FromSeconds(20);

    private static HttpClient ClientFor(HttpMessageHandler? handler = null) =>
        new(handler ?? new SocketsHttpHandler()) { Timeout = s_clientTimeout };

    [Fact]
    public async Task FullRead_OfAStalledBody_TimesOutAsATimeout()
    {
        await using var server = new StalledBodyServer();
        using var client = ClientFor();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(s_callerDeadline);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetStringWithCharsetFallbackAsync(new Uri(server.BaseUri, "page").ToString(), caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.True(FetchFailure.IsSkippable(ex, caller.Token));
    }

    [Fact]
    public async Task PrefixRead_OfAStalledBody_TimesOutAsATimeout()
    {
        await using var server = new StalledBodyServer();
        using var client = ClientFor();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(s_callerDeadline);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetTextPrefixWithCharsetFallbackAsync(new Uri(server.BaseUri, "robots.txt").ToString(), 500 * 1024, caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.True(FetchFailure.IsSkippable(ex, caller.Token));
    }

    [Fact]
    public async Task PrefixRead_CallerCancellationDuringAStalledBody_IsNotATimeout()
    {
        await using var server = new StalledBodyServer();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetTextPrefixWithCharsetFallbackAsync(new Uri(server.BaseUri, "robots.txt").ToString(), 500 * 1024, caller.Token));

        Assert.True(caller.IsCancellationRequested);
        Assert.IsNotType<TimeoutException>(ex.InnerException);
        Assert.False(FetchFailure.IsSkippable(ex, caller.Token));
    }

    [Fact]
    public async Task PrefixRead_WithAnInfiniteClientTimeout_ReadsAWholeBody()
    {
        using var client = new HttpClient(new FakeBodyHandler("User-agent: *\nDisallow: /x\n"))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var (text, truncated) = await client.GetTextPrefixWithCharsetFallbackAsync(
            "https://example.com/robots.txt", 500 * 1024, TestContext.Current.CancellationToken);

        Assert.Equal("User-agent: *\nDisallow: /x\n", text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task ACrawledPageWithAStalledBody_IsReported_AndTheCrawlContinues()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(s_callerDeadline);
        var handler = new RoutingHandler(url => url switch
        {
            "https://example.com/" => Page("""<a href="/stalled">x</a><a href="/fine">y</a>"""),
            "https://example.com/stalled" => StalledBodyContent.Response(),
            "https://example.com/fine" => Page("<p>fine</p>"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/", ClientFor(handler), new WebCrawlerOptions { RespectRobotsTxt = false })
            .GetFilesAsync(caller.Token)
            .ToListAsync(caller.Token);

        Assert.False(caller.IsCancellationRequested);
        var error = Assert.IsType<RagError.TransportFailed>(Assert.Single(entries, e => e.IsFailure).Error);
        Assert.Contains("https://example.com/stalled", error.Inner.Message, StringComparison.Ordinal);
        Assert.Equal(2, entries.Count(e => e.IsSuccess));
    }

    /// <remarks>
    /// The status decides before the body is read: buffering the body first would turn a 404 whose
    /// body stalls into a timeout, reported as a page that may come back, and a 503 into a
    /// transport failure without its status.
    /// </remarks>
    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    public async Task AnErrorStatusWithAStalledBody_KeepsItsStatus(int status)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(s_callerDeadline);
        using var client = new HttpClient(new RoutingHandler(_ =>
            StalledBodyContent.Response("<p>error", (System.Net.HttpStatusCode)status)))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var full = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetStringWithCharsetFallbackAsync("https://example.com/page", caller.Token));
        var prefix = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetTextPrefixWithCharsetFallbackAsync("https://example.com/robots.txt", 500 * 1024, caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.Equal((System.Net.HttpStatusCode)status, full.StatusCode);
        Assert.Equal((System.Net.HttpStatusCode)status, prefix.StatusCode);
    }

    [Fact]
    public async Task ARobotsTxtWithAStalledBody_IsUnreachable()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(s_callerDeadline);
        var handler = new RoutingHandler(_ => StalledBodyContent.Response("User-agent: *\n"));

        var robots = await RobotsTxt.LoadAsync(ClientFor(handler), new Uri("https://example.com/"), caller.Token);

        Assert.False(caller.IsCancellationRequested);
        Assert.IsType<RagError.TransportFailed>(robots.Unreachable);
        Assert.True(robots.IsDisallowed("https://example.com/any"));
    }

    private static HttpResponseMessage Page(string html) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) };

    private sealed class RoutingHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request.RequestUri!.ToString()));
    }

    private sealed class FakeBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Page(body));
    }
}
