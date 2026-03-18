using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

public sealed class SitemapDataProviderTests
{
    private static HttpClient MakeClient(Dictionary<string, string> responses)
    {
        var handler = new FakeHttpMessageHandler(responses);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task GetFilesAsync_ParsesUrlElements()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/page1</loc></url>
              <url><loc>https://example.com/page2</loc></url>
            </urlset>
            """;
        var client = MakeClient(new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_LastmodBecomesETag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://example.com/page1</loc>
                <lastmod>2024-01-15</lastmod>
              </url>
            </urlset>
            """;
        var client = MakeClient(new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2024-01-15", entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_SitemapIndex_RecursesIntoChildSitemaps()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/sitemap-index.xml"] = """
                <?xml version="1.0"?>
                <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <sitemap><loc>https://example.com/sitemap1.xml</loc></sitemap>
                  <sitemap><loc>https://example.com/sitemap2.xml</loc></sitemap>
                </sitemapindex>
                """,
            ["https://example.com/sitemap1.xml"] = """
                <?xml version="1.0"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.com/page1</loc></url>
                </urlset>
                """,
            ["https://example.com/sitemap2.xml"] = """
                <?xml version="1.0"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.com/page2</loc></url>
                </urlset>
                """,
        };
        var sut = new SitemapDataProvider("https://example.com/sitemap-index.xml", MakeClient(responses));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_InferredFileName_EndsWithHtml()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/docs/getting-started</loc></url>
            </urlset>
            """;
        var client = MakeClient(new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("getting-started.html", entries[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_OpenContentAsync_ReturnsSeekableStream()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/page</loc></url>
            </urlset>
            """;
        var client = MakeClient(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/sitemap.xml"] = xml,
            ["https://example.com/page"] = "<html>content</html>",
        });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await using var stream = await entries[0].OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.CanSeek);
    }
}
