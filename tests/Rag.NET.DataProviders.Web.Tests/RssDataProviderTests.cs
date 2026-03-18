using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

public sealed class RssDataProviderTests
{
    private static HttpClient MakeClient(string feedUrl, string feedXml)
        => new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal) { [feedUrl] = feedXml }));

    [Fact]
    public async Task GetFilesAsync_Rss2_ParsesItems()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                </item>
                <item>
                  <guid>https://example.com/post-2</guid>
                  <link>https://example.com/post-2</link>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss", MakeClient("https://example.com/feed.rss", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/post-1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/post-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_Rss2_PubDateBecomesETag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss", MakeClient("https://example.com/feed.rss", xml));
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Mon, 01 Jan 2024 00:00:00 GMT", entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_Atom_ParsesEntries()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <updated>2024-01-01T00:00:00Z</updated>
              </entry>
              <entry>
                <id>https://example.com/post-2</id>
                <link href="https://example.com/post-2"/>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml", MakeClient("https://example.com/atom.xml", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/post-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_Atom_UpdatedBecomesETag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <updated>2024-01-01T00:00:00Z</updated>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml", MakeClient("https://example.com/atom.xml", xml));
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2024-01-01T00:00:00Z", entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_Rss2_MissingGuid_FallsBackToLink()
    {
        // No <guid> element — Id must come from <link>
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <link>https://example.com/post-via-link</link>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss",
            MakeClient("https://example.com/feed.rss", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("https://example.com/post-via-link", entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_Atom_MissingId_FallsBackToLinkHref()
    {
        // No <id> element — Id must come from <link href>
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <link href="https://example.com/post-via-link"/>
                <updated>2024-01-01T00:00:00Z</updated>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml",
            MakeClient("https://example.com/atom.xml", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("https://example.com/post-via-link", entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_Rss2_OpenContentAsync_ReturnsSeekableStream()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                </item>
              </channel>
            </rss>
            """;
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/feed.rss"] = xml,
            ["https://example.com/post-1"] = "<html>content</html>",
        });
        var sut = new RssDataProvider("https://example.com/feed.rss", new HttpClient(handler));
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await using var stream = await entries[0].OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.CanSeek, "stream must be seekable for parent-document retrieval");
    }
}
