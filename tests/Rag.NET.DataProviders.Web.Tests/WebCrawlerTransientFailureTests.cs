using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// A page whose response breaks off, or times out, is skipped; the crawl goes on.
/// </summary>
/// <remarks>
/// Found on a live 675-page crawl that died on one dropped connection: the per-page catch took only
/// <see cref="HttpRequestException"/>, while a body that ends early throws
/// <see cref="HttpIOException"/>, an <see cref="IOException"/>, and an <see cref="HttpClient"/>
/// timeout throws <see cref="TaskCanceledException"/>.
/// </remarks>
public sealed class WebCrawlerTransientFailureTests
{
    private sealed class FailingHandler(Dictionary<string, string> pages, string failingUrl, Exception failure)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (string.Equals(url, failingUrl, StringComparison.Ordinal)) throw failure;
            return Task.FromResult(pages.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    public static TheoryData<string> Failures => ["io", "timeout"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedPage_IsSkipped_AndTheCrawlContinues(string kind)
    {
        Exception failure = string.Equals(kind, "io", StringComparison.Ordinal)
            ? new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/"] = """<a href="/broken">x</a><a href="/fine">y</a>""",
            ["https://example.com/fine"] = "<p>fine</p>",
        };

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(new FailingHandler(pages, "https://example.com/broken", failure)),
                new WebCrawlerOptions { RespectRobotsTxt = false })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["https://example.com", "https://example.com/fine"],
            entries.Select(e => e.Value.Id.Value).ToList());
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedRobotsTxt_AllowsEverything(string kind)
    {
        Exception failure = string.Equals(kind, "io", StringComparison.Ordinal)
            ? new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var pages = new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/"] = "<p>home</p>" };

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(new FailingHandler(pages, "https://example.com/robots.txt", failure)),
                new WebCrawlerOptions { RespectRobotsTxt = true })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com", Assert.Single(entries).Value.Id.Value);
    }

    [Fact]
    public async Task CallerCancellation_StillStopsTheCrawl()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var pages = new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/"] = "<p>home</p>" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await new WebCrawlerDataProvider(
                    "https://example.com/",
                    new HttpClient(new FailingHandler(pages, "none", new InvalidOperationException())),
                    new WebCrawlerOptions { RespectRobotsTxt = false })
                .GetFilesAsync(cts.Token)
                .ToListAsync(cts.Token));
    }
}
