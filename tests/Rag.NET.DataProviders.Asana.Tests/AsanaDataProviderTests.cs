using System.Net;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Asana;
using Xunit;

namespace Rag.NET.DataProviders.Asana.Tests;

public sealed class AsanaDataProviderTests
{
    private static AsanaDataProvider MakeProvider(
        Dictionary<string, string> responses,
        AsanaOptions? options = null)
    {
        var handler = new FakeHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        return new AsanaDataProvider(
            http,
            new StaticTokenProvider("test-token"),
            options ?? new AsanaOptions { WorkspaceGid = "ws-1" });
    }

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsTaskWithSubtasks()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-1",
                  "name": "Fix bug",
                  "notes": "Reproduce on dev",
                  "due_on": "2026-04-01",
                  "completed": false,
                  "assignee": { "name": "Bob" },
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        const string subtasksJson = """
            {
              "data": [
                {
                  "gid": "sub-1",
                  "name": "Write test",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T09:00:00Z"
                }
              ]
            }
            """;

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]        = tasksJson,
            ["task-1/subtasks"]       = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Fix bug.md", results[0].FileName);
        var content = await ReadContentAsync(results[0]);
        Assert.Contains("## Subtasks", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_RequestContainsModifiedSince()
    {
        const string tasksJson = """{ "data": [] }""";

        var capturer = new FakeCapturingHandler(tasksJson);
        var http = new HttpClient(capturer) { BaseAddress = new Uri("https://app.asana.com") };
        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-1",
            DeltaToken   = "2026-03-01T00:00:00Z"
        };
        var sut = new AsanaDataProvider(http, new StaticTokenProvider("test-token"), opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturer.LastRequestUrl);
        Assert.Contains("modified_since", capturer.LastRequestUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-2",
                  "name": "Some task",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        const string subtasksJson = """{ "data": [] }""";

        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-1",
            Extensions   = [".txt"]
        };

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]  = tasksJson,
            ["task-2/subtasks"] = subtasksJson
        }, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullHttp_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AsanaDataProvider(null!, new StaticTokenProvider("tok"),
                new AsanaOptions { WorkspaceGid = "ws-1" }));
    }

    [Fact]
    public void Constructor_NullTokenProvider_Throws()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://app.asana.com") };
        Assert.Throws<ArgumentNullException>(() =>
            new AsanaDataProvider(http, null!, new AsanaOptions { WorkspaceGid = "ws-1" }));
    }

    [Fact]
    public async Task GetFilesAsync_ProjectGidSet_UsesProjectEndpoint()
    {
        const string tasksJson = """{ "data": [] }""";

        var capturer = new FakeCapturingHandler(tasksJson);
        var http = new HttpClient(capturer) { BaseAddress = new Uri("https://app.asana.com") };
        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-1",
            ProjectGid   = "proj-42"
        };
        var sut = new AsanaDataProvider(http, new StaticTokenProvider("test-token"), opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturer.LastRequestUrl);
        Assert.Contains("/projects/proj-42/tasks", capturer.LastRequestUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullOptionalFields_MarkdownOmitsThem()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-n",
                  "name": "Minimal task",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        const string subtasksJson = """{ "data": [] }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]    = tasksJson,
            ["task-n/subtasks"]   = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        var content = await ReadContentAsync(results[0]);
        Assert.Contains("# Minimal task", content, StringComparison.Ordinal);
        Assert.Contains("**Completed:** False", content, StringComparison.Ordinal);
        Assert.DoesNotContain("**Due:**", content, StringComparison.Ordinal);
        Assert.DoesNotContain("**Assignee:**", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_WithSubtasks_MarkdownRendersSubtaskSection()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-s",
                  "name": "Parent task",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        const string subtasksJson = """
            {
              "data": [
                { "gid": "sub-a", "name": "Subtask Alpha" },
                { "gid": "sub-b", "name": "Subtask Beta" }
              ]
            }
            """;

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]    = tasksJson,
            ["task-s/subtasks"]   = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        var content = await ReadContentAsync(results[0]);
        Assert.Contains("## Subtasks", content, StringComparison.Ordinal);
        Assert.Contains("- Subtask Alpha", content, StringComparison.Ordinal);
        Assert.Contains("- Subtask Beta", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NoSubtasks_MarkdownOmitsSubtaskSection()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-ns",
                  "name": "No subs",
                  "notes": "Some notes",
                  "due_on": null,
                  "completed": true,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        const string subtasksJson = """{ "data": [] }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]     = tasksJson,
            ["task-ns/subtasks"]   = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        var content = await ReadContentAsync(results[0]);
        Assert.DoesNotContain("## Subtasks", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyResults_YieldsNothing()
    {
        const string tasksJson = """{ "data": [], "next_page": null }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"] = tasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_Pagination_FetchesAllPages()
    {
        const string page1Json = """
            {
              "data": [
                {
                  "gid": "task-p1",
                  "name": "Page one task",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ],
              "next_page": { "offset": "page2token" }
            }
            """;

        const string page2Json = """
            {
              "data": [
                {
                  "gid": "task-p2",
                  "name": "Page two task",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T11:00:00Z"
                }
              ],
              "next_page": null
            }
            """;

        const string subtasksJson = """{ "data": [] }""";

        var handler = new FakeSequentialHandler(new Dictionary<string, Queue<string>>(StringComparer.Ordinal)
        {
            ["?workspace="] = new Queue<string>([page1Json, page2Json]),
            ["/subtasks"]   = new Queue<string>([subtasksJson, subtasksJson])
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        var sut = new AsanaDataProvider(
            http,
            new StaticTokenProvider("test-token"),
            new AsanaOptions { WorkspaceGid = "ws-1" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("Page one task.md", results[0].FileName);
        Assert.Equal("Page two task.md", results[1].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_NullModifiedAt_ETagIsEmpty()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-m",
                  "name": "No modified",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": null
                }
              ]
            }
            """;

        const string subtasksJson = """{ "data": [] }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]    = tasksJson,
            ["task-m/subtasks"]   = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(string.Empty, results[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        // Three tasks so the cancellation token fires between yielded items.
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-c1",
                  "name": "First",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                },
                {
                  "gid": "task-c2",
                  "name": "Second",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                },
                {
                  "gid": "task-c3",
                  "name": "Third",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        const string subtasksJson = """{ "data": [] }""";

        using var cts = new CancellationTokenSource();
        // Cancel after 3 requests: 1 task-list + 2 subtask fetches.
        // The provider will check the token before fetching task-c3's subtasks.
        var handler = new FakeCancellingHandler(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["?workspace="]  = tasksJson,
                ["/subtasks"]    = subtasksJson
            }, cts, cancelAfterRequests: 3);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        var sut = new AsanaDataProvider(
            http,
            new StaticTokenProvider("test-token"),
            new AsanaOptions { WorkspaceGid = "ws-1" });

        // The cancelled token causes either an OperationCanceledException (from
        // ThrowIfCancellationRequested) or a Refit-internal NullReferenceException
        // depending on timing.  Either way, enumeration must not complete normally.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
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
        // Pick the longest matching key so that more-specific keys (e.g. "/subtasks") win
        // over shorter ones (e.g. "/api/1.0/tasks") when both match.
        var key = responses.Keys
            .Where(k => url.Contains(k, StringComparison.Ordinal))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
        });
    }
}

file sealed class FakeCapturingHandler(string responseJson) : HttpMessageHandler
{
    public string? LastRequestUrl { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUrl = request.RequestUri!.ToString();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
    }
}

file sealed class FakeCancellingHandler(
    Dictionary<string, string> responses,
    CancellationTokenSource cts,
    int cancelAfterRequests) : HttpMessageHandler
{
    private int _requestCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _requestCount) >= cancelAfterRequests)
            cts.Cancel();

        var url = request.RequestUri!.ToString();
        var key = responses.Keys
            .Where(k => url.Contains(k, StringComparison.Ordinal))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
        });
    }
}

file sealed class FakeSequentialHandler(Dictionary<string, Queue<string>> responseQueues) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var key = responseQueues.Keys
            .Where(k => url.Contains(k, StringComparison.Ordinal))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        if (key is null || responseQueues[key].Count == 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var json = responseQueues[key].Dequeue();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
