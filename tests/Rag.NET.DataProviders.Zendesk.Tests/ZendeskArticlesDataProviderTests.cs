using System.Net;
using System.Text;
using Rag.NET.DataProviders.Zendesk;
using Refit;
using Xunit;

namespace Rag.NET.DataProviders.Zendesk.Tests;

public sealed class ZendeskArticlesDataProviderTests
{
    private static ZendeskArticlesDataProvider MakeProvider(
        HttpMessageHandler handler,
        ZendeskArticlesOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.zendesk.com") };
        var api = RestService.For<IZendeskApi>(http);
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
        Assert.Equal("article-100.md", results[0].FileName);
        Assert.Equal("article-101.md", results[1].FileName);
        Assert.Equal("100", results[0].Id);
        Assert.Equal("2026-01-01T00:00:00Z", results[0].ETag);
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

        Assert.Single(results);
        var content = await ReadContentAsync(results[0]);
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

        Assert.Single(results);
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
