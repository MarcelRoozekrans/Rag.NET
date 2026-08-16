using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// Covers <see cref="SitemapOptions"/>' filtering semantics: which URLs are excluded, and the two
/// decisions that were open when issue #252 was scheduled.
/// </summary>
/// <remarks>
/// Fast tier, over <c>FakeHttpMessageHandler</c>, because these assertions are about
/// <i>which URLs survive a filter</i> — a question about the option's semantics, not about HTTP.
/// The real-server half lives in <c>Rag.NET.DataProviders.IntegrationTests</c>, which drives the
/// same provider over WireMock; neither replaces the other.
/// </remarks>
public sealed class SitemapFilteringTests
{
    private const string Sitemap = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>https://example.com/index.html</loc></url>
          <url><loc>https://example.com/blog/first</loc></url>
          <url><loc>https://example.com/blog/second</loc></url>
          <url><loc>https://example.com/docs/guide</loc></url>
          <url><loc>https://example.com/tags/csharp</loc></url>
        </urlset>
        """;

    private const string SitemapUrl = "https://example.com/sitemap.xml";
    private const string NestedUrl = "https://example.com/sitemap-blog.xml";

    private static SitemapDataProvider Provider(string xml, SitemapOptions? options) =>
        new(SitemapUrl,
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SitemapUrl] = xml,
            })),
            options);

    private static async Task<List<string>> UrlsAsync(SitemapOptions? options, string xml = Sitemap)
    {
        var entries = await Provider(xml, options)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        return entries.Select(e => e.Value.Id.Value).ToList();
    }

    [Fact]
    public async Task WithNoOptions_NothingIsExcluded()
    {
        // The default must stay what every existing caller already gets: this feature is additive.
        Assert.Equal(5, (await UrlsAsync(null)).Count);
        Assert.Equal(5, (await UrlsAsync(new SitemapOptions())).Count);
    }

    [Fact]
    public async Task APrefix_ExcludesEveryUrlBeneathIt()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            ExcludedUrlPrefixes = ["https://example.com/blog/"],
        });

        Assert.Equal(3, urls.Count);
        Assert.DoesNotContain("https://example.com/blog/first", urls, StringComparer.Ordinal);
        Assert.DoesNotContain("https://example.com/blog/second", urls, StringComparer.Ordinal);
        Assert.Contains("https://example.com/docs/guide", urls, StringComparer.Ordinal);
    }

    /// <remarks>
    /// Prefixes match the full URL as published, not a path fragment. A caller who writes
    /// <c>/blog/</c> expecting a path match gets nothing excluded — surprising once, and far less
    /// surprising than a fragment match that also hits <c>https://example.com/archive/blog/</c>.
    /// </remarks>
    [Fact]
    public async Task APrefixIsAPrefixOfTheWholeUrl_NotAPathFragment()
    {
        var urls = await UrlsAsync(new SitemapOptions { ExcludedUrlPrefixes = ["/blog/"] });

        Assert.Equal(5, urls.Count);
    }

    [Fact]
    public async Task APrefixIsMatchedCaseInsensitively()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            ExcludedUrlPrefixes = ["HTTPS://EXAMPLE.COM/BLOG/"],
        });

        Assert.Equal(3, urls.Count);
    }

    [Fact]
    public async Task APattern_ExcludesEveryUrlItMatches()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            ExcludedUrlPatterns = [@"/(tags|blog)/"],
        });

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://example.com/index.html", urls, StringComparer.Ordinal);
        Assert.Contains("https://example.com/docs/guide", urls, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrefixesAndPatterns_BothApply()
    {
        var urls = await UrlsAsync(new SitemapOptions
        {
            ExcludedUrlPrefixes = ["https://example.com/blog/"],
            ExcludedUrlPatterns = [@"/tags/"],
        });

        Assert.Equal(2, urls.Count);
    }

    /// <remarks>
    /// A blank prefix is a prefix of every string. Honouring it would silently return an empty
    /// sitemap — a mistake with no symptom, which is the one kind this provider should refuse.
    /// </remarks>
    [Fact]
    public async Task ABlankPrefix_IsIgnoredRatherThanExcludingEverything()
    {
        var urls = await UrlsAsync(new SitemapOptions { ExcludedUrlPrefixes = ["", "   "] });

        Assert.Equal(5, urls.Count);
    }

    // ── The nested-index decision ────────────────────────────────────────────

    private const string Index = """
        <?xml version="1.0" encoding="UTF-8"?>
        <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <sitemap><loc>https://example.com/sitemap-blog.xml</loc></sitemap>
        </sitemapindex>
        """;

    private static FakeHttpMessageHandler IndexHandler() =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SitemapUrl] = Index,
            [NestedUrl] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.com/blog/first</loc></url>
                </urlset>
                """,
        });

    /// <remarks>
    /// The default. Excluding an index link prunes everything under it <b>without fetching it</b>,
    /// which is the point: a site that partitions its index by section lets one prefix skip the
    /// whole section for one avoided request.
    /// </remarks>
    [Fact]
    public async Task ByDefault_AnExcludedIndexLinkIsNotFollowed()
    {
        var handler = IndexHandler();
        var sut = new SitemapDataProvider(
            SitemapUrl,
            new HttpClient(handler),
            new SitemapOptions { ExcludedUrlPrefixes = [NestedUrl] });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
        // One request — the index itself. The nested sitemap was never fetched, which is the
        // saving this option exists for.
        Assert.Equal(1, handler.RequestCount);
    }

    /// <remarks>
    /// The opt-out, for an index partitioned by something unrelated to the URLs inside it — by
    /// date, or by shard — where an index link matching a prefix says nothing about its pages.
    /// </remarks>
    [Fact]
    public async Task WithExcludeNestedSitemapsOff_TheIndexLinkIsFollowedAnyway()
    {
        var handler = IndexHandler();
        var sut = new SitemapDataProvider(
            SitemapUrl,
            new HttpClient(handler),
            new SitemapOptions
            {
                ExcludedUrlPrefixes = [NestedUrl],
                ExcludeNestedSitemaps = false,
            });

        _ = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Two requests: the index, then the nested sitemap it was told not to prune.
        Assert.Equal(2, handler.RequestCount);
    }
}
