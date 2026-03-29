using System.Net;
using System.Text;
using Rag.NET.DataProviders.Zendesk;
using Refit;
using Xunit;

namespace Rag.NET.DataProviders.Zendesk.Tests;

public sealed class ZendeskTicketsDataProviderTests
{
    private static ZendeskTicketsDataProvider MakeProvider(
        HttpMessageHandler handler,
        ZendeskTicketsOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.zendesk.com") };
        var api = RestService.For<IZendeskApi>(http);
        return new ZendeskTicketsDataProvider(api, options ?? new ZendeskTicketsOptions
        {
            Subdomain = "test",
            Email = "agent@test.com"
        });
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ZendeskTicketsDataProvider(null!, new ZendeskTicketsOptions
            {
                Subdomain = "test",
                Email = "agent@test.com"
            }));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsTickets()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 1, "subject": "Login issue", "description": "Cannot login", "status": "open", "priority": "high", "updated_at": "2026-01-01T00:00:00Z" },
                { "id": 2, "subject": "Billing question", "description": "Overcharged", "status": "pending", "priority": "normal", "updated_at": "2026-01-02T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1735700000
            }
            """;
        const string commentsJson = """{ "comments": [] }""";

        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/incremental/tickets/cursor.json"] = ticketsJson,
            ["/api/v2/tickets/"] = commentsJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("ticket-1.md", results[0].FileName);
        Assert.Equal("ticket-2.md", results[1].FileName);
        Assert.Equal("1", results[0].Id);
        Assert.Equal("2026-01-01T00:00:00Z", results[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_UsesStartTime()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 10, "subject": "Delta ticket", "description": "Updated", "status": "solved", "priority": null, "updated_at": "2026-03-01T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1740000000
            }
            """;
        const string commentsJson = """{ "comments": [] }""";

        var handler = new FakeCapturingHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/incremental/tickets/cursor.json"] = ticketsJson,
            ["/api/v2/tickets/"] = commentsJson
        });
        var opts = new ZendeskTicketsOptions
        {
            Subdomain = "test",
            Email = "agent@test.com",
            DeltaToken = "1735000000"
        };
        var sut = MakeProvider(handler, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        // Verify the incremental endpoint was called with start_time from DeltaToken
        var ticketUrl = handler.CapturedUrls.First(u =>
            u.Contains("incremental", StringComparison.Ordinal));
        Assert.Contains("1735000000", ticketUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_WithComments_MarkdownRendersComments()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 42, "subject": "Need help", "description": "I need assistance", "status": "open", "priority": "urgent", "updated_at": "2026-02-15T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1739000000
            }
            """;
        const string commentsJson = """
            {
              "comments": [
                { "id": 100, "body": "We are looking into it.", "author_id": 999, "created_at": "2026-02-15T01:00:00Z" },
                { "id": 101, "body": "Issue resolved.", "author_id": 999, "created_at": "2026-02-15T02:00:00Z" }
              ]
            }
            """;

        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/incremental/tickets/cursor.json"] = ticketsJson,
            ["/api/v2/tickets/"] = commentsJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        var content = await ReadContentAsync(results[0]);
        Assert.StartsWith("# Need help", content, StringComparison.Ordinal);
        Assert.Contains("**Status:** open", content, StringComparison.Ordinal);
        Assert.Contains("**Priority:** urgent", content, StringComparison.Ordinal);
        Assert.Contains("I need assistance", content, StringComparison.Ordinal);
        Assert.Contains("## Comments", content, StringComparison.Ordinal);
        Assert.Contains("We are looking into it.", content, StringComparison.Ordinal);
        Assert.Contains("Issue resolved.", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 1, "subject": "Test", "description": "Desc", "status": "open", "priority": null, "updated_at": "2026-01-01T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1735700000
            }
            """;
        const string commentsJson = """{ "comments": [] }""";

        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/incremental/tickets/cursor.json"] = ticketsJson,
            ["/api/v2/tickets/"] = commentsJson
        });
        var opts = new ZendeskTicketsOptions
        {
            Subdomain = "test",
            Email = "agent@test.com",
            Extensions = [".txt"] // tickets are .md — nothing should match
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
/// Returns canned JSON responses keyed by URL substring and captures request URLs for assertions.
/// </summary>
file sealed class FakeCapturingHandler(Dictionary<string, string> responses) : HttpMessageHandler
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
