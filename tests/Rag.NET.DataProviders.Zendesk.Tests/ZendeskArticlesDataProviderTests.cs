using System.Net;
using System.Text;
using Rag.NET.DataProviders.Zendesk;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.Zendesk.Tests;

public sealed class ZendeskArticlesDataProviderTests
{
    private static readonly IRestSerializer JsonSerializer = new SystemTextJsonSerializer();

    private static ZendeskArticlesDataProvider MakeProvider(
        HttpMessageHandler handler,
        ZendeskArticlesOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.zendesk.com") };
        var api = new ZendeskApiClient(http, JsonSerializer);
        return new ZendeskArticlesDataProvider(api, options ?? new ZendeskArticlesOptions
        {
            Subdomain = "test",
            Email = "agent@test.com"
        });
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ZendeskArticlesDataProvider(null!, new ZendeskArticlesOptions
            {
                Subdomain = "test",
                Email = "agent@test.com"
            }));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsArticles()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 100, "title": "Getting started", "body": "Welcome to our docs.", "updated_at": "2026-01-01T00:00:00Z", "section_id": 1 },
                { "id": 101, "title": "FAQ", "body": "Frequently asked questions.", "updated_at": "2026-01-02T00:00:00Z", "section_id": 2 }
              ],
              "end_time": 1735700000,
              "count": 2
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("article-100.md", results[0].Value.FileName);
        Assert.Equal("article-101.md", results[1].Value.FileName);
        Assert.Equal("100", results[0].Value.Id);
        Assert.Equal("2026-01-01T00:00:00Z", results[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_HtmlBody_IsStrippedToMarkdown()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 200, "title": "HTML article", "body": "<p>hello</p><br/><strong>world</strong>&amp;more", "updated_at": "2026-02-01T00:00:00Z", "section_id": 1 }
              ],
              "end_time": 1738400000,
              "count": 1
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.StartsWith("# HTML article", content, StringComparison.Ordinal);
        Assert.Contains("hello", content, StringComparison.Ordinal);
        Assert.Contains("world", content, StringComparison.Ordinal);
        Assert.Contains("&more", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_UsesStartTime()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 300, "title": "Updated article", "body": "New content.", "updated_at": "2026-03-01T00:00:00Z", "section_id": 5 }
              ],
              "end_time": 1740000000,
              "count": 1
            }
            """;

        var handler = new ArticleCapturingHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var opts = new ZendeskArticlesOptions
        {
            Subdomain = "test",
            Email = "agent@test.com",
            DeltaToken = "1735000000"
        };
        var sut = MakeProvider(handler, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var articleUrl = handler.CapturedUrls.First(u =>
            u.Contains("incremental", StringComparison.Ordinal));
        Assert.Contains("1735000000", articleUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 400, "title": "Filtered out", "body": "Should not appear.", "updated_at": "2026-01-01T00:00:00Z", "section_id": 1 }
              ],
              "end_time": 1735700000,
              "count": 1
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var opts = new ZendeskArticlesOptions
        {
            Subdomain = "test",
            Email = "agent@test.com",
            Extensions = [".txt"] // articles are .md -- nothing should match
        };
        var sut = MakeProvider(handler, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_NullBody_YieldsEmptyContent()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 500, "title": "No body article", "body": null, "updated_at": "2026-01-10T00:00:00Z", "section_id": 1 }
              ],
              "end_time": 1736500000,
              "count": 1
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.StartsWith("# No body article", content, StringComparison.Ordinal);
        // Only the title heading should be present, no body text
        Assert.Equal("# No body article", content.Trim());
    }

    [Fact]
    public async Task GetFilesAsync_Pagination_FetchesAllPages()
    {
        const string page1Json = """
            {
              "articles": [
                { "id": 600, "title": "Page 1 article", "body": "First page.", "updated_at": "2026-01-01T00:00:00Z", "section_id": 1 }
              ],
              "end_time": 1735600000,
              "count": 1000
            }
            """;
        const string page2Json = """
            {
              "articles": [
                { "id": 601, "title": "Page 2 article", "body": "Second page.", "updated_at": "2026-01-02T00:00:00Z", "section_id": 2 }
              ],
              "end_time": 1735700000,
              "count": 1
            }
            """;

        var handler = new ArticleSequentialHandler([page1Json, page2Json]);
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("article-600.md", results[0].Value.FileName);
        Assert.Equal("article-601.md", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyArticles_YieldsNothing()
    {
        const string articlesJson = """
            {
              "articles": [],
              "end_time": 1735700000,
              "count": 0
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        // Two articles so cancellation fires between them.
        const string articlesJson = """
            {
              "articles": [
                { "id": 700, "title": "Cancel test 1", "body": "Body1.", "updated_at": "2026-01-01T00:00:00Z", "section_id": 1 },
                { "id": 701, "title": "Cancel test 2", "body": "Body2.", "updated_at": "2026-01-02T00:00:00Z", "section_id": 1 }
              ],
              "end_time": 1735700000,
              "count": 2
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in sut.GetFilesAsync(cts.Token))
            {
                // Cancel after consuming the first item
                cts.Cancel();
            }
        });
    }

    [Fact]
    public async Task GetFilesAsync_ContentReadable_MarkdownCorrect()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 800, "title": "Setup Guide", "body": "<p>Follow these steps to get started.</p>", "updated_at": "2026-02-01T00:00:00Z", "section_id": 3 }
              ],
              "end_time": 1738400000,
              "count": 1
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.StartsWith("# Setup Guide", content, StringComparison.Ordinal);
        Assert.Contains("Follow these steps to get started.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_HtmlEntities_Decoded()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 900, "title": "Entities test", "body": "<p>Tom &amp; Jerry &lt;3 &gt; fun &quot;always&quot;</p>", "updated_at": "2026-02-15T00:00:00Z", "section_id": 1 }
              ],
              "end_time": 1739500000,
              "count": 1
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("Tom & Jerry", content, StringComparison.Ordinal);
        Assert.Contains("<3", content, StringComparison.Ordinal);
        Assert.Contains("\"always\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", content, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", content, StringComparison.Ordinal);
        Assert.DoesNotContain("&quot;", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsUpdatedAt()
    {
        const string articlesJson = """
            {
              "articles": [
                { "id": 950, "title": "ETag check", "body": "Content.", "updated_at": "2026-03-20T08:15:00Z", "section_id": 1 }
              ],
              "end_time": 1742500000,
              "count": 1
            }
            """;

        var handler = new ArticleFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("2026-03-20T08:15:00Z", results[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_HttpError_PropagatesFailure()
    {
        var handler = new ArticleErrorHandler(HttpStatusCode.ServiceUnavailable);
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        var err = Assert.IsType<RagError.HttpFailed>(results[0].Error);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, err.StatusCode);
    }

    private static async Task<string> ReadContentAsync(Rag.NET.DataProviders.FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure -- fake HTTP handlers
// ---------------------------------------------------------------------------

/// <summary>
/// Returns canned JSON responses keyed by URL substring, so tests never hit the network.
/// </summary>
file sealed class ArticleFakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
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
/// Returns canned JSON responses keyed by URL substring and captures request URLs for assertions.
/// </summary>
file sealed class ArticleCapturingHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    private readonly List<string> _urls = [];
    public IReadOnlyList<string> CapturedUrls => _urls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        _urls.Add(url);
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
/// Returns sequential article page responses for pagination tests.
/// </summary>
file sealed class ArticleSequentialHandler(List<string> pages) : HttpMessageHandler
{
    private int _pageIndex;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = _pageIndex < pages.Count
            ? pages[_pageIndex++]
            : pages[^1];

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Always returns an HTTP error for all requests.
/// </summary>
file sealed class ArticleErrorHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(statusCode));
}
