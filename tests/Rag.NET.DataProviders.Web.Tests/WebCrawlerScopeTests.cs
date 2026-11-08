using System.Diagnostics;
using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// Covers <see cref="WebCrawlerOptions.IncludedUrlPrefixes"/> and
/// <see cref="WebCrawlerOptions.MinimumRequestInterval"/>.
/// </summary>
/// <remarks>
/// Found on a government site whose FAQ answer pages are missing from its sitemap and reachable
/// only by following links from FAQ index pages. A same-domain crawl one level deep from such a
/// page also followed every header and footer link on the site.
/// </remarks>
public sealed class WebCrawlerScopeTests
{
    private const string Seed = "https://example.com/themes/health/faq";

    private static readonly Dictionary<string, string> s_site = new(StringComparer.Ordinal)
    {
        [Seed] = """
            <html><body>
              <a href="/">Home</a>
              <a href="/contact">Contact</a>
              <a href="/themes/health/faq/smoking">Is smoking bad?</a>
              <a href="/themes/health/faq/vaping">Where do I hand in a vape?</a>
              <a href="/themes/housing/rent">Rent</a>
            </body></html>
            """,
        ["https://example.com/"] = "<html><body>home</body></html>",
        ["https://example.com/contact"] = "<html><body>contact</body></html>",
        ["https://example.com/themes/health/faq/smoking"] = "<html><body>Yes.</body></html>",
        ["https://example.com/themes/health/faq/vaping"] = "<html><body>At a collection point.</body></html>",
        ["https://example.com/themes/housing/rent"] = "<html><body>rent</body></html>",
    };

    private static async Task<List<string>> UrlsAsync(WebCrawlerOptions options, FakeHttpMessageHandler? handler = null)
    {
        var entries = await new WebCrawlerDataProvider(Seed, new HttpClient(handler ?? new FakeHttpMessageHandler(s_site)), options)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        return entries.Select(e => e.Value.Id.Value).ToList();
    }

    [Fact]
    public async Task WithoutIncludedPrefixes_EverySameDomainLinkIsFollowed()
    {
        // The default stays what every existing caller gets.
        var urls = await UrlsAsync(new WebCrawlerOptions { MaxDepth = 1, RespectRobotsTxt = false });

        Assert.Equal(6, urls.Count);
    }

    [Fact]
    public async Task IncludedPrefixes_KeepTheCrawlBeneathThem()
    {
        var urls = await UrlsAsync(new WebCrawlerOptions
        {
            MaxDepth = 1,
            RespectRobotsTxt = false,
            IncludedUrlPrefixes = ["https://example.com/themes/health/faq"],
        });

        Assert.Equal(
            [Seed, "https://example.com/themes/health/faq/smoking", "https://example.com/themes/health/faq/vaping"],
            urls);
    }

    /// <remarks>
    /// Out-of-scope links are dropped before they are fetched, not fetched and then filtered —
    /// the point of scoping a crawl on a site you do not run.
    /// </remarks>
    [Fact]
    public async Task OutOfScopeLinks_AreNeverFetched()
    {
        var handler = new FakeHttpMessageHandler(s_site);

        _ = await UrlsAsync(
            new WebCrawlerOptions
            {
                MaxDepth = 1,
                RespectRobotsTxt = false,
                IncludedUrlPrefixes = ["https://example.com/themes/health/faq"],
            },
            handler);

        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task ASeedOutsideTheIncludedPrefixes_YieldsNothing()
    {
        var urls = await UrlsAsync(new WebCrawlerOptions
        {
            RespectRobotsTxt = false,
            IncludedUrlPrefixes = ["https://example.com/somewhere-else/"],
        });

        Assert.Empty(urls);
    }

    [Fact]
    public async Task IncludedPrefixes_AreMatchedCaseInsensitively_AndBlanksAreIgnored()
    {
        var urls = await UrlsAsync(new WebCrawlerOptions
        {
            MaxDepth = 1,
            RespectRobotsTxt = false,
            IncludedUrlPrefixes = ["", "HTTPS://EXAMPLE.COM/THEMES/HEALTH/FAQ"],
        });

        Assert.Equal(3, urls.Count);
    }

    [Fact]
    public async Task MinimumRequestInterval_SpacesOutFetches()
    {
        var interval = TimeSpan.FromMilliseconds(150);
        var stopwatch = Stopwatch.StartNew();

        var urls = await UrlsAsync(new WebCrawlerOptions
        {
            MaxDepth = 1,
            RespectRobotsTxt = false,
            IncludedUrlPrefixes = ["https://example.com/themes/health/faq"],
            MinimumRequestInterval = interval,
        });

        // Three fetches: the first starts at once, the next two each wait a full interval. A lower
        // bound only, so a slow machine cannot fail it.
        Assert.Equal(3, urls.Count);
        Assert.True(
            stopwatch.Elapsed >= interval * 2 - TimeSpan.FromMilliseconds(15),
            $"Three fetches took {stopwatch.Elapsed.TotalMilliseconds} ms; expected at least {interval.TotalMilliseconds * 2} ms.");
    }

    [Fact]
    public void ANegativeMinimumRequestInterval_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebCrawlerDataProvider(
            Seed,
            new HttpClient(new FakeHttpMessageHandler(s_site)),
            new WebCrawlerOptions { MinimumRequestInterval = TimeSpan.FromSeconds(-1) }));
    }
}
