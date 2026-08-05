using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Asana;
using Rag.NET.DataProviders.Testing;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.Asana.Tests;

public sealed class AsanaDataProviderTests
{
    private static readonly IRestSerializer JsonSerializer = new SystemTextJsonSerializer();

    private static AsanaDataProvider MakeProvider(
        Dictionary<string, string> responses,
        AsanaOptions? options = null)
    {
        var handler = new FakeHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        var api = new AsanaApiClient(http, JsonSerializer);
        return new AsanaDataProvider(api, options ?? new AsanaOptions { WorkspaceGid = "ws-1" });
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

        _ = Assert.Single(results);
        Assert.Equal("Fix bug.md", results[0].Value.FileName);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("## Subtasks", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_HostileTaskName_Sanitized()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-9",
                  "name": "Ship v2: API/CLI",
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
            ["/api/1.0/tasks"]  = tasksJson,
            ["task-9/subtasks"] = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("Ship v2_ API_CLI.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_RequestContainsModifiedSince()
    {
        const string tasksJson = """{ "data": [] }""";

        var capturer = new FakeCapturingHandler(tasksJson);
        var http = new HttpClient(capturer) { BaseAddress = new Uri("https://app.asana.com") };
        var api = new AsanaApiClient(http, JsonSerializer);
        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-1",
            DeltaToken   = "2026-03-01T00:00:00Z"
        };
        var sut = new AsanaDataProvider(api, opts);

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
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AsanaDataProvider(null!, new AsanaOptions { WorkspaceGid = "ws-1" }));
    }

    [Fact]
    public async Task GetFilesAsync_ProjectGidSet_UsesProjectEndpoint()
    {
        const string tasksJson = """{ "data": [] }""";

        var capturer = new FakeCapturingHandler(tasksJson);
        var http = new HttpClient(capturer) { BaseAddress = new Uri("https://app.asana.com") };
        var api = new AsanaApiClient(http, JsonSerializer);
        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-1",
            ProjectGid   = "proj-42"
        };
        var sut = new AsanaDataProvider(api, opts);

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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
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

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
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
        var api = new AsanaApiClient(http, JsonSerializer);
        var sut = new AsanaDataProvider(api, new AsanaOptions { WorkspaceGid = "ws-1" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("Page one task.md", results[0].Value.FileName);
        Assert.Equal("Page two task.md", results[1].Value.FileName);
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

        _ = Assert.Single(results);
        Assert.Equal(string.Empty, results[0].Value.ETag);
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
        var api = new AsanaApiClient(http, JsonSerializer);
        var sut = new AsanaDataProvider(api, new AsanaOptions { WorkspaceGid = "ws-1" });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    [Fact]
    public async Task GetFilesAsync_TaskListHttpError_PropagatesFailure()
    {
        var handler = new FakeStatusHandler(HttpStatusCode.Unauthorized);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        var api = new AsanaApiClient(http, JsonSerializer);
        var sut = new AsanaDataProvider(api, new AsanaOptions { WorkspaceGid = "ws-1" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        var error = Assert.IsType<RagError.HttpFailed>(results[0].Error);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task GetFilesAsync_SubtaskHttpError_PropagatesFailure()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-err",
                  "name": "Erroring task",
                  "notes": null,
                  "due_on": null,
                  "completed": false,
                  "assignee": null,
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;

        // The routing handler returns the task list for the tasks endpoint and 503 for subtasks.
        var handler = new FakeRoutingHandler(url =>
            url.Contains("/subtasks", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : url.Contains("/api/1.0/tasks", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(tasksJson, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        var api = new AsanaApiClient(http, JsonSerializer);
        var sut = new AsanaDataProvider(api, new AsanaOptions { WorkspaceGid = "ws-1" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        var error = Assert.IsType<RagError.HttpFailed>(results[0].Error);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public void AddAsanaDataProvider_DefaultBaseUrl_UsesProductionUrl()
    {
        var services = new ServiceCollection();
        services.AddAsanaDataProvider(
            personalAccessToken: "test-pat",
            workspaceGid: "ws-1");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddAsanaDataProvider_CustomBaseUrl_OverridesDefault()
    {
        var services = new ServiceCollection();
        services.AddAsanaDataProvider(
            personalAccessToken: "test-pat",
            workspaceGid: "ws-1",
            baseUrl: "https://custom.example.com");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddAsanaDataProvider_ITokenProvider_CustomBaseUrl_OverridesDefault()
    {
        var services = new ServiceCollection();
        services.AddAsanaDataProvider(
            tokenProvider: new FakeTokenProvider(),
            workspaceGid: "ws-1",
            baseUrl: "https://custom.example.com");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsTaskKeys()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-md",
                  "name": "Fix bug",
                  "notes": null,
                  "due_on": "2026-04-01",
                  "completed": true,
                  "assignee": { "name": "Bob" },
                  "modified_at": "2026-03-01T10:00:00Z"
                }
              ]
            }
            """;
        const string subtasksJson = """{ "data": [] }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"]     = tasksJson,
            ["task-md/subtasks"]   = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("Bob",        metadata["assignee"]);
        Assert.Equal("2026-04-01", metadata["due_on"]);
        // updated_at is no longer a plain tag — it is the typed FileHandle.UpdatedAt, asserted in
        // GetFilesAsync_ModifiedAt_IsTypedAndSurfacesAsUpdatedAtChunkTag below.
        Assert.False(metadata.ContainsKey("updated_at"));
        // Lowercase literal, deliberately unlike the Markdown line's bool.ToString() ("True"):
        // HasTagSpec matches ordinally.
        Assert.Equal("true", metadata["completed"]);
        // WorkspaceGid is required and scopes every request, so workspace is unconditional.
        Assert.Equal("ws-1", metadata["workspace"]);
        Assert.False(metadata.ContainsKey("project"));
        Assert.Equal(4, metadata.Count);

        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("**Completed:** True", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ProjectGidSet_MetadataCarriesProject()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-pj",
                  "name": "Scoped task",
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

        // ProjectGid routes to /projects/{gid}/tasks, so match on the shared "/tasks" suffix;
        // the longer subtasks key still wins for the subtask fetch.
        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/tasks"]           = tasksJson,
            ["task-pj/subtasks"] = subtasksJson
        }, new AsanaOptions { WorkspaceGid = "ws-1", ProjectGid = "proj-42" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("ws-1",    metadata["workspace"]);
        Assert.Equal("proj-42", metadata["project"]);
    }

    [Fact]
    public async Task GetFilesAsync_NullOptionalFields_MetadataOmitsThem()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-mo",
                  "name": "Minimal task",
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
            ["/api/1.0/tasks"]     = tasksJson,
            ["task-mo/subtasks"]   = subtasksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("false", metadata["completed"]);
        Assert.Equal("ws-1",  metadata["workspace"]);
        Assert.False(metadata.ContainsKey("assignee"));
        Assert.False(metadata.ContainsKey("due_on"));
        Assert.False(metadata.ContainsKey("updated_at"));
        Assert.False(metadata.ContainsKey("project"));
        Assert.Equal(2, metadata.Count);
    }

    /// <summary>
    /// Phase 4.10 Task 4: <c>modified_at</c> used to be written as a plain
    /// <c>metadata["updated_at"]</c> tag; it now becomes <c>FileHandle.UpdatedAt</c> and reaches
    /// <c>FileEntry.UpdatedAt</c> unchanged. This pins both that the connector still produces the
    /// timestamp at all, and that it still surfaces as the reserved chunk tag downstream.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_ModifiedAt_IsTypedAndSurfacesAsUpdatedAtChunkTag()
    {
        const string tasksJson = """
            {
              "data": [
                {
                  "gid": "task-ts",
                  "name": "Timestamp task",
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
            ["/api/1.0/tasks"]   = tasksJson,
            ["task-ts/subtasks"] = subtasksJson
        });

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

file sealed class FakeStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(statusCode));
}

file sealed class FakeRoutingHandler(Func<string, HttpResponseMessage> router) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(router(request.RequestUri!.ToString()));
}

file sealed class FakeTokenProvider : ITokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult("fake-token");
}
