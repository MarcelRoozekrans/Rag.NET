using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

public sealed class WebCrawlerDataProviderTests
{
    private const string SeedUrl = "https://example.com/";

    // Minimal 3-page site: index links to page1 and page2; page1 links back to index; page2 is a leaf
    private static readonly Dictionary<string, string> s_site = new(StringComparer.Ordinal)
    {
        [SeedUrl] = """
            <html><body>
              <a href="/page1">Page 1</a>
              <a href="/page2">Page 2</a>
            </body></html>
            """,
        ["https://example.com/page1"] = """
            <html><body>
              <a href="/">Back home</a>
              <p>Page one content</p>
            </body></html>
            """,
        ["https://example.com/page2"] = "<html><body><p>Page two content</p></body></html>",
    };

    private static HttpClient MakeClient(Dictionary<string, string>? responses = null)
        => new HttpClient(new FakeHttpMessageHandler(responses ?? s_site));

    [Fact]
    public async Task GetFilesAsync_BfsDiscoversPagesFromSeed()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(entries, e => string.Equals(e.Id, SeedUrl, StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_MaxPages_LimitsResults()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { MaxPages = 2, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task GetFilesAsync_MaxDepth_StopsFollowingLinksAtDepth()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { MaxDepth = 0, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Depth 0 → only the seed page; links are not followed
        Assert.Single(entries);
        Assert.Equal(SeedUrl, entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_SameDomain_ExcludesExternalLinks()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SeedUrl] = """
                <html><body>
                  <a href="/internal">Internal</a>
                  <a href="https://other.com/page">External</a>
                </body></html>
                """,
            ["https://example.com/internal"] = "<html><body>Internal page</body></html>",
        };
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(responses),
            new WebCrawlerOptions { SameDomain = true, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(entries, e => e.Id.StartsWith("https://other.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_RobotsTxt_DisallowedPathSkipped()
    {
        var responses = new Dictionary<string, string>(s_site, StringComparer.Ordinal)
        {
            ["https://example.com/robots.txt"] = """
                User-agent: *
                Disallow: /page2
                """,
        };
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(responses),
            new WebCrawlerOptions { RespectRobotsTxt = true });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(entries, e => string.Equals(e.Id, "https://example.com/page2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_OpenContentAsync_ReturnsPageHtml()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { MaxDepth = 0, RespectRobotsTxt = false });
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        await using var stream = await entries[0].OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Page 1", content, StringComparison.Ordinal);
    }
}
