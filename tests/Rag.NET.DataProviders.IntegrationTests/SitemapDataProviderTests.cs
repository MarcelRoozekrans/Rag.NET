using Rag.NET.DataProviders.Web;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class SitemapDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public SitemapDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Sitemap");

        // The sitemap XML must contain absolute URLs pointing at the WireMock server,
        // so we register this stub programmatically after the base URL is known.
        var baseUrl = _fixture.BaseUrl;
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/sitemap.xml")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/xml; charset=utf-8")
                .WithBody($"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                      <url>
                        <loc>{baseUrl}/page1</loc>
                        <lastmod>2024-01-15</lastmod>
                      </url>
                      <url>
                        <loc>{baseUrl}/page2</loc>
                        <lastmod>2024-02-20</lastmod>
                      </url>
                    </urlset>
                    """));
    }

    private HttpClient CreateHttpClient() =>
        new() { BaseAddress = new Uri(_fixture.BaseUrl) };

    [Fact]
    public async Task GetFilesAsync_ReturnsAllSitemapUrls()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider($"{_fixture.BaseUrl}/sitemap.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task GetFilesAsync_EachEntryHasNonEmptyId()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider($"{_fixture.BaseUrl}/sitemap.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e => Assert.NotEmpty(e.Value.Id.Value));
    }

    [Fact]
    public async Task GetFilesAsync_LastmodBecomesETag()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider($"{_fixture.BaseUrl}/sitemap.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e => Assert.NotEmpty(e.Value.ETag!));
        Assert.Contains(entries, e => string.Equals(e.Value.ETag, "2024-01-15", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_OpenContent_ReturnsPageHtml()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider($"{_fixture.BaseUrl}/sitemap.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var first = entries.First(e => e.Value.Id.Value.EndsWith("/page1", StringComparison.Ordinal));
        await using var stream = await first.Value.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.Length > 0);
    }

    /// <remarks>
    /// Issue #252 over a real HTTP server. The fast-tier suite
    /// (<c>Rag.NET.DataProviders.Web.Tests/SitemapFilteringTests</c>) covers the filter's
    /// semantics exhaustively; this asserts the feature survives the real transport, which is the
    /// bar this package is held to since Phase 6.1.
    /// </remarks>
    [Fact]
    public async Task ExcludedPrefixes_AreSkippedOverRealHttp()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider(
            $"{_fixture.BaseUrl}/sitemap.xml",
            httpClient,
            new SitemapOptions { ExcludedUrlPrefixes = [$"{_fixture.BaseUrl}/page1"] });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(entries).Value;
        Assert.Equal($"{_fixture.BaseUrl}/page2", only.Id.Value);
    }

    /// <remarks>The pattern mechanism, likewise over the real transport.</remarks>
    [Fact]
    public async Task ExcludedPatterns_AreSkippedOverRealHttp()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider(
            $"{_fixture.BaseUrl}/sitemap.xml",
            httpClient,
            new SitemapOptions { ExcludedUrlPatterns = [@"/page\d$"] });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }
}
