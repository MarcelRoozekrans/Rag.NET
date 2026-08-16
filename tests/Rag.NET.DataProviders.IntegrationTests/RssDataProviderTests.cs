using Rag.NET.DataProviders.Web;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

/// <summary>
/// Drives <see cref="RssDataProvider"/> against a <b>real HTTP server</b> — both feed shapes it
/// claims to support, and the content fetch that follows a feed entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.1.</b> The allowlist entry for <c>Rag.NET.DataProviders.Web</c> reads
/// "live HTTP crawl, sitemap and RSS (a local server is a 6.2 option; decide in 6.1)". The decision
/// is that the local server is enough, and for sitemap and crawler it already existed — both have
/// WireMock suites here. <b>RSS did not.</b> Its only coverage was
/// <c>Rag.NET.DataProviders.Web.Tests</c>, which drives a <c>FakeHttpMessageHandler</c>.
/// </para>
/// <para>
/// <b>The distinction that makes this worth writing.</b> A fake <see cref="HttpMessageHandler"/>
/// replaces the transport: no socket is opened, no HTTP is parsed, no header or status code is
/// produced by anything but the test. WireMock is a real HTTP server on a real port — it is not a
/// substitute for the provider, it is a real counterparty to it. The provider's own HTTP stack runs.
/// That is the line §2 of the 6.2 design draws, and RSS was on the wrong side of it.
/// </para>
/// <para>
/// Both branches are covered because <see cref="RssDataProvider"/> has two, chosen on the root
/// element name: Atom (<c>&lt;feed&gt;</c>) and RSS 2.0 (anything else). They map different
/// elements and set different timestamps — Atom fills both <c>CreatedAt</c> and <c>UpdatedAt</c>,
/// RSS 2.0 has only <c>pubDate</c> and deliberately leaves <c>UpdatedAt</c> unset rather than
/// fabricating it. A test of one branch says nothing about the other.
/// </para>
/// </remarks>
[Collection("WireMock")]
public sealed class RssDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public RssDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        var baseUrl = _fixture.BaseUrl;

        // Atom. Links are absolute and point back at this server, so the content fetch below
        // resolves against something real rather than a rewritten URL.
        _fixture.Server
            .Given(Request.Create().WithPath("/atom.xml").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/atom+xml; charset=utf-8")
                .WithBody($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <feed xmlns="http://www.w3.org/2005/Atom">
                      <title>Integration Test Feed</title>
                      <entry>
                        <id>{baseUrl}/atom/first</id>
                        <title>First Atom Entry</title>
                        <link href="{baseUrl}/atom/first"/>
                        <published>2024-01-15T09:00:00Z</published>
                        <updated>2024-02-20T11:30:00Z</updated>
                      </entry>
                      <entry>
                        <id>{baseUrl}/atom/second</id>
                        <title>Second Atom Entry</title>
                        <link href="{baseUrl}/atom/second"/>
                        <published>2024-03-01T08:00:00Z</published>
                        <updated>2024-03-02T08:00:00Z</updated>
                      </entry>
                    </feed>
                    """));

        // RSS 2.0 — a different root element, so a different branch of the provider.
        _fixture.Server
            .Given(Request.Create().WithPath("/rss.xml").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/rss+xml; charset=utf-8")
                .WithBody($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <rss version="2.0">
                      <channel>
                        <title>Integration Test Channel</title>
                        <item>
                          <guid>{baseUrl}/rss/first</guid>
                          <title>First RSS Item</title>
                          <link>{baseUrl}/rss/first</link>
                          <pubDate>Mon, 15 Jan 2024 09:00:00 GMT</pubDate>
                        </item>
                      </channel>
                    </rss>
                    """));

        _fixture.Server
            .Given(Request.Create().WithPath("/atom/first").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html; charset=utf-8")
                .WithBody("<html><body><h1>Integration Test Document</h1></body></html>"));
    }

    private HttpClient CreateHttpClient() => new() { BaseAddress = new Uri(_fixture.BaseUrl) };

    [Fact]
    public async Task AnAtomFeedOverRealHttp_YieldsEveryEntry()
    {
        using var httpClient = CreateHttpClient();
        var sut = new RssDataProvider($"{_fixture.BaseUrl}/atom.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
        Assert.All(entries, e => Assert.NotEmpty(e.Value.Id.Value));
    }

    /// <remarks>
    /// Atom carries both <c>published</c> and <c>updated</c>, and the provider maps them to
    /// different fields. Asserting only that entries came back would not notice the two being
    /// swapped, or one being dropped.
    /// </remarks>
    [Fact]
    public async Task AnAtomEntry_CarriesBothItsTimestamps()
    {
        using var httpClient = CreateHttpClient();
        var sut = new RssDataProvider($"{_fixture.BaseUrl}/atom.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var first = entries[0].Value;
        Assert.NotNull(first.CreatedAt);
        Assert.NotNull(first.UpdatedAt);
        Assert.NotEqual(first.CreatedAt, first.UpdatedAt);
        // ETag is the Atom <updated> value, passed through verbatim.
        Assert.Equal("2024-02-20T11:30:00Z", first.ETag);
    }

    /// <remarks>
    /// The RSS 2.0 branch, and the deliberate asymmetry the source documents: RSS has only
    /// <c>pubDate</c>, so <c>UpdatedAt</c> stays unset rather than being fabricated from it. A
    /// change that "helpfully" filled it in would pass every Atom test.
    /// </remarks>
    [Fact]
    public async Task AnRss20Feed_SetsCreatedAtButLeavesUpdatedAtUnset()
    {
        using var httpClient = CreateHttpClient();
        var sut = new RssDataProvider($"{_fixture.BaseUrl}/rss.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(entries).Value;
        Assert.NotNull(only.CreatedAt);
        Assert.Null(only.UpdatedAt);
        Assert.Equal("Mon, 15 Jan 2024 09:00:00 GMT", only.ETag);
    }

    /// <remarks>
    /// The feed is only half the provider's job: each entry carries a deferred fetch, and that
    /// second request is a separate real round trip. A provider that enumerated a feed correctly
    /// and could not download an entry would pass every assertion above.
    /// </remarks>
    [Fact]
    public async Task FollowingAnEntry_FetchesItsContentOverRealHttp()
    {
        using var httpClient = CreateHttpClient();
        var sut = new RssDataProvider($"{_fixture.BaseUrl}/atom.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await using var content = await entries[0].Value
            .OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(content);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Integration Test Document", body, StringComparison.Ordinal);
    }
}
