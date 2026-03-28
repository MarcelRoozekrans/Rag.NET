using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Confluence;
using Refit;
using Xunit;

namespace Rag.NET.DataProviders.Confluence.Tests;

public sealed class ConfluenceDataProviderTests
{
    private static ConfluenceDataProvider MakeProvider(
        string responseJson,
        ConfluenceOptions? options = null,
        string urlKey = "/wiki/rest/api/content")
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { [urlKey] = responseJson });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        return new ConfluenceDataProvider(api, options ?? new ConfluenceOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsPages()
    {
        const string json = """
            {
              "results": [
                { "id": "123", "title": "Guide", "body": { "storage": { "value": "<p>Hello</p>" } }, "version": { "number": 3 } },
                { "id": "456", "title": "FAQ",   "body": { "storage": { "value": "<p>World</p>" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("Guide.md", results[0].FileName);
        Assert.Equal("3", results[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_UsesSearchEndpoint()
    {
        const string json = """
            {
              "results": [
                { "id": "789", "title": "Updated", "body": { "storage": { "value": "<p>New</p>" } }, "version": { "number": 5 } }
              ],
              "_links": {}
            }
            """;
        var opts = new ConfluenceOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            DeltaToken = "2026-01-01T00:00:00Z"
        };
        var sut = MakeProvider(json, opts, urlKey: "/wiki/rest/api/content/search");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Updated.md", results[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        // Extension filter applies to FileName (.md) — set non-md extension to exclude all
        const string json = """
            {
              "results": [
                { "id": "1", "title": "Doc", "body": { "storage": { "value": "x" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var opts = new ConfluenceOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            Extensions = [".txt"]  // all pages are .md — nothing should match
        };
        var sut = MakeProvider(json, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ConfluenceDataProvider(null!, new ConfluenceOptions
            {
                BaseUrl = "https://test.atlassian.net",
                Email   = "t@t.com"
            }));
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handler
// ---------------------------------------------------------------------------

/// <summary>
/// Returns canned JSON responses keyed by URL substring, so tests never hit the network.
/// </summary>
file sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var key = responses.Keys.FirstOrDefault(k => url.Contains(k,
            StringComparison.Ordinal));
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
        });
    }
}
