using Rag.NET.DataProviders.Testing;
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
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/page1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/page2", StringComparison.Ordinal));
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

        Assert.Equal("2024-01-15", entries[0].Value.ETag);
    }

    /// <summary>
    /// Phase 4.10 Task 5: <c>&lt;lastmod&gt;</c> also becomes the typed
    /// <see cref="Rag.NET.DataProviders.FileEntry.UpdatedAt"/>. The <c>lastmod</c> metadata tag
    /// keeps carrying the raw string verbatim (asserted separately) — the typed field is an
    /// addition, not a replacement.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_Lastmod_IsTypedAsUpdatedAt()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://example.com/page1</loc>
                <lastmod>2024-01-15T10:00:00Z</lastmod>
              </url>
              <url>
                <loc>https://example.com/page2</loc>
              </url>
            </urlset>
            """;
        var client = MakeClient(new Dictionary<string, string>(StringComparer.Ordinal) { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            entries[0].Value.UpdatedAt);
        Assert.Equal("2024-01-15T10:00:00Z", entries[0].Value.Metadata!["lastmod"]);

        // No <lastmod> at all — stays unset, never guessed.
        Assert.Null(entries[1].Value.UpdatedAt);
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
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/page1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/page2", StringComparison.Ordinal));
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

        Assert.Equal("getting-started.html", entries[0].Value.FileName);
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

        await using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.CanSeek, "stream must be seekable for parent-document retrieval");
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsUrlAndLastmod()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://example.com/page1</loc>
                <lastmod>2024-01-15</lastmod>
              </url>
              <url><loc>https://example.com/page2</loc></url>
            </urlset>
            """;
        var client = MakeClient(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var entry in entries)
            MetadataContract.AssertValid(entry.Value.Metadata, entry.Value.Id.Value);

        var first = entries[0].Value.Metadata!;
        Assert.Equal("https://example.com/page1", first["url"]);
        Assert.Equal("2024-01-15", first["lastmod"]);
        Assert.Equal(2, first.Count);

        // <lastmod> is optional in the sitemap protocol — omitted, never written empty.
        var second = entries[1].Value.Metadata!;
        Assert.Equal("https://example.com/page2", second["url"]);
        Assert.False(second.ContainsKey("lastmod"));
        _ = Assert.Single(second);
    }
}
