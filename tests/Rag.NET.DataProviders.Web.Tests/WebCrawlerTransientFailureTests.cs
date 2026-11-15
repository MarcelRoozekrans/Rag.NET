using Rag.NET.DataProviders.Web;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// A page whose response breaks off, or times out, is reported; the crawl goes on.
/// </summary>
/// <remarks>
/// Found on a live 675-page crawl that died on one dropped connection: the per-page catch took only
/// <see cref="HttpRequestException"/>, while a body that ends early throws
/// <see cref="HttpIOException"/>, an <see cref="IOException"/>, and an <see cref="HttpClient"/>
/// timeout throws <see cref="TaskCanceledException"/>.
/// </remarks>
public sealed class WebCrawlerTransientFailureTests
{
    private sealed class FailingHandler(Dictionary<string, string> pages, string failingUrl, Exception? failure)
        : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (string.Equals(url, failingUrl, StringComparison.Ordinal))
            {
                // A reset fails inside the body read, after the headers arrived.
                if (failure is null) return Task.FromResult(BodyResetContent.Response());
                throw failure;
            }
            return Task.FromResult(pages.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    public static TheoryData<string> Failures => ["io", "timeout", "reset"];

    /// <summary>The failure for <paramref name="kind"/>; <see langword="null"/> for a reset during the body.</summary>
    private static Exception? FailureOf(string kind) => kind switch
    {
        "io" => new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
        "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
        _ => null,
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedPage_IsReported_AndTheCrawlContinues(string kind)
    {
        var failure = FailureOf(kind);
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

        // Reported, so full cleanup cannot delete a page that may come back.
        var reported = Assert.Single(entries, e => e.IsFailure);
        var error = Assert.IsType<RagError.TransportFailed>(reported.Error);
        Assert.Contains("https://example.com/broken", error.Inner.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["https://example.com", "https://example.com/fine"],
            entries.Where(e => e.IsSuccess).Select(e => e.Value.Id.Value).ToList());
    }

    /// <summary>A server error may pass, so the page is reported; a 404 says it is gone, so it is not.</summary>
    [Theory]
    [InlineData(503, true)]
    [InlineData(429, true)]
    [InlineData(404, false)]
    [InlineData(410, false)]
    public async Task APageStatus_IsReportedOnlyWhenItMayPass(int status, bool reported)
    {
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/"] = """<a href="/broken">x</a><a href="/fine">y</a>""",
            ["https://example.com/fine"] = "<p>fine</p>",
        };
        var failure = new HttpRequestException("status", null, (System.Net.HttpStatusCode)status);

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(new FailingHandler(pages, "https://example.com/broken", failure)),
                new WebCrawlerOptions { RespectRobotsTxt = false })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var failures = entries.Where(e => e.IsFailure).ToList();
        if (reported)
        {
            var error = Assert.IsType<RagError.HttpFailed>(Assert.Single(failures).Error);
            Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode);
        }
        else
        {
            Assert.Empty(failures);
        }

        Assert.Equal(2, entries.Count(e => e.IsSuccess));
    }

    /// <summary>Answers robots.txt with one status and every other URL with a page.</summary>
    private sealed class RobotsStatusHandler(System.Net.HttpStatusCode robotsStatus, string page = "<p>home</p>")
        : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(string.Equals(request.RequestUri.AbsolutePath, "/robots.txt", StringComparison.Ordinal)
                ? new HttpResponseMessage(robotsStatus)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(page) });
        }
    }

    /// <remarks>
    /// #998: RFC 9309 section 2.3.1.4 reads an unreachable robots.txt as disallow-all. Until then
    /// a failed fetch allowed everything, so an outage crawled the whole site.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedRobotsTxt_ReportsTheHost_AndListsNothing(string kind)
    {
        var failure = FailureOf(kind);
        var pages = new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/"] = "<p>home</p>" };
        var handler = new FailingHandler(pages, "https://example.com/robots.txt", failure);

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(handler),
                new WebCrawlerOptions { RespectRobotsTxt = true })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsFailure);
        Assert.IsType<RagError.TransportFailed>(entry.Error);
        Assert.Equal(["https://example.com/robots.txt"], handler.Requested);
    }

    [Fact]
    public async Task AServerErrorRobotsTxt_ReportsHttpFailed()
    {
        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(new RobotsStatusHandler(System.Net.HttpStatusCode.ServiceUnavailable)),
                new WebCrawlerOptions { RespectRobotsTxt = true })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsFailure);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
            Assert.IsType<RagError.HttpFailed>(entry.Error).StatusCode);
    }

    [Fact]
    public async Task WithRespectRobotsTxtOff_AFailingRobotsTxtIsNeverFetched()
    {
        var handler = new RobotsStatusHandler(System.Net.HttpStatusCode.ServiceUnavailable);

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(handler),
                new WebCrawlerOptions { RespectRobotsTxt = false })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsSuccess);
        Assert.Equal("https://example.com", entry.Value.Id.Value);
        Assert.DoesNotContain("https://example.com/robots.txt", handler.Requested, StringComparer.Ordinal);
    }

    /// <remarks>
    /// With <see cref="WebCrawlerOptions.SameDomain"/> off the seed's robots.txt gates every host,
    /// so an unreachable one stops the whole crawl, links to other hosts included.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableSeedRobotsTxt_StopsTheCrawl_EvenAcrossDomains()
    {
        var handler = new RobotsStatusHandler(
            System.Net.HttpStatusCode.ServiceUnavailable, """<a href="https://other.example/page">x</a>""");

        var entries = await new WebCrawlerDataProvider(
                "https://example.com/",
                new HttpClient(handler),
                new WebCrawlerOptions { RespectRobotsTxt = true, SameDomain = false })
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(entries).IsFailure);
        Assert.Equal(["https://example.com/robots.txt"], handler.Requested);
    }

    [Fact]
    public async Task CallerCancellationDuringTheRobotsFetch_StopsTheCrawl()
    {
        using var cts = new CancellationTokenSource();
        var pages = new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/"] = "<p>home</p>" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await new WebCrawlerDataProvider(
                    "https://example.com/",
                    new HttpClient(new CancellingHandler(pages, "https://example.com/robots.txt", cts)),
                    new WebCrawlerOptions { RespectRobotsTxt = true })
                .GetFilesAsync(cts.Token)
                .ToListAsync(cts.Token));
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

    /// <remarks>
    /// The other half of the timeout rule: a <see cref="TaskCanceledException"/> raised because
    /// the caller cancelled mid-request must end the walk, not be skipped as a timed-out page.
    /// </remarks>
    [Fact]
    public async Task CallerCancellationDuringARequest_StopsTheCrawl()
    {
        using var cts = new CancellationTokenSource();
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/"] = """<a href="/slow">x</a><a href="/fine">y</a>""",
            ["https://example.com/fine"] = "<p>fine</p>",
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await new WebCrawlerDataProvider(
                    "https://example.com/",
                    new HttpClient(new CancellingHandler(pages, "https://example.com/slow", cts)),
                    new WebCrawlerOptions { RespectRobotsTxt = false })
                .GetFilesAsync(cts.Token)
                .ToListAsync(CancellationToken.None));
    }

    private sealed class CancellingHandler(Dictionary<string, string> pages, string cancelAt, CancellationTokenSource cts)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (string.Equals(url, cancelAt, StringComparison.Ordinal))
            {
                cts.Cancel();
                throw new TaskCanceledException("Cancelled by the caller.");
            }

            return Task.FromResult(pages.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
