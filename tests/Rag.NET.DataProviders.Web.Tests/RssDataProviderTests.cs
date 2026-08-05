using Rag.NET.DataProviders.Testing;
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
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/post-1", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/post-2", StringComparison.Ordinal));
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

        Assert.Equal("Mon, 01 Jan 2024 00:00:00 GMT", entries[0].Value.ETag);
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
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "https://example.com/post-1", StringComparison.Ordinal));
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

        Assert.Equal("2024-01-01T00:00:00Z", entries[0].Value.ETag);
    }

    /// <summary>
    /// Phase 4.10 Task 5: RSS 2.0 has no <c>published</c>/<c>updated</c> split — only
    /// <c>pubDate</c> — so it becomes the typed <see cref="Rag.NET.DataProviders.FileEntry.CreatedAt"/>
    /// and <c>UpdatedAt</c> stays unset rather than fabricated.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_Rss2_PubDate_IsTypedAsCreatedAtOnly()
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

        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), entries[0].Value.CreatedAt);
        Assert.Null(entries[0].Value.UpdatedAt);
    }

    /// <summary>
    /// Phase 4.10 Task 5: Atom's <c>&lt;published&gt;</c>/<c>&lt;updated&gt;</c> become the typed
    /// <see cref="Rag.NET.DataProviders.FileEntry.CreatedAt"/>/
    /// <see cref="Rag.NET.DataProviders.FileEntry.UpdatedAt"/> — both, unlike RSS 2.0.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_Atom_PublishedAndUpdated_AreTypedAsCreatedAndUpdatedAt()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <published>2024-01-01T00:00:00Z</published>
                <updated>2024-02-02T00:00:00Z</updated>
              </entry>
              <entry>
                <id>https://example.com/post-2</id>
                <link href="https://example.com/post-2"/>
                <updated>2024-03-03T00:00:00Z</updated>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml", MakeClient("https://example.com/atom.xml", xml));
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), entries[0].Value.CreatedAt);
        Assert.Equal(new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc), entries[0].Value.UpdatedAt);

        // No <published> at all — CreatedAt stays unset rather than falling back to <updated>
        // (unlike the published_at metadata tag, which does fall back).
        Assert.Null(entries[1].Value.CreatedAt);
        Assert.Equal(new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc), entries[1].Value.UpdatedAt);
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

        _ = Assert.Single(entries);
        Assert.Equal("https://example.com/post-via-link", entries[0].Value.Id);
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

        _ = Assert.Single(entries);
        Assert.Equal("https://example.com/post-via-link", entries[0].Value.Id);
    }

    [Fact]
    public async Task GetFilesAsync_Atom_OpenContentAsync_ReturnsSeekableStream()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/atom-post-1</id>
                <link href="https://example.com/atom-post-1"/>
              </entry>
            </feed>
            """;
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.com/feed.atom"] = xml,
            ["https://example.com/atom-post-1"] = "<html>atom content</html>",
        });
        var sut = new RssDataProvider("https://example.com/feed.atom", new HttpClient(handler));
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.CanSeek, "stream must be seekable for parent-document retrieval");
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

        await using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.CanSeek, "stream must be seekable for parent-document retrieval");
    }

    [Fact]
    public async Task GetFilesAsync_Rss2_Metadata_PinsUrlPublishedAtAndAuthor()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                  <author>alice@example.com (Alice)</author>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                </item>
                <item>
                  <guid>https://example.com/post-2</guid>
                  <link>https://example.com/post-2</link>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss",
            MakeClient("https://example.com/feed.rss", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var entry in entries)
            MetadataContract.AssertValid(entry.Value.Metadata, entry.Value.Id.Value);

        var first = entries[0].Value.Metadata!;
        Assert.Equal("https://example.com/post-1", first["url"]);
        // RSS carries RFC 822; the tag is normalised to round-trip ISO-8601 so it is comparable
        // with the Atom branch's values under the same key. The ETag keeps the raw string.
        Assert.Equal("2024-01-01T00:00:00.0000000+00:00", first["published_at"]);
        Assert.Equal("Mon, 01 Jan 2024 00:00:00 GMT",     entries[0].Value.ETag);
        Assert.Equal("alice@example.com (Alice)",         first["author"]);
        Assert.Equal(3, first.Count);

        // Both optional elements absent → omitted, never written empty.
        var second = entries[1].Value.Metadata!;
        Assert.Equal("https://example.com/post-2", second["url"]);
        Assert.False(second.ContainsKey("published_at"));
        Assert.False(second.ContainsKey("author"));
        _ = Assert.Single(second);
    }

    [Fact]
    public async Task GetFilesAsync_Atom_Metadata_PinsUrlPublishedAtAndAuthor()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <author><name>Alice</name></author>
                <published>2024-01-01T00:00:00Z</published>
                <updated>2024-02-02T00:00:00Z</updated>
              </entry>
              <entry>
                <id>https://example.com/post-2</id>
                <link href="https://example.com/post-2"/>
                <updated>2024-03-03T00:00:00Z</updated>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml",
            MakeClient("https://example.com/atom.xml", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var entry in entries)
            MetadataContract.AssertValid(entry.Value.Metadata, entry.Value.Id.Value);

        var first = entries[0].Value.Metadata!;
        Assert.Equal("https://example.com/post-1", first["url"]);
        // <published> wins over <updated> when both are present.
        Assert.Equal("2024-01-01T00:00:00.0000000+00:00", first["published_at"]);
        Assert.Equal("Alice",                             first["author"]);

        // No <published> → falls back to <updated>; no <author> → omitted.
        var second = entries[1].Value.Metadata!;
        Assert.Equal("2024-03-03T00:00:00.0000000+00:00", second["published_at"]);
        Assert.False(second.ContainsKey("author"));
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task GetFilesAsync_PublishedAt_IsComparableAcrossFeedShapes()
    {
        // The whole point of normalising: an RFC 822 pubDate and an ISO-8601 <published> naming
        // the same instant must produce the same tag value, or ordering and range filters on
        // published_at are meaningless within this one provider.
        const string rssXml = """
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
        const string atomXml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <published>2024-01-01T00:00:00Z</published>
              </entry>
            </feed>
            """;

        var rss = new RssDataProvider("https://example.com/feed.rss",
            MakeClient("https://example.com/feed.rss", rssXml));
        var atom = new RssDataProvider("https://example.com/atom.xml",
            MakeClient("https://example.com/atom.xml", atomXml));

        var rssEntries = await rss.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var atomEntries = await atom.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            atomEntries[0].Value.Metadata!["published_at"],
            rssEntries[0].Value.Metadata!["published_at"]);
    }

    [Fact]
    public async Task GetFilesAsync_UnparseablePubDate_PassesThroughUnchanged()
    {
        // A non-conforming feed is still better described by its own string than by nothing.
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                  <pubDate>sometime last Tuesday</pubDate>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss",
            MakeClient("https://example.com/feed.rss", xml));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var entry in entries)
            MetadataContract.AssertValid(entry.Value.Metadata, entry.Value.Id.Value);

        Assert.Equal("sometime last Tuesday", entries[0].Value.Metadata!["published_at"]);
    }
}
