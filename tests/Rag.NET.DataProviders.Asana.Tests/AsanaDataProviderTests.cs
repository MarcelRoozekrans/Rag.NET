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
