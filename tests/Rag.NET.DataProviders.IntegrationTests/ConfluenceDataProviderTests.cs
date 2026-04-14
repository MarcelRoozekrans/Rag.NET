using System.Net.Http.Headers;
using Rag.NET.DataProviders.Confluence;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class ConfluenceDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public ConfluenceDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Confluence", "https://test.atlassian.net");
    }

    private ConfluenceDataProvider CreateProvider(ConfluenceOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        var api = new ConfluenceApiClient(http, JsonSerializer);
        return new ConfluenceDataProvider(api, opts ?? new ConfluenceOptions
        {
            BaseUrl = _fixture.BaseUrl,
            Email   = "test@test.com",
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsPages()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.FileName);
            Assert.NotEmpty(r.Value.Id.Value);
        });
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "Getting Started.md", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "API Reference.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        var sut = CreateProvider();
        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // Every request to the Confluence API must carry an Accept: application/json header.
        Assert.All(logEntries, entry =>
        {
            var headers = entry.RequestMessage.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Accept"), "Accept header missing");
            Assert.Contains("application/json", headers["Accept"]);
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_EachFileHasETag()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.ETag!);
        });

        // ETags are version numbers from the cassette (3 and 7).
        Assert.Contains(results, r => string.Equals(r.Value.ETag, "3", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Value.ETag, "7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesCqlSearchEndpoint()
    {
        // Register a programmatic stub for the search endpoint.
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/wiki/rest/api/content/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody("""
                    {
                      "results": [
                        { "id": "999", "title": "Delta Page", "body": { "storage": { "value": "<p>Changed</p>" } }, "version": { "number": 2 } }
                      ],
                      "_links": {}
                    }
                    """));

        _fixture.Server.ResetLogEntries();

        var opts = new ConfluenceOptions
        {
            BaseUrl    = _fixture.BaseUrl,
            Email      = "test@test.com",
            DeltaToken = "2026-01-01T00:00:00Z",
        };
        var sut = CreateProvider(opts);

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsSuccess);
        Assert.Equal("Delta Page.md", results[0].Value.FileName);

        // Verify the /search endpoint was actually called.
        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.Contains(logEntries, entry =>
            entry.RequestMessage.AbsolutePath.Contains("/wiki/rest/api/content/search",
                StringComparison.Ordinal));
    }
}
