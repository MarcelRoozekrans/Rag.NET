using System.Globalization;
using System.Net;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Jira;
using Rag.NET.DataProviders.Testing;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.Jira.Tests;

public sealed class JiraDataProviderTests
{
    private static readonly IRestSerializer JsonSerializer = new SystemTextJsonSerializer();

    private static JiraDataProvider MakeProvider(
        string responseJson,
        JiraOptions? options = null,
        string urlKey = "/rest/api/3/search")
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { [urlKey] = responseJson });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        return new JiraDataProvider(api, options ?? new JiraOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsIssues()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Fix login bug",
                    "description": "Users cannot login",
                    "status": { "name": "In Progress" },
                    "priority": { "name": "High" },
                    "assignee": { "displayName": "Alice" },
                    "comment": { "comments": [] },
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("PROJ-1.md", results[0].Value.FileName);
        Assert.Equal("2026-03-01T10:00:00Z", results[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_JqlContainsUpdatedFilter()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10002",
                  "key": "PROJ-2",
                  "fields": {
                    "summary": "New issue",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-10T08:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;

        string? capturedUrl = null;
        var handler = new FakeCapturingHandler(json, url => capturedUrl = url);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        var opts = new JiraOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            DeltaToken = "2026-03-05T00:00:00Z"
        };
        var sut = new JiraDataProvider(api, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.NotNull(capturedUrl);
        // URL should contain URL-encoded form of "updated >"
        Assert.Contains("updated", capturedUrl, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            capturedUrl.Contains("updated+%3E", StringComparison.Ordinal) ||
            capturedUrl.Contains("updated%20%3E", StringComparison.Ordinal) ||
            capturedUrl.Contains("updated >", StringComparison.Ordinal),
            $"Expected URL to contain updated > filter, but got: {capturedUrl}");
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10003",
                  "key": "PROJ-3",
                  "fields": {
                    "summary": "Some issue",
                    "description": null,
                    "status": { "name": "Done" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T00:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        var opts = new JiraOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            Extensions = [".txt"]   // all issues emit .md — nothing should match
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
            new JiraDataProvider(null!, new JiraOptions
            {
                BaseUrl = "https://test.atlassian.net",
                Email   = "t@t.com"
            }));
    }

    [Fact]
    public void Constructor_InvalidDeltaToken_Throws()
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["/rest/api/3/search"] = """{"issues":[],"total":0}""" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);

        Assert.Throws<ArgumentException>(() =>
            new JiraDataProvider(api, new JiraOptions
            {
                BaseUrl    = "https://test.atlassian.net",
                Email      = "t@t.com",
                DeltaToken = "SELECT *; --"
            }));
    }

    [Fact]
    public void Constructor_InvalidProjectKey_Throws()
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["/rest/api/3/search"] = """{"issues":[],"total":0}""" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);

        Assert.Throws<ArgumentException>(() =>
            new JiraDataProvider(api, new JiraOptions
            {
                BaseUrl    = "https://test.atlassian.net",
                Email      = "t@t.com",
                ProjectKey = "has spaces"
            }));
    }

    [Fact]
    public async Task GetFilesAsync_StaleDeltaToken_FallsBackToFullTraversal()
    {
        const string fullJson = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Fallback issue",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;

        var handler = new FakeStaleDeltaHandler(fullJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        var opts = new JiraOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            DeltaToken = "2020-01-01T00:00:00Z"
        };
        var sut = new JiraDataProvider(api, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("PROJ-1.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_WithProjectKey_JqlContainsProjectFilter()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "ENG-1",
                  "fields": {
                    "summary": "Eng issue",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;

        string? capturedUrl = null;
        var handler = new FakeCapturingHandler(json, url => capturedUrl = url);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        var opts = new JiraOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            ProjectKey = "ENG"
        };
        var sut = new JiraDataProvider(api, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.NotNull(capturedUrl);
        Assert.Contains("project", capturedUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ENG", capturedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullOptionalFields_MarkdownOmitsThem()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Minimal issue",
                    "description": null,
                    "status": { "name": "Done" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("# Minimal issue", content, StringComparison.Ordinal);
        Assert.Contains("**Status:** Done", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Priority", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Assignee", content, StringComparison.Ordinal);
        Assert.DoesNotContain("## Comments", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_WithComments_MarkdownRendersCommentSection()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Commented issue",
                    "description": "Some description",
                    "status": { "name": "Open" },
                    "priority": { "name": "High" },
                    "assignee": { "displayName": "Alice" },
                    "comment": {
                      "comments": [
                        {
                          "author": { "displayName": "Bob" },
                          "created": "2026-03-02T12:00:00Z",
                          "body": "Looks good to me"
                        }
                      ]
                    },
                    "updated": "2026-03-02T12:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("## Comments", content, StringComparison.Ordinal);
        Assert.Contains("Bob", content, StringComparison.Ordinal);
        Assert.Contains("Looks good to me", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyResults_YieldsNothing()
    {
        const string json = """{"issues":[],"total":0}""";
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        var handler = new FakeCancellingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        var sut = new JiraDataProvider(api, new JiraOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    [Fact]
    public async Task GetFilesAsync_Pagination_FetchesAllIssues()
    {
        const string page1Json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Issue One",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 2
            }
            """;
        const string page2Json = """
            {
              "issues": [
                {
                  "id": "10002",
                  "key": "PROJ-2",
                  "fields": {
                    "summary": "Issue Two",
                    "description": null,
                    "status": { "name": "Done" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-02T10:00:00Z"
                  }
                }
              ],
              "total": 2
            }
            """;
        var handler = new FakeSequentialHandler(page1Json, page2Json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        var sut = new JiraDataProvider(api, new JiraOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("PROJ-1.md", results[0].Value.FileName);
        Assert.Equal("PROJ-2.md", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ApiReturnsServerError_YieldsHttpFailedError()
    {
        var handler = new FakeErrorHandler(HttpStatusCode.InternalServerError);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = new JiraApiClient(http, JsonSerializer);
        var sut = new JiraDataProvider(api, new JiraOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(results);
        Assert.True(single.IsFailure);
        var err = Assert.IsType<RagError.HttpFailed>(single.Error);
        Assert.Equal(HttpStatusCode.InternalServerError, err.StatusCode);
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsIssueKeys()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Fix login bug",
                    "description": "Users cannot login",
                    "status": { "name": "In Progress" },
                    "priority": { "name": "High" },
                    "assignee": { "displayName": "Alice" },
                    "comment": { "comments": [] },
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        // No ProjectKey configured — project is still emitted, derived from the issue key.
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("PROJ-1",      metadata["issue_key"]);
        Assert.Equal("In Progress", metadata["status"]);
        Assert.Equal("High",        metadata["priority"]);
        Assert.Equal("Alice",       metadata["assignee"]);
        // updated_at is no longer a plain tag — it is the typed FileHandle.UpdatedAt, asserted in
        // GetFilesAsync_Updated_IsTypedAndSurfacesAsUpdatedAtChunkTag below.
        Assert.False(metadata.ContainsKey("updated_at"));
        Assert.Equal("PROJ", metadata["project"]);
        Assert.Equal(5, metadata.Count);

        // The same values stay in the Markdown body — that is what gets embedded.
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("**Status:** In Progress", content, StringComparison.Ordinal);
        Assert.Contains("**Priority:** High", content, StringComparison.Ordinal);
        Assert.Contains("**Assignee:** Alice", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullPriorityAndAssignee_MetadataOmitsThem()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10002",
                  "key": "PROJ-2",
                  "fields": {
                    "summary": "Bare issue",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-10T08:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("PROJ-2", metadata["issue_key"]);
        Assert.Equal("Open",   metadata["status"]);
        Assert.Equal("PROJ",   metadata["project"]);
        Assert.False(metadata.ContainsKey("priority"));
        Assert.False(metadata.ContainsKey("assignee"));
        Assert.Equal(3, metadata.Count);
    }

    [Fact]
    public async Task GetFilesAsync_CustomJqlSpanningProjects_ProjectTracksEachIssue()
    {
        // The case that decided deriving project from the issue key rather than from options:
        // a caller-supplied Jql can span projects with ProjectKey unset, where a single
        // options-derived value would be wrong for most results.
        const string json = """
            {
              "issues": [
                {
                  "id": "1",
                  "key": "ALPHA-1",
                  "fields": {
                    "summary": "In Alpha",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T10:00:00Z"
                  }
                },
                {
                  "id": "2",
                  "key": "BETA-27",
                  "fields": {
                    "summary": "In Beta",
                    "description": null,
                    "status": { "name": "Done" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-02T10:00:00Z"
                  }
                }
              ],
              "total": 2
            }
            """;
        var sut = MakeProvider(json, new JiraOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com",
            Jql     = "labels = urgent order by updated DESC"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        Assert.Equal(2, results.Count);
        Assert.Equal("ALPHA", results[0].Value.Metadata!["project"]);
        Assert.Equal("BETA",  results[1].Value.Metadata!["project"]);
    }

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Phase 4.10 Task 4: <c>fields.updated</c> used to be written as a plain
    /// <c>metadata["updated_at"]</c> tag; it now becomes <c>FileHandle.UpdatedAt</c> and reaches
    /// <c>FileEntry.UpdatedAt</c> unchanged. This pins both that the connector still produces the
    /// timestamp at all, and that it still surfaces as the reserved chunk tag downstream.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_Updated_IsTypedAndSurfacesAsUpdatedAtChunkTag()
    {
        const string json = """
            {
              "issues": [
                {
                  "id": "10001",
                  "key": "PROJ-1",
                  "fields": {
                    "summary": "Timestamp issue",
                    "description": null,
                    "status": { "name": "Open" },
                    "priority": null,
                    "assignee": null,
                    "comment": null,
                    "updated": "2026-03-01T10:00:00Z"
                  }
                }
              ],
              "total": 1
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(results).Value;
        var expected = DateTime.Parse(
            "2026-03-01T10:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(expected, entry.UpdatedAt);
        Assert.False(entry.Metadata!.ContainsKey("updated_at"));

        await UpdatedAtChunkTagAssertion.AssertSurfacesAsChunkTagAsync(
            entry.UpdatedAt, TestContext.Current.CancellationToken);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handlers
// ---------------------------------------------------------------------------

file sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var key = responses.Keys.FirstOrDefault(k => url.Contains(k, StringComparison.Ordinal));
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
        });
    }
}

file sealed class FakeCapturingHandler(string responseJson, Action<string> captureUrl) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        captureUrl(request.RequestUri!.ToString());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
    }
}

file sealed class FakeSequentialHandler(params string[] responses) : HttpMessageHandler
{
    private int _index;

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
/// Returns HTTP 400 on the first request (simulating a stale delta token),
/// then returns the provided JSON for subsequent full-traversal requests.
/// </summary>
file sealed class FakeStaleDeltaHandler(string fullTraversalJson) : HttpMessageHandler
{
    private bool _firstRequest = true;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_firstRequest)
        {
            _firstRequest = false;
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"errorMessages":["Invalid JQL"]}""",
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fullTraversalJson, Encoding.UTF8, "application/json"),
            RequestMessage = request
        });
    }
}

file sealed class FakeCancellingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Always returns the given error status code, so tests can assert HTTP failure propagation.
/// </summary>
file sealed class FakeErrorHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(statusCode) { RequestMessage = request });
}
