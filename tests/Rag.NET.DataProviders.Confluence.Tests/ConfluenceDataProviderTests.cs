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

    [Fact]
    public async Task GetFilesAsync_Pagination_FetchesAllPages()
    {
        const string page1Json = """
            {
              "results": [
                { "id": "1", "title": "PageOne", "body": { "storage": { "value": "a" } }, "version": { "number": 1 } }
              ],
              "_links": { "next": "/wiki/rest/api/content?cursor=abc123&limit=50" }
            }
            """;
        const string page2Json = """
            {
              "results": [
                { "id": "2", "title": "PageTwo", "body": { "storage": { "value": "b" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var handler = new FakeSequentialHandler(page1Json, page2Json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        var sut = new ConfluenceDataProvider(api, new ConfluenceOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("PageOne.md", results[0].FileName);
        Assert.Equal("PageTwo.md", results[1].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_HtmlBody_IsStrippedToMarkdown()
    {
        const string json = """
            {
              "results": [
                { "id": "1", "title": "Doc", "body": { "storage": { "value": "<p>Hello &amp; World</p>" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var content = await ReadContentAsync(results[0]);
        Assert.Contains("Hello & World", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_InvalidDeltaToken_Throws()
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);

        Assert.Throws<ArgumentException>(() =>
            new ConfluenceDataProvider(api, new ConfluenceOptions
            {
                BaseUrl    = "https://test.atlassian.net",
                Email      = "t@t.com",
                DeltaToken = "DROP TABLE; --"
            }));
    }

    [Fact]
    public void Constructor_InvalidSpaceKey_Throws()
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);

        Assert.Throws<ArgumentException>(() =>
            new ConfluenceDataProvider(api, new ConfluenceOptions
            {
                BaseUrl  = "https://test.atlassian.net",
                Email    = "t@t.com",
                SpaceKey = "foo bar\""
            }));
    }

    [Fact]
    public async Task GetFilesAsync_StaleDeltaToken_FallsBackToFullTraversal()
    {
        const string fullJson = """
            {
              "results": [
                { "id": "1", "title": "Fallback", "body": { "storage": { "value": "ok" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var handler = new FakeStaleDeltaHandler(fullJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        var sut = new ConfluenceDataProvider(api, new ConfluenceOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            DeltaToken = "2026-01-01T00:00:00Z"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Fallback.md", results[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyFirstPage_YieldsNothing()
    {
        const string json = """
            {
              "results": [],
              "_links": {}
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_CursorWithAmpersand_ExtractsCorrectly()
    {
        const string page1Json = """
            {
              "results": [
                { "id": "1", "title": "First", "body": { "storage": { "value": "a" } }, "version": { "number": 1 } }
              ],
              "_links": { "next": "/wiki/rest/api/content?cursor=abc123&limit=50" }
            }
            """;
        const string page2Json = """
            {
              "results": [
                { "id": "2", "title": "Second", "body": { "storage": { "value": "b" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var handler = new FakeSequentialHandler(page1Json, page2Json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        var sut = new ConfluenceDataProvider(api, new ConfluenceOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("First.md", results[0].FileName);
        Assert.Equal("Second.md", results[1].FileName);
        // Verify cursor=abc123 was extracted (stopped at &) by confirming the second page was fetched
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaWithSpaceKey_CqlContainsBoth()
    {
        const string json = """
            {
              "results": [
                { "id": "1", "title": "Doc", "body": { "storage": { "value": "x" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var handler = new FakeCapturingHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        var sut = new ConfluenceDataProvider(api, new ConfluenceOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            SpaceKey   = "DEV",
            DeltaToken = "2026-01-01T00:00:00Z"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        var url = handler.CapturedUrls[0];
        // The CQL query should contain both space= and lastModified>
        Assert.Contains("space", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lastModified", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEV", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        // Use a handler that returns multiple pages so we can cancel mid-enumeration.
        const string page1Json = """
            {
              "results": [
                { "id": "1", "title": "One", "body": { "storage": { "value": "a" } }, "version": { "number": 1 } },
                { "id": "2", "title": "Two", "body": { "storage": { "value": "b" } }, "version": { "number": 1 } }
              ],
              "_links": { "next": "/wiki/rest/api/content?cursor=next1&limit=50" }
            }
            """;
        const string page2Json = """
            {
              "results": [
                { "id": "3", "title": "Three", "body": { "storage": { "value": "c" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var handler = new FakeSequentialHandler(page1Json, page2Json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        var sut = new ConfluenceDataProvider(api, new ConfluenceOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });

        using var cts = new CancellationTokenSource();
        var count = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in sut.GetFilesAsync(cts.Token))
            {
                count++;
                if (count == 1)
                    cts.Cancel(); // cancel after first item
            }
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetFilesAsync_ContentReadable_MarkdownCorrect()
    {
        const string json = """
            {
              "results": [
                { "id": "1", "title": "My Title", "body": { "storage": { "value": "<h1>Heading</h1><p>Some body text &amp; more</p>" } }, "version": { "number": 2 } }
              ],
              "_links": {}
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var content = await ReadContentAsync(results[0]);
        Assert.StartsWith("# My Title", content, StringComparison.Ordinal);
        Assert.Contains("Some body text & more", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", content, StringComparison.Ordinal);
    }

    private static async Task<string> ReadContentAsync(Rag.NET.DataProviders.FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handlers
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

/// <summary>
/// Returns responses in order for each successive request, regardless of URL.
/// Exposes <see cref="RequestCount"/> so tests can assert the number of round-trips.
/// </summary>
file sealed class FakeSequentialHandler(params string[] responses) : HttpMessageHandler
{
    private int _index;
    public int RequestCount => _index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = _index < responses.Length ? responses[_index++] : "{}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Returns HTTP 400 for search endpoint requests (simulating stale delta token),
/// and valid JSON for content endpoint requests (full traversal fallback).
/// </summary>
file sealed class FakeStaleDeltaHandler(string fullTraversalJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("/search", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content        = new StringContent("{\"message\":\"Bad CQL\"}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content        = new StringContent(fullTraversalJson, Encoding.UTF8, "application/json"),
            RequestMessage = request
        });
    }
}

/// <summary>
/// Returns OK with canned JSON for all requests and captures request URLs for assertions.
/// </summary>
file sealed class FakeCapturingHandler(string responseJson) : HttpMessageHandler
{
    private readonly List<string> _urls = [];
    public IReadOnlyList<string> CapturedUrls => _urls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _urls.Add(request.RequestUri!.ToString());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
    }
}
