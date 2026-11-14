using System.Diagnostics;
using System.Net;
using Rag.NET.DataProviders.Web;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// Covers the three <see cref="SitemapOptions"/> settings that scope a sitemap walk on a large
/// site: <see cref="SitemapOptions.IncludedUrlPrefixes"/>,
/// <see cref="SitemapOptions.RespectRobotsTxt"/> and
/// <see cref="SitemapOptions.MinimumRequestInterval"/>.
/// </summary>
/// <remarks>
/// Found while pointing the provider at a government site with ~120,000 URLs across 1,209
/// sharded sub-sitemaps, of which one theme of a few hundred pages was wanted. Exclusions alone
/// could only express that as a negative-lookahead regex, robots.txt was not consulted, and pages
/// were fetched back to back.
/// </remarks>
public sealed class SitemapScopeTests
{
    private const string SitemapUrl = "https://example.com/sitemap.xml";
    private const string RobotsUrl = "https://example.com/robots.txt";

    private const string Sitemap = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>https://example.com/index.html</loc></url>
          <url><loc>https://example.com/themes/health/vaccines</loc></url>
          <url><loc>https://example.com/themes/health/primary-care</loc></url>
          <url><loc>https://example.com/themes/housing/rent</loc></url>
          <url><loc>https://example.com/documents/report-1</loc></url>
        </urlset>
        """;

    private static FakeHttpMessageHandler Handler(string? robots = null, string xml = Sitemap)
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal) { [SitemapUrl] = xml };
        if (robots is not null) responses[RobotsUrl] = robots;
        return new FakeHttpMessageHandler(responses);
    }

    private static async Task<List<string>> UrlsAsync(SitemapOptions? options, FakeHttpMessageHandler? handler = null)
    {
        var entries = await new SitemapDataProvider(SitemapUrl, new HttpClient(handler ?? Handler()), options)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        return entries.Select(e => e.Value.Id.Value).ToList();
    }

    // ── IncludedUrlPrefixes ──────────────────────────────────────────────────

    [Fact]
    public async Task IncludedPrefixes_KeepOnlyUrlsBeneathThem()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            IncludedUrlPrefixes = ["https://example.com/themes/health/"],
        });

        Assert.Equal(
            ["https://example.com/themes/health/vaccines", "https://example.com/themes/health/primary-care"],
            urls);
    }

    [Fact]
    public async Task SeveralIncludedPrefixes_KeepTheUnion()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            IncludedUrlPrefixes = ["https://example.com/themes/health/", "https://example.com/themes/housing/"],
        });

        Assert.Equal(3, urls.Count);
    }

    [Fact]
    public async Task AnExclusion_WinsOverAnInclusion()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            IncludedUrlPrefixes = ["https://example.com/themes/"],
            ExcludedUrlPrefixes = ["https://example.com/themes/housing/"],
        });

        Assert.Equal(2, urls.Count);
        Assert.DoesNotContain("https://example.com/themes/housing/rent", urls, StringComparer.Ordinal);
    }

    [Fact]
    public async Task IncludedPrefixes_AreMatchedCaseInsensitively()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            IncludedUrlPrefixes = ["HTTPS://EXAMPLE.COM/THEMES/HEALTH/"],
        });

        Assert.Equal(2, urls.Count);
    }

    /// <remarks>
    /// An all-blank list must mean "no inclusion filter", not "include nothing": the same
    /// rule as <see cref="SitemapOptions.ExcludedUrlPrefixes"/>, where a blank is dropped rather
    /// than silently emptying the result.
    /// </remarks>
    [Fact]
    public async Task OnlyBlankIncludedPrefixes_AreIgnored()
    {
        var urls = await UrlsAsync(new SitemapOptions { IncludedUrlPrefixes = ["", "  "] });

        Assert.Equal(5, urls.Count);
    }

    /// <remarks>
    /// An index link's URL says nothing about the pages inside it when the index is sharded
    /// (<c>/sitemap/1.xml</c>, <c>/sitemap/2.xml</c>), so inclusion must not prune index links —
    /// otherwise every sharded site would yield nothing at all.
    /// </remarks>
    [Fact]
    public async Task IncludedPrefixes_DoNotPruneNestedSitemaps()
    {
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SitemapUrl] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <sitemap><loc>https://example.com/sitemap/1.xml</loc></sitemap>
                </sitemapindex>
                """,
            ["https://example.com/sitemap/1.xml"] = Sitemap,
        });

        var urls = await UrlsAsync(
            new SitemapOptions { IncludedUrlPrefixes = ["https://example.com/themes/health/"], RespectRobotsTxt = false },
            handler);

        Assert.Equal(2, urls.Count);
    }

    // ── Duplicates ───────────────────────────────────────────────────────────

    /// <remarks>
    /// Found on the same government site: five theme pages appear in two shards each. Yielded
    /// twice, each was fetched, embedded and stored twice, and the ingestion result listed it twice.
    /// </remarks>
    [Fact]
    public async Task AUrlListedInTwoSitemaps_IsYieldedOnce()
    {
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SitemapUrl] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <sitemap><loc>https://example.com/sitemap/1.xml</loc></sitemap>
                  <sitemap><loc>https://example.com/sitemap/2.xml</loc></sitemap>
                </sitemapindex>
                """,
            ["https://example.com/sitemap/1.xml"] = Sitemap,
            ["https://example.com/sitemap/2.xml"] = Sitemap,
        });

        var urls = await UrlsAsync(new SitemapOptions { RespectRobotsTxt = false }, handler);

        Assert.Equal(5, urls.Count);
        Assert.Equal(urls.Count, urls.Distinct(StringComparer.Ordinal).Count());
    }

    // ── RespectRobotsTxt ─────────────────────────────────────────────────────

    [Fact]
    public async Task ByDefault_RobotsTxtDisallowedPagesAreSkipped()
    {
        var urls = await UrlsAsync(
            options: null,
            Handler(robots: "User-agent: *\nDisallow: /documents/\n"));

        Assert.Equal(4, urls.Count);
        Assert.DoesNotContain("https://example.com/documents/report-1", urls, StringComparer.Ordinal);
    }

    [Fact]
    public async Task WithRespectRobotsTxtOff_DisallowedPagesAreKept()
    {
        var urls = await UrlsAsync(
            new SitemapOptions { RespectRobotsTxt = false },
            Handler(robots: "User-agent: *\nDisallow: /documents/\n"));

        Assert.Equal(5, urls.Count);
    }

    [Fact]
    public async Task AMissingRobotsTxt_AllowsEverything()
    {
        // The handler answers 404 for robots.txt.
        Assert.Equal(5, (await UrlsAsync(options: null)).Count);
    }

    /// <remarks>
    /// #998: RFC 9309 section 2.3.1.4 reads an unreachable robots.txt as disallow-all. The host is
    /// reported once, not once per page, and listed nothing.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableRobotsTxt_ReportsTheHostOnce_AndSkipsItsPages()
    {
        var handler = new FakeHttpMessageHandler(
            new Dictionary<string, string>(StringComparer.Ordinal) { [SitemapUrl] = Sitemap },
            new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal) { [RobotsUrl] = HttpStatusCode.ServiceUnavailable });

        var entries = await new SitemapDataProvider(SitemapUrl, new HttpClient(handler))
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsFailure);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<RagError.HttpFailed>(entry.Error).StatusCode);
    }

    [Fact]
    public async Task ARobotsTxtThatCannotBeFetched_ReportsTransportFailed()
    {
        var entries = await new SitemapDataProvider(SitemapUrl, new HttpClient(new ThrowingRobotsHandler(
                new HttpRequestException("Connection refused"))))
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.IsType<RagError.TransportFailed>(entry.Error);
    }

    [Fact]
    public async Task CallerCancellationDuringTheRobotsFetch_StopsTheWalk()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ThrowingRobotsHandler(new TaskCanceledException("Cancelled by the caller."), cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await new SitemapDataProvider(SitemapUrl, new HttpClient(handler))
                .GetFilesAsync(cts.Token)
                .ToListAsync(cts.Token));
    }

    [Fact]
    public async Task WithRespectRobotsTxtOff_AFailingRobotsTxtIsNeverFetched()
    {
        var handler = new FakeHttpMessageHandler(
            new Dictionary<string, string>(StringComparer.Ordinal) { [SitemapUrl] = Sitemap },
            new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal) { [RobotsUrl] = HttpStatusCode.ServiceUnavailable });

        var urls = await UrlsAsync(new SitemapOptions { RespectRobotsTxt = false }, handler);

        Assert.Equal(5, urls.Count);
        Assert.Equal(1, handler.RequestCount); // the sitemap only; page bodies are fetched lazily
    }

    /// <summary>Serves <see cref="Sitemap"/>; robots.txt throws, cancelling the caller first if given.</summary>
    private sealed class ThrowingRobotsHandler(Exception failure, CancellationTokenSource? cancel = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (string.Equals(request.RequestUri!.ToString(), RobotsUrl, StringComparison.Ordinal))
            {
                cancel?.Cancel();
                throw failure;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sitemap) });
        }
    }

    [Fact]
    public async Task AnUnreachableHost_DoesNotStopTheOtherHost()
    {
        const string twoHosts = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/a</loc></url>
              <url><loc>https://other.example/c</loc></url>
              <url><loc>https://example.com/b</loc></url>
              <url><loc>https://other.example/d</loc></url>
            </urlset>
            """;
        var handler = new FakeHttpMessageHandler(
            new Dictionary<string, string>(StringComparer.Ordinal) { [SitemapUrl] = twoHosts },
            new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal) { [RobotsUrl] = HttpStatusCode.ServiceUnavailable });

        var entries = await new SitemapDataProvider(SitemapUrl, new HttpClient(handler))
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.IsType<RagError.HttpFailed>(Assert.Single(entries, e => e.IsFailure).Error);
        Assert.Equal(
            ["https://other.example/c", "https://other.example/d"],
            entries.Where(e => e.IsSuccess).Select(e => e.Value.Id.Value).ToList());
    }

    [Fact]
    public async Task RobotsTxt_IsFetchedOncePerHost()
    {
        var handler = Handler(robots: "User-agent: *\nDisallow: /documents/\n");

        _ = await UrlsAsync(options: null, handler);

        // The sitemap and one robots.txt; page bodies are fetched lazily and are not opened here.
        Assert.Equal(2, handler.RequestCount);
    }

    // ── MinimumRequestInterval ───────────────────────────────────────────────

    [Fact]
    public async Task MinimumRequestInterval_SpacesOutPageFetches()
    {
        var interval = TimeSpan.FromMilliseconds(150);
        var responses = new Dictionary<string, string>(StringComparer.Ordinal) { [SitemapUrl] = Sitemap };
        foreach (var path in new[] { "index.html", "themes/health/vaccines", "themes/health/primary-care" })
        {
            responses["https://example.com/" + path] = "<html></html>";
        }

        var sut = new SitemapDataProvider(
            SitemapUrl,
            new HttpClient(new FakeHttpMessageHandler(responses)),
            new SitemapOptions { MinimumRequestInterval = interval, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            await using var _ = await entries[i].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        }

        // Three page fetches after the sitemap fetch: each waits a full interval. A lower bound
        // only, so a slow machine cannot fail it.
        Assert.True(
            stopwatch.Elapsed >= interval * 3 - TimeSpan.FromMilliseconds(15),
            $"Three fetches took {stopwatch.Elapsed.TotalMilliseconds} ms; expected at least {interval.TotalMilliseconds * 3} ms.");
    }

    /// <remarks>#927: the Crawl-delay of the page host's robots.txt raises the interval. The
    /// stopwatch starts before the walk: the robots.txt fetch is the start the first page open
    /// waits on.</remarks>
    [Fact]
    public async Task CrawlDelay_SpacesSitemapPageFetches()
    {
        var delay = TimeSpan.FromMilliseconds(150);
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SitemapUrl] = Sitemap,
            [RobotsUrl] = "User-agent: *\nCrawl-delay: 0.15\n",
        };
        foreach (var path in new[] { "index.html", "themes/health/vaccines", "themes/health/primary-care" })
        {
            responses["https://example.com/" + path] = "<html></html>";
        }

        var sut = new SitemapDataProvider(SitemapUrl, new HttpClient(new FakeHttpMessageHandler(responses)));
        var stopwatch = Stopwatch.StartNew();

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            await using var _ = await entries[i].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(
            stopwatch.Elapsed >= delay * 3 - TimeSpan.FromMilliseconds(15),
            $"robots.txt and three page fetches took {stopwatch.Elapsed.TotalMilliseconds} ms; expected at least {delay.TotalMilliseconds * 3} ms.");
    }

    [Fact]
    public void ANegativeMinimumRequestInterval_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SitemapDataProvider(
            SitemapUrl,
            new HttpClient(Handler()),
            new SitemapOptions { MinimumRequestInterval = TimeSpan.FromSeconds(-1) }));
    }
}
