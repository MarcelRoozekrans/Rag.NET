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

        Assert.All(entries, e => Assert.NotEmpty(e.Id));
    }

    [Fact]
    public async Task GetFilesAsync_LastmodBecomesETag()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider($"{_fixture.BaseUrl}/sitemap.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e => Assert.NotEmpty(e.ETag!));
        Assert.Contains(entries, e => string.Equals(e.ETag, "2024-01-15", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_OpenContent_ReturnsPageHtml()
    {
        using var httpClient = CreateHttpClient();
        var sut = new SitemapDataProvider($"{_fixture.BaseUrl}/sitemap.xml", httpClient);

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var first = entries.First(e => e.Id.EndsWith("/page1", StringComparison.Ordinal));
        await using var stream = await first.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.Length > 0);
    }
}
