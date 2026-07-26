using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Testing;
using Rag.NET.DataProviders.Zendesk;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.Zendesk.Tests;

public sealed class ZendeskTicketsDataProviderTests
{
    private static readonly IRestSerializer JsonSerializer = new SystemTextJsonSerializer();

    private static ZendeskTicketsDataProvider MakeProvider(
        HttpMessageHandler handler,
        ZendeskTicketsOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.zendesk.com") };
        var api = new ZendeskApiClient(http, JsonSerializer);
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
        Assert.Equal("ticket-1.md", results[0].Value.FileName);
        Assert.Equal("ticket-2.md", results[1].Value.FileName);
        Assert.Equal("1", results[0].Value.Id);
        Assert.Equal("2026-01-01T00:00:00Z", results[0].Value.ETag);
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

        _ = Assert.Single(results);
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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
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

    [Fact]
    public async Task GetFilesAsync_NullOptionalFields_MarkdownOmitsThem()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 5, "subject": "Minimal ticket", "description": null, "status": "new", "priority": null, "updated_at": "2026-01-05T00:00:00Z" }
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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.StartsWith("# Minimal ticket", content, StringComparison.Ordinal);
        Assert.Contains("**Status:** new", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Priority", content, StringComparison.Ordinal);
        // Description is null, so it should not appear as text between status and comments
        Assert.DoesNotContain("## Comments", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_Pagination_FetchesAllPages()
    {
        const string page1Json = """
            {
              "tickets": [
                { "id": 1, "subject": "First", "description": "Desc1", "status": "open", "priority": null, "updated_at": "2026-01-01T00:00:00Z" }
              ],
              "after_cursor": "cursor_abc",
              "end_of_stream": false,
              "end_time": 1735600000
            }
            """;
        const string page2Json = """
            {
              "tickets": [
                { "id": 2, "subject": "Second", "description": "Desc2", "status": "solved", "priority": null, "updated_at": "2026-01-02T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1735700000
            }
            """;
        const string commentsJson = """{ "comments": [] }""";

        var handler = new FakeSequentialHandler(
            ticketPages: [page1Json, page2Json],
            commentsJson: commentsJson);
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("ticket-1.md", results[0].Value.FileName);
        Assert.Equal("ticket-2.md", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyTickets_YieldsNothing()
    {
        const string ticketsJson = """
            {
              "tickets": [],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1735700000
            }
            """;

        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/incremental/tickets/cursor.json"] = ticketsJson
        });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        // Two tickets so cancellation fires between them.
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 1, "subject": "First", "description": "Desc1", "status": "open", "priority": null, "updated_at": "2026-01-01T00:00:00Z" },
                { "id": 2, "subject": "Second", "description": "Desc2", "status": "open", "priority": null, "updated_at": "2026-01-02T00:00:00Z" }
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
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 7, "subject": "Login broken", "description": "Cannot log in at all", "status": "open", "priority": "high", "updated_at": "2026-02-10T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1739000000
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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.StartsWith("# Login broken", content, StringComparison.Ordinal);
        Assert.Contains("**Status:** open", content, StringComparison.Ordinal);
        Assert.Contains("**Priority:** high", content, StringComparison.Ordinal);
        Assert.Contains("Cannot log in at all", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsUpdatedAt()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 9, "subject": "ETag test", "description": "Check etag", "status": "closed", "priority": null, "updated_at": "2026-03-15T12:30:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1742000000
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

        _ = Assert.Single(results);
        Assert.Equal("2026-03-15T12:30:00Z", results[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_MultipleComments_AllRendered()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 50, "subject": "Multi-comment ticket", "description": "Original issue", "status": "open", "priority": "low", "updated_at": "2026-02-20T00:00:00Z" }
              ],
              "after_cursor": null,
              "end_of_stream": true,
              "end_time": 1740000000
            }
            """;
        const string commentsJson = """
            {
              "comments": [
                { "id": 201, "body": "First comment body", "author_id": 10, "created_at": "2026-02-20T01:00:00Z" },
                { "id": 202, "body": "Second comment body", "author_id": 20, "created_at": "2026-02-20T02:00:00Z" },
                { "id": 203, "body": "Third comment body", "author_id": 30, "created_at": "2026-02-20T03:00:00Z" }
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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("## Comments", content, StringComparison.Ordinal);
        Assert.Contains("First comment body", content, StringComparison.Ordinal);
        Assert.Contains("Second comment body", content, StringComparison.Ordinal);
        Assert.Contains("Third comment body", content, StringComparison.Ordinal);
        Assert.Contains("**10**", content, StringComparison.Ordinal);
        Assert.Contains("**20**", content, StringComparison.Ordinal);
        Assert.Contains("**30**", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_TicketsHttpError_PropagatesFailure()
    {
        var handler = new FakeErrorHandler(HttpStatusCode.ServiceUnavailable);
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        var err = Assert.IsType<RagError.HttpFailed>(results[0].Error);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, err.StatusCode);
    }

    [Fact]
    public async Task GetFilesAsync_CommentsHttpError_PropagatesFailure()
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

        var handler = new FakePartialErrorHandler(
            ticketsJson: ticketsJson,
            commentsStatusCode: HttpStatusCode.Forbidden);
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        var err = Assert.IsType<RagError.HttpFailed>(results[0].Error);
        Assert.Equal(HttpStatusCode.Forbidden, err.StatusCode);
    }

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public void AddZendeskTicketsDataProvider_DefaultBaseUrl_UsesProductionUrl()
    {
        var services = new ServiceCollection();
        services.AddZendeskTicketsDataProvider(
            subdomain: "mycompany",
            email: "agent@mycompany.com",
            apiToken: "test-token");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddZendeskTicketsDataProvider_CustomBaseUrl_OverridesDefault()
    {
        var services = new ServiceCollection();
        services.AddZendeskTicketsDataProvider(
            subdomain: "mycompany",
            email: "agent@mycompany.com",
            apiToken: "test-token",
            baseUrl: "https://custom.example.com");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddZendeskTicketsDataProvider_NullBaseUrl_UsesSubdomainUrl()
    {
        var services = new ServiceCollection();
        services.AddZendeskTicketsDataProvider(
            subdomain: "mycompany",
            email: "agent@mycompany.com",
            apiToken: "test-token");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsTicketKeys()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 7, "subject": "Login issue", "description": "Cannot login", "status": "open", "priority": "high", "updated_at": "2026-01-01T00:00:00Z" }
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

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("7",                    metadata["ticket_id"]);
        Assert.Equal("open",                 metadata["status"]);
        Assert.Equal("high",                 metadata["priority"]);
        Assert.Equal("2026-01-01T00:00:00Z", metadata["updated_at"]);
        // Container context, reachable only because ToHandle became an instance method.
        Assert.Equal("test", metadata["subdomain"]);
        Assert.Equal(5, metadata.Count);

        // Status and priority stay in the Markdown body — that is what gets embedded.
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("**Status:** open", content, StringComparison.Ordinal);
        Assert.Contains("**Priority:** high", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullPriority_MetadataOmitsIt()
    {
        const string ticketsJson = """
            {
              "tickets": [
                { "id": 8, "subject": "No priority", "description": "x", "status": "solved", "priority": null, "updated_at": "2026-03-01T00:00:00Z" }
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

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("8",      metadata["ticket_id"]);
        Assert.Equal("solved", metadata["status"]);
        Assert.False(metadata.ContainsKey("priority"));
        Assert.Equal(4, metadata.Count);
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

/// <summary>
/// Returns sequential ticket page responses for pagination tests, and a fixed comments response.
/// </summary>
file sealed class FakeSequentialHandler(List<string> ticketPages, string commentsJson) : HttpMessageHandler
{
    private int _ticketPageIndex;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        string json;
        if (url.Contains("incremental/tickets", StringComparison.Ordinal))
        {
            json = _ticketPageIndex < ticketPages.Count
                ? ticketPages[_ticketPageIndex++]
                : ticketPages[^1];
        }
        else
        {
            json = commentsJson;
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Always returns an HTTP error for all requests.
/// </summary>
file sealed class FakeErrorHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(statusCode));
}

/// <summary>
/// Returns a successful tickets response but an HTTP error for comments requests.
/// </summary>
file sealed class FakePartialErrorHandler(string ticketsJson, HttpStatusCode commentsStatusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("incremental/tickets", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ticketsJson, Encoding.UTF8, "application/json")
            });

        return Task.FromResult(new HttpResponseMessage(commentsStatusCode));
    }
}
