using Rag.NET.DataProviders.Web;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class WebCrawlerDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public WebCrawlerDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("WebCrawler");

        // Register the seed page with an embedded link so the crawler can follow it.
        // This is done programmatically because the link href must use the WireMock base URL.
        var baseUrl = _fixture.BaseUrl;
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/start")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html; charset=utf-8")
                .WithBody($"""
                    <html>
                    <head><title>Start Page</title></head>
                    <body>
                      <h1>Welcome</h1>
                      <p>This is the starting page.</p>
                      <a href="{baseUrl}/about">About</a>
                    </body>
                    </html>
                    """));

        _fixture.Server
            .Given(Request.Create()
                .WithPath("/about")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html; charset=utf-8")
                .WithBody("""
                    <html>
                    <head><title>About</title></head>
                    <body><h1>About Us</h1><p>Learn more about us here.</p></body>
                    </html>
                    """));
    }

    private HttpClient CreateHttpClient() =>
        new() { BaseAddress = new Uri(_fixture.BaseUrl) };

    [Fact]
    public async Task GetFilesAsync_CrawlsSeedPage()
    {
        using var httpClient = CreateHttpClient();
        var sut = new WebCrawlerDataProvider(
            $"{_fixture.BaseUrl}/start",
            httpClient,
            new WebCrawlerOptions { MaxPages = 10, MaxDepth = 2 });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Value.Id.Value.Contains("/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_EachEntryHasNonEmptyId()
    {
        using var httpClient = CreateHttpClient();
        var sut = new WebCrawlerDataProvider(
            $"{_fixture.BaseUrl}/start",
            httpClient,
            new WebCrawlerOptions { MaxPages = 10, MaxDepth = 2 });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e =>
        {
            Assert.NotEmpty(e.Value.Id.Value);
            Assert.NotEmpty(e.Value.FileName);
        });
    }

    [Fact]
    public async Task GetFilesAsync_OpenContent_ReturnsHtml()
    {
        using var httpClient = CreateHttpClient();
        var sut = new WebCrawlerDataProvider(
            $"{_fixture.BaseUrl}/start",
            httpClient,
            new WebCrawlerOptions { MaxPages = 1, MaxDepth = 0 });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        await using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task GetFilesAsync_MaxPages_LimitsResults()
    {
        using var httpClient = CreateHttpClient();
        var sut = new WebCrawlerDataProvider(
            $"{_fixture.BaseUrl}/start",
            httpClient,
            new WebCrawlerOptions { MaxPages = 1, MaxDepth = 5 });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
    }

    [Fact]
    public async Task GetFilesAsync_RespectRobotsTxt_DisallowsPrivatePaths()
    {
        var baseUrl = _fixture.BaseUrl;

        // Register robots.txt that disallows /private/ — registered here (not just via
        // cassette) to ensure the correct stub is active for this test, regardless of
        // WireMock stub ordering in the shared fixture.
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/robots.txt")
                .UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain; charset=utf-8")
                .WithBody("User-agent: *\nDisallow: /private/\n"));

        // Register a private page and verify it is blocked by robots.txt.
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/private/secret")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html; charset=utf-8")
                .WithBody("<html><body>SECRET</body></html>"));

        // Seed page with a link into /private/.
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/start-robots")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html; charset=utf-8")
                .WithBody($"""
                    <html><body>
                      <a href="{baseUrl}/private/secret">Secret</a>
                    </body></html>
                    """));

        using var httpClient = CreateHttpClient();
        var sut = new WebCrawlerDataProvider(
            $"{_fixture.BaseUrl}/start-robots",
            httpClient,
            new WebCrawlerOptions { MaxPages = 10, MaxDepth = 2, RespectRobotsTxt = true });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(entries, e => e.Value.Id.Value.Contains("/private/", StringComparison.Ordinal));
    }
}
