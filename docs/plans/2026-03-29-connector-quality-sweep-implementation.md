# Connector Quality Sweep — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add comprehensive tests (~85-100 new), benchmarks (8 files), and documentation (guide, XML docs, README, index, samples) for all 7 new SaaS connectors.

**Architecture:** Three parallel workstreams — documentation (5 tasks), tests (7 tasks, one per connector), benchmarks (3 tasks) — followed by a final build+test gate. Independent tasks can run in parallel via subagents.

**Tech Stack:** xUnit, NSubstitute, BenchmarkDotNet v0.15, MailKit, Refit, Microsoft.Graph SDK, System.Text.Json

---

## Task 1: Update `docs/guide/data-providers.md`

**Files:**
- Modify: `docs/guide/data-providers.md:74-83` (connector reference table), `:215+` (DI examples section)

**Step 1: Add 7 rows to connector reference table**

Insert after the GitHub row (around line 82) in the existing table:

```markdown
| Confluence   | `Rag.NET.DataProviders.Confluence`      | Refit / REST          | Basic Auth (email + API token) | CQL `lastModified>` cursor        |
| Jira         | `Rag.NET.DataProviders.Jira`            | Refit / REST          | Basic Auth (email + API token) | JQL `updated >` timestamp          |
| Notion       | `Rag.NET.DataProviders.Notion`          | Refit / REST          | Bearer integration token       | Client-side `last_edited_time`     |
| Asana        | `Rag.NET.DataProviders.Asana`           | Refit / REST          | Bearer PAT or OAuth2           | `modified_since` parameter         |
| Slack        | `Rag.NET.DataProviders.Slack`           | Refit / REST          | Bearer bot token               | `oldest` Unix timestamp            |
| Microsoft Teams | `Rag.NET.DataProviders.MicrosoftTeams` | Microsoft.Graph SDK | OAuth2 client credentials      | Not yet supported (Graph delta)    |
| Gmail        | `Rag.NET.DataProviders.Gmail`           | MailKit IMAP          | OAuth2 (SaslMechanismOAuth2)   | IMAP UniqueId watermark            |
```

**Step 2: Add DI registration examples**

Append after the existing Web connector example:

```markdown
### Confluence

```csharp
services.AddConfluenceDataProvider(
    baseUrl:  "https://your-org.atlassian.net",
    email:    "user@your-org.com",
    apiToken: "your-api-token",
    configure: opts =>
    {
        opts.SpaceKey   = "ENG";          // optional — limit to one space
        opts.DeltaToken = savedToken;     // null = full traversal
        opts.Extensions = ["*"];          // pages always emit as .md
    });
```

### Jira

```csharp
services.AddJiraDataProvider(
    baseUrl:  "https://your-org.atlassian.net",
    email:    "user@your-org.com",
    apiToken: "your-api-token",
    configure: opts =>
    {
        opts.ProjectKey = "PROJ";                    // optional — limit to one project
        opts.Jql        = "order by updated DESC";   // default sort
        opts.DeltaToken = savedToken;                // null = full traversal
    });
```

### Notion

```csharp
services.AddNotionDataProvider(
    integrationToken: "ntn_xxxxxxxxxxxx",
    configure: opts =>
    {
        opts.DeltaToken = savedToken;  // ISO 8601 timestamp or null
    });
```

### Asana

```csharp
services.AddAsanaDataProvider(
    baseUrl:      "https://app.asana.com",
    workspaceGid: "1234567890",
    tokenProvider: new StaticTokenProvider("your-pat"),
    configure: opts =>
    {
        opts.ProjectGid = "9876543210";   // optional — limit to one project
        opts.DeltaToken = savedToken;     // ISO 8601 timestamp or null
    });
```

### Slack

```csharp
services.AddSlackDataProvider(
    botToken: "xoxb-your-bot-token",
    configure: opts =>
    {
        opts.ChannelId    = "C01234ABCDE";  // optional — limit to one channel
        opts.MessageLimit = 200;            // messages per page (default 200)
        opts.DeltaToken   = savedToken;     // Unix epoch string or null
    });
```

### Microsoft Teams

```csharp
services.AddMicrosoftTeamsDataProvider(
    tenantId:     "your-tenant-id",
    clientId:     "your-client-id",
    clientSecret: "your-client-secret",
    configure: opts =>
    {
        opts.TeamId    = "team-guid";      // optional — limit to one team
        opts.ChannelId = "channel-guid";   // optional — limit to one channel
    });
```

### Gmail

```csharp
services.AddGmailDataProvider(
    userName:      "user@gmail.com",
    tokenProvider: myOAuthTokenProvider,  // must supply OAuth2 access token
    configure: opts =>
    {
        opts.MaxResults = 500;             // limit per run (default 500)
        opts.DeltaToken = savedUid;        // IMAP UniqueId string or null
    });
```
```

**Step 3: Add delta token guidance for new connectors**

Append to the existing delta ingestion section:

```markdown
#### Platform-specific delta tokens

| Connector        | Token format                     | Persistence note                          |
|-----------------|----------------------------------|-------------------------------------------|
| Confluence       | ISO 8601 timestamp               | Save after each successful run            |
| Jira             | ISO 8601 timestamp               | Save after each successful run            |
| Notion           | ISO 8601 timestamp               | Client-side filter; save after run        |
| Asana            | ISO 8601 timestamp               | Passed as `modified_since` query param    |
| Slack            | Unix epoch string (e.g. `"1711670400.000000"`) | Save latest message `ts` after run |
| Microsoft Teams  | Not supported yet                | Future: Graph delta links                 |
| Gmail            | IMAP UniqueId integer (e.g. `"4523"`) | Save highest UID after run           |
```

**Step 4: Add error handling notes for new connectors**

Append to the existing error handling section:

```markdown
- **Confluence/Jira**: Stale delta tokens trigger HTTP 400 from the Atlassian API. The provider catches this and automatically falls back to full traversal — no action needed.
- **Slack**: The provider throws `InvalidOperationException` if the Slack API returns `ok: false`. Check bot token scopes: `channels:read`, `channels:history`, `users:read`.
- **Notion**: Requires an internal integration token with "Read content" capability. Share target pages/databases with the integration.
- **Asana**: Requires a Personal Access Token or OAuth2 token with `default` scope.
- **Gmail**: Requires OAuth2 with `https://mail.google.com/` scope. Token refresh is handled by your `ITokenProvider`.
```

**Step 5: Run docs build (if applicable) and commit**

```bash
git add docs/guide/data-providers.md
git commit -m "docs: add 7 new connectors to data-providers guide"
```

---

## Task 2: Add XML doc comments to all 21 connector source files

**Files:**
- Modify: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProvider.cs` (add class-level XML doc)
- Modify: `src/Rag.NET.DataProviders.Confluence/ConfluenceOptions.cs` (add property-level XML docs)
- Modify: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProviderExtensions.cs` (add method-level XML docs)
- Modify: (same pattern for Jira, Notion, Asana, Slack, MicrosoftTeams, Gmail — 21 files total)

**Step 1: Add XML docs to all DataProvider classes**

For each `*DataProvider.cs`, add a class-level `<summary>` before the class declaration. Example for Confluence:

```csharp
/// <summary>
/// Enumerates Confluence pages as Markdown file handles via the Confluence REST API.
/// <para>Full run: fetches all pages (optionally filtered by <see cref="ConfluenceOptions.SpaceKey"/>).
/// Delta run: uses a CQL <c>lastModified&gt;</c> filter when <see cref="ConfluenceOptions.DeltaToken"/> is set.
/// Falls back to full traversal on HTTP 400 (stale delta token).</para>
/// <para>Each page is emitted as a single <c>.md</c> file with HTML body stripped to plain text.</para>
/// </summary>
```

Apply similar patterns for each connector:

- **Jira**: "Enumerates Jira issues as Markdown... JQL `updated >` filter... falls back on HTTP 400..."
- **Notion**: "Enumerates Notion pages... `POST /v1/search` + `GET /v1/blocks/{id}/children`... delta uses descending sort with client-side time filter..."
- **Asana**: "Enumerates Asana tasks... workspace or project scoped... subtasks fetched per task... token refreshed per call..."
- **Slack**: "Enumerates Slack channel messages grouped by UTC day... thread replies expanded... user names resolved via cache..."
- **MicrosoftTeams**: "Enumerates Microsoft Teams channel messages grouped by UTC day via Microsoft Graph SDK... HTML body stripped..."
- **Gmail**: "Enumerates Gmail messages via IMAP using MailKit... OAuth2 auth... delta uses UniqueId watermark..."

**Step 2: Add XML docs to all Options classes**

For each `*Options.cs`, add property-level docs. Example for `ConfluenceOptions.cs`:

```csharp
/// <summary>Configuration for <see cref="ConfluenceDataProvider"/>.</summary>
public sealed class ConfluenceOptions : CloudStorageOptions
{
    /// <summary>Confluence instance base URL (e.g. <c>https://your-org.atlassian.net</c>).</summary>
    public required string BaseUrl { get; set; }

    /// <summary>Atlassian account email used for Basic Authentication.</summary>
    public required string Email { get; set; }

    /// <summary>Optional space key to limit traversal to a single space. Must match <c>^[A-Za-z0-9\-_]+$</c>.</summary>
    public string? SpaceKey { get; set; }
}
```

Apply same for: JiraOptions (BaseUrl, Email, ProjectKey, Jql), NotionOptions (DatabaseId reserved), AsanaOptions (WorkspaceGid, ProjectGid), SlackOptions (ChannelId, MessageLimit), MicrosoftTeamsOptions (TeamId, ChannelId), GmailOptions (UserName, MaxResults).

**Step 3: Add XML docs to all Extension methods**

For each `*DataProviderExtensions.cs`, add `<summary>`, `<param>`, and `<returns>` tags. Example:

```csharp
/// <summary>
/// Registers a <see cref="ConfluenceDataProvider"/> with the DI container.
/// </summary>
/// <param name="services">The service collection.</param>
/// <param name="baseUrl">Confluence instance base URL.</param>
/// <param name="email">Atlassian account email for Basic Auth.</param>
/// <param name="apiToken">Atlassian API token.</param>
/// <param name="configure">Optional callback to configure <see cref="ConfluenceOptions"/>.</param>
/// <returns>The service collection for chaining.</returns>
```

**Step 4: Build to verify no XML warnings, commit**

```bash
dotnet build
git add src/Rag.NET.DataProviders.*/
git commit -m "docs: add XML doc comments to all 7 connector source files"
```

---

## Task 3: Update README.md, index.md, and samples

**Files:**
- Modify: `README.md:22-38` (packages table)
- Modify: `docs/index.md` (package layout section)
- Modify: `samples/Rag.NET.Sample/Program.cs`

**Step 1: Add connector packages to README packages table**

Insert after the existing packages table rows:

```markdown
| [Rag.NET.DataProviders.Confluence](src/Rag.NET.DataProviders.Confluence) | Confluence pages via REST API |
| [Rag.NET.DataProviders.Jira](src/Rag.NET.DataProviders.Jira)           | Jira issues via REST API |
| [Rag.NET.DataProviders.Notion](src/Rag.NET.DataProviders.Notion)       | Notion pages and blocks via REST API |
| [Rag.NET.DataProviders.Asana](src/Rag.NET.DataProviders.Asana)         | Asana tasks and subtasks via REST API |
| [Rag.NET.DataProviders.Slack](src/Rag.NET.DataProviders.Slack)         | Slack channel messages via REST API |
| [Rag.NET.DataProviders.MicrosoftTeams](src/Rag.NET.DataProviders.MicrosoftTeams) | Teams channel messages via Microsoft Graph |
| [Rag.NET.DataProviders.Gmail](src/Rag.NET.DataProviders.Gmail)         | Gmail messages via IMAP (MailKit) |
```

**Step 2: Update index.md package diagram**

Add a "Data Providers" section to the package layout listing all connectors (Azure Blob, SharePoint, OneDrive, Google Drive, Dropbox, Box, GitHub, Web, Confluence, Jira, Notion, Asana, Slack, Microsoft Teams, Gmail).

**Step 3: Add commented-out example to sample**

In `samples/Rag.NET.Sample/Program.cs`, add a commented block after the DI setup showing how to wire up Confluence + Slack:

```csharp
// --- Optional: SaaS connector examples ---
// Uncomment to ingest from Confluence or Slack instead of local files.
//
// services.AddConfluenceDataProvider(
//     baseUrl:  "https://your-org.atlassian.net",
//     email:    "user@your-org.com",
//     apiToken: Environment.GetEnvironmentVariable("CONFLUENCE_API_TOKEN")!);
//
// services.AddSlackDataProvider(
//     botToken: Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")!,
//     configure: opts => opts.ChannelId = "C01234ABCDE");
```

**Step 4: Build, commit**

```bash
git add README.md docs/index.md samples/Rag.NET.Sample/Program.cs
git commit -m "docs: add new connectors to README, index, and sample"
```

---

## Task 4: Confluence comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Confluence.Tests/ConfluenceDataProviderTests.cs`

**Step 1: Write failing tests for input validation**

Add after existing tests:

```csharp
[Fact]
public void Constructor_InvalidDeltaToken_Throws()
{
    var api = MakeApi([]); // empty handler
    var opts = new ConfluenceOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", DeltaToken = "DROP TABLE; --" };
    Assert.Throws<ArgumentException>(() => new ConfluenceDataProvider(api, opts));
}

[Fact]
public void Constructor_InvalidSpaceKey_Throws()
{
    var api = MakeApi([]);
    var opts = new ConfluenceOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", SpaceKey = "foo bar\"" };
    Assert.Throws<ArgumentException>(() => new ConfluenceDataProvider(api, opts));
}
```

**Step 2: Write failing test for stale delta fallback**

```csharp
[Fact]
public async Task GetFilesAsync_StaleDeltaToken_FallsBackToFullTraversal()
{
    // First call to SearchPagesAsync returns 400, then full traversal via GetPagesAsync
    var ct = TestContext.Current.CancellationToken;
    var handler = new FakeStaleTokenHandler(
        searchResponse: null, // simulate 400
        fullResponse: """{"results":[{"id":"1","title":"Page","body":{"storage":{"value":"body"}},"version":{"number":1}}],"_links":{"next":null}}""");
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IConfluenceApi>(http);
    var opts = new ConfluenceOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", DeltaToken = "2025-01-01T00:00:00Z" };
    var provider = new ConfluenceDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Single(files);
    Assert.Equal("Page.md", files[0].FileName);
}
```

Add `FakeStaleTokenHandler` as test infrastructure:

```csharp
file sealed class FakeStaleTokenHandler(string? searchResponse, string fullResponse)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.PathAndQuery;
        if (url.Contains("/search", StringComparison.Ordinal) && searchResponse is null)
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                { Content = new StringContent("Bad Request") });
        if (url.Contains("/search", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage
                { Content = new StringContent(searchResponse!) });
        return Task.FromResult(new HttpResponseMessage
            { Content = new StringContent(fullResponse) });
    }
}
```

**Step 3: Write failing tests for content rendering edge cases**

```csharp
[Fact]
public async Task GetFilesAsync_NullBodyStorage_YieldsEmptyContent()
{
    // Page with empty body storage value
    var ct = TestContext.Current.CancellationToken;
    var json = """{"results":[{"id":"1","title":"EmptyPage","body":{"storage":{"value":""}},"version":{"number":1}}],"_links":{"next":null}}""";
    var provider = MakeProvider(json);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Single(files);
    var content = await ReadContentAsync(files[0]);
    Assert.Equal("# EmptyPage", content);
}

[Fact]
public async Task GetFilesAsync_HtmlEntitiesInTitle_AreDecoded()
{
    var ct = TestContext.Current.CancellationToken;
    var json = """{"results":[{"id":"1","title":"Q&amp;A Guide","body":{"storage":{"value":"<p>hello</p>"}},"version":{"number":1}}],"_links":{"next":null}}""";
    var provider = MakeProvider(json);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    // Title in filename should use the JSON-deserialized title (already "Q&A Guide" from JSON)
    // but HTML entities in body are decoded by WebUtility.HtmlDecode
    var content = await ReadContentAsync(files[0]);
    Assert.Contains("hello", content);
}
```

**Step 4: Write failing tests for pagination edge cases**

```csharp
[Fact]
public async Task GetFilesAsync_EmptyFirstPage_YieldsNothing()
{
    var ct = TestContext.Current.CancellationToken;
    var json = """{"results":[],"_links":{"next":null}}""";
    var provider = MakeProvider(json);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Empty(files);
}

[Fact]
public async Task GetFilesAsync_CursorWithAmpersand_ExtractsCorrectly()
{
    // Cursor in URL contains trailing &limit=50 — should stop at &
    var ct = TestContext.Current.CancellationToken;
    var handler = new FakeSequentialHandler([
        """{"results":[{"id":"1","title":"P1","body":{"storage":{"value":"b"}},"version":{"number":1}}],"_links":{"next":"/wiki/rest/api/content?cursor=abc123&limit=50"}}""",
        """{"results":[{"id":"2","title":"P2","body":{"storage":{"value":"b"}},"version":{"number":1}}],"_links":{"next":null}}"""
    ]);
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IConfluenceApi>(http);
    var opts = new ConfluenceOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com" };
    var provider = new ConfluenceDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Equal(2, files.Count);
}
```

**Step 5: Write failing test for cancellation**

```csharp
[Fact]
public async Task GetFilesAsync_CancellationRequested_Throws()
{
    var cts = new CancellationTokenSource();
    var json = """{"results":[{"id":"1","title":"P1","body":{"storage":{"value":"b"}},"version":{"number":1}}],"_links":{"next":"/wiki/rest/api/content?cursor=next"}}""";
    var provider = MakeProvider(json);

    cts.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
    {
        await foreach (var _ in provider.GetFilesAsync(cts.Token)) { }
    });
}
```

**Step 6: Write failing test for delta with SpaceKey**

```csharp
[Fact]
public async Task GetFilesAsync_DeltaWithSpaceKey_CqlContainsBoth()
{
    var ct = TestContext.Current.CancellationToken;
    var handler = new FakeCapturingHandler(
        """{"results":[{"id":"1","title":"P","body":{"storage":{"value":"b"}},"version":{"number":1}}],"_links":{"next":null}}""");
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IConfluenceApi>(http);
    var opts = new ConfluenceOptions
    {
        BaseUrl = "https://x.atlassian.net", Email = "a@b.com",
        SpaceKey = "ENG", DeltaToken = "2025-01-01T00:00:00Z"
    };
    var provider = new ConfluenceDataProvider(api, opts);

    await foreach (var _ in provider.GetFilesAsync(ct)) { }

    Assert.Contains("space=", handler.LastRequestUrl);
    Assert.Contains("lastModified>", handler.LastRequestUrl);
}
```

Add `FakeCapturingHandler` if not already present:

```csharp
file sealed class FakeCapturingHandler(string response) : HttpMessageHandler
{
    public string? LastRequestUrl { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUrl = request.RequestUri!.PathAndQuery;
        return Task.FromResult(new HttpResponseMessage
            { Content = new StringContent(response) });
    }
}
```

**Step 7: Add helper methods**

```csharp
private static async Task<string> ReadContentAsync(FileEntry entry)
{
    await using var stream = await entry.OpenContentAsync(CancellationToken.None);
    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync();
}

private static ConfluenceDataProvider MakeProvider(string jsonResponse)
{
    var handler = new FakeHandler(new() { { "content", jsonResponse } });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IConfluenceApi>(http);
    var opts = new ConfluenceOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com" };
    return new ConfluenceDataProvider(api, opts);
}
```

**Step 8: Run tests to verify all pass**

```bash
dotnet test tests/Rag.NET.DataProviders.Confluence.Tests/ -v normal
```

Expected: 13+ tests pass (6 existing + 7+ new).

**Step 9: Commit**

```bash
git add tests/Rag.NET.DataProviders.Confluence.Tests/
git commit -m "test: add comprehensive Confluence connector tests"
```

---

## Task 5: Jira comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Jira.Tests/JiraDataProviderTests.cs`

**Step 1: Write input validation tests**

```csharp
[Fact]
public void Constructor_InvalidDeltaToken_Throws()
{
    var api = RestService.For<IJiraApi>(new HttpClient(new FakeHandler([])) { BaseAddress = new Uri("https://x.atlassian.net") });
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", DeltaToken = "SELECT *; --" };
    Assert.Throws<ArgumentException>(() => new JiraDataProvider(api, opts));
}

[Fact]
public void Constructor_InvalidProjectKey_Throws()
{
    var api = RestService.For<IJiraApi>(new HttpClient(new FakeHandler([])) { BaseAddress = new Uri("https://x.atlassian.net") });
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", ProjectKey = "has spaces" };
    Assert.Throws<ArgumentException>(() => new JiraDataProvider(api, opts));
}
```

**Step 2: Write stale delta fallback test**

```csharp
[Fact]
public async Task GetFilesAsync_StaleDeltaToken_FallsBackToFullTraversal()
{
    var ct = TestContext.Current.CancellationToken;
    var handler = new FakeJiraStaleDeltaHandler("""{"issues":[{"id":"1","key":"PROJ-1","fields":{"summary":"Issue","status":{"name":"Open"},"updated":"2025-01-01T00:00:00Z"}}],"total":1}""");
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IJiraApi>(http);
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", DeltaToken = "2024-01-01T00:00:00Z" };
    var provider = new JiraDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Single(files);
}
```

Add handler:

```csharp
file sealed class FakeJiraStaleDeltaHandler(string fullResponse) : HttpMessageHandler
{
    private bool _firstCall = true;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.PathAndQuery;
        if (_firstCall && url.Contains("updated", StringComparison.Ordinal))
        {
            _firstCall = false;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                { Content = new StringContent("Bad Request") });
        }
        return Task.FromResult(new HttpResponseMessage
            { Content = new StringContent(fullResponse) });
    }
}
```

**Step 3: Write JQL construction tests**

```csharp
[Fact]
public async Task GetFilesAsync_WithProjectKey_JqlContainsProjectFilter()
{
    var ct = TestContext.Current.CancellationToken;
    var handler = new FakeCapturingHandler(
        """{"issues":[{"id":"1","key":"ENG-1","fields":{"summary":"T","status":{"name":"Open"},"updated":"2025-01-01T00:00:00Z"}}],"total":1}""");
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IJiraApi>(http);
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com", ProjectKey = "ENG" };
    var provider = new JiraDataProvider(api, opts);

    await foreach (var _ in provider.GetFilesAsync(ct)) { }

    Assert.Contains("project", handler.LastRequestUrl!);
    Assert.Contains("ENG", handler.LastRequestUrl!);
}
```

**Step 4: Write null field rendering tests**

```csharp
[Fact]
public async Task GetFilesAsync_NullOptionalFields_MarkdownOmitsThem()
{
    var ct = TestContext.Current.CancellationToken;
    var json = """{"issues":[{"id":"1","key":"P-1","fields":{"summary":"Minimal","description":null,"status":{"name":"Open"},"priority":null,"assignee":null,"comment":null,"updated":"2025-01-01T00:00:00Z"}}],"total":1}""";
    var handler = new FakeHandler(new() { { "search", json } });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IJiraApi>(http);
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com" };
    var provider = new JiraDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    var content = await ReadContentAsync(files[0]);
    Assert.Contains("# Minimal", content);
    Assert.Contains("**Status:** Open", content);
    Assert.DoesNotContain("Priority", content);
    Assert.DoesNotContain("Assignee", content);
    Assert.DoesNotContain("Comments", content);
}

[Fact]
public async Task GetFilesAsync_WithComments_MarkdownRendersCommentSection()
{
    var ct = TestContext.Current.CancellationToken;
    var json = """{"issues":[{"id":"1","key":"P-1","fields":{"summary":"WithComments","description":"desc","status":{"name":"Open"},"priority":{"name":"High"},"assignee":{"displayName":"Alice"},"comment":{"comments":[{"author":{"displayName":"Bob"},"created":"2025-01-02","body":"Looks good"}]},"updated":"2025-01-02T00:00:00Z"}}],"total":1}""";
    var handler = new FakeHandler(new() { { "search", json } });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IJiraApi>(http);
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com" };
    var provider = new JiraDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    var content = await ReadContentAsync(files[0]);
    Assert.Contains("## Comments", content);
    Assert.Contains("**Bob**", content);
    Assert.Contains("Looks good", content);
}
```

**Step 5: Write empty results and cancellation tests**

```csharp
[Fact]
public async Task GetFilesAsync_EmptyResults_YieldsNothing()
{
    var ct = TestContext.Current.CancellationToken;
    var json = """{"issues":[],"total":0}""";
    var handler = new FakeHandler(new() { { "search", json } });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IJiraApi>(http);
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com" };
    var provider = new JiraDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Empty(files);
}

[Fact]
public async Task GetFilesAsync_CancellationRequested_Throws()
{
    var cts = new CancellationTokenSource();
    cts.Cancel();
    var json = """{"issues":[{"id":"1","key":"P-1","fields":{"summary":"T","status":{"name":"O"},"updated":"2025-01-01T00:00:00Z"}}],"total":100}""";
    var handler = new FakeHandler(new() { { "search", json } });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://x.atlassian.net") };
    var api = RestService.For<IJiraApi>(http);
    var opts = new JiraOptions { BaseUrl = "https://x.atlassian.net", Email = "a@b.com" };
    var provider = new JiraDataProvider(api, opts);

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
    {
        await foreach (var _ in provider.GetFilesAsync(cts.Token)) { }
    });
}
```

**Step 6: Add helper, run, commit**

```csharp
private static async Task<string> ReadContentAsync(FileEntry entry)
{
    await using var stream = await entry.OpenContentAsync(CancellationToken.None);
    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync();
}
```

```bash
dotnet test tests/Rag.NET.DataProviders.Jira.Tests/ -v normal
git add tests/Rag.NET.DataProviders.Jira.Tests/
git commit -m "test: add comprehensive Jira connector tests"
```

---

## Task 6: Notion comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Notion.Tests/NotionDataProviderTests.cs`

**Step 1: Write block type coverage tests**

```csharp
[Fact]
public async Task GetFilesAsync_AllBlockTypes_MarkdownRendersCorrectly()
{
    var ct = TestContext.Current.CancellationToken;
    var searchJson = """{"results":[{"id":"p1","last_edited_time":"2025-06-01T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"AllBlocks"}]}}}],"has_more":false}""";
    var blocksJson = """{"results":[
        {"type":"heading_1","heading_1":{"rich_text":[{"plain_text":"H1 Title"}]}},
        {"type":"heading_2","heading_2":{"rich_text":[{"plain_text":"H2 Title"}]}},
        {"type":"heading_3","heading_3":{"rich_text":[{"plain_text":"H3 Title"}]}},
        {"type":"paragraph","paragraph":{"rich_text":[{"plain_text":"A paragraph."}]}},
        {"type":"bulleted_list_item","bulleted_list_item":{"rich_text":[{"plain_text":"Bullet item"}]}},
        {"type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"Numbered item"}]}},
        {"type":"code","code":{"rich_text":[{"plain_text":"console.log('hi')"}],"language":"javascript"}},
        {"type":"quote","quote":{"rich_text":[{"plain_text":"A quote"}]}},
        {"type":"divider","divider":{}}
    ],"has_more":false}""";

    var handler = new FakeHandler(new()
    {
        { "/v1/search", searchJson },
        { "/v1/blocks/p1/children", blocksJson }
    });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var provider = new NotionDataProvider(api, new NotionOptions());

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    var content = await ReadContentAsync(files[0]);
    Assert.Contains("# H1 Title", content);
    Assert.Contains("## H2 Title", content);
    Assert.Contains("### H3 Title", content);
    Assert.Contains("A paragraph.", content);
    Assert.Contains("- Bullet item", content);
    Assert.Contains("1. Numbered item", content);
    Assert.Contains("```javascript", content);
    Assert.Contains("console.log('hi')", content);
    Assert.Contains("> A quote", content);
}
```

**Step 2: Write title extraction edge cases**

```csharp
[Fact]
public async Task GetFilesAsync_NoTitleProperty_FallsBackToPageId()
{
    var ct = TestContext.Current.CancellationToken;
    var searchJson = """{"results":[{"id":"page-abc","last_edited_time":"2025-06-01T00:00:00Z","properties":{"Status":{}}}],"has_more":false}""";
    var blocksJson = """{"results":[{"type":"paragraph","paragraph":{"rich_text":[{"plain_text":"text"}]}}],"has_more":false}""";
    var handler = new FakeHandler(new()
    {
        { "/v1/search", searchJson },
        { "/v1/blocks/page-abc/children", blocksJson }
    });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var provider = new NotionDataProvider(api, new NotionOptions());

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Equal("page-abc.md", files[0].FileName);
}

[Fact]
public async Task GetFilesAsync_MultipleRichTexts_Concatenated()
{
    var ct = TestContext.Current.CancellationToken;
    var searchJson = """{"results":[{"id":"p1","last_edited_time":"2025-06-01T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"Hello "},{"plain_text":"World"}]}}}],"has_more":false}""";
    var blocksJson = """{"results":[],"has_more":false}""";
    var handler = new FakeHandler(new()
    {
        { "/v1/search", searchJson },
        { "/v1/blocks/p1/children", blocksJson }
    });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var provider = new NotionDataProvider(api, new NotionOptions());

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Equal("Hello World.md", files[0].FileName);
}
```

**Step 3: Write pagination and delta tests**

```csharp
[Fact]
public async Task GetFilesAsync_SearchPagination_FetchesAllPages()
{
    var ct = TestContext.Current.CancellationToken;
    var page1 = """{"results":[{"id":"p1","last_edited_time":"2025-06-01T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"Page1"}]}}}],"has_more":true,"next_cursor":"cur1"}""";
    var page2 = """{"results":[{"id":"p2","last_edited_time":"2025-06-01T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"Page2"}]}}}],"has_more":false}""";
    var blocks = """{"results":[],"has_more":false}""";

    int searchCallCount = 0;
    var handler = new FakeSearchPaginationHandler(page1, page2, blocks, () => searchCallCount++);
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var provider = new NotionDataProvider(api, new NotionOptions());

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Equal(2, files.Count);
}

[Fact]
public async Task GetFilesAsync_DeltaStopsAtOldPage()
{
    var ct = TestContext.Current.CancellationToken;
    // Two pages: one newer than delta, one older — should only yield the newer one
    var searchJson = """{"results":[
        {"id":"new","last_edited_time":"2025-06-15T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"New"}]}}},
        {"id":"old","last_edited_time":"2025-01-01T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"Old"}]}}}
    ],"has_more":false}""";
    var blocks = """{"results":[],"has_more":false}""";
    var handler = new FakeHandler(new()
    {
        { "/v1/search", searchJson },
        { "/v1/blocks/new/children", blocks }
    });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var opts = new NotionOptions { DeltaToken = "2025-06-01T00:00:00Z" };
    var provider = new NotionDataProvider(api, opts);

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Single(files);
    Assert.Equal("New.md", files[0].FileName);
}
```

**Step 4: Write empty results and cancellation tests**

```csharp
[Fact]
public async Task GetFilesAsync_EmptySearch_YieldsNothing()
{
    var ct = TestContext.Current.CancellationToken;
    var handler = new FakeHandler(new() { { "/v1/search", """{"results":[],"has_more":false}""" } });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var provider = new NotionDataProvider(api, new NotionOptions());

    var files = new List<FileEntry>();
    await foreach (var f in provider.GetFilesAsync(ct))
        files.Add(f);

    Assert.Empty(files);
}

[Fact]
public async Task GetFilesAsync_CancellationRequested_Throws()
{
    var cts = new CancellationTokenSource();
    cts.Cancel();
    var handler = new FakeHandler(new() {
        { "/v1/search", """{"results":[{"id":"p1","last_edited_time":"2025-06-01T00:00:00Z","properties":{"Name":{"title":[{"plain_text":"T"}]}}}],"has_more":false}""" },
        { "/v1/blocks/p1/children", """{"results":[],"has_more":false}""" }
    });
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
    var api = RestService.For<INotionApi>(http);
    var provider = new NotionDataProvider(api, new NotionOptions());

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
    {
        await foreach (var _ in provider.GetFilesAsync(cts.Token)) { }
    });
}
```

**Step 5: Add test infrastructure, run, commit**

Add `FakeSearchPaginationHandler` and `ReadContentAsync` helper. Run and commit:

```bash
dotnet test tests/Rag.NET.DataProviders.Notion.Tests/ -v normal
git add tests/Rag.NET.DataProviders.Notion.Tests/
git commit -m "test: add comprehensive Notion connector tests"
```

---

## Task 7: Asana comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Asana.Tests/AsanaDataProviderTests.cs`

**New tests to add:**
1. `GetFilesAsync_ProjectGidSet_UsesProjectEndpoint` — verify correct API routing
2. `GetFilesAsync_NullOptionalFields_MarkdownOmitsThem` — DueOn, Assignee, Notes all null
3. `GetFilesAsync_WithSubtasks_MarkdownRendersSubtaskSection` — verify subtask markdown
4. `GetFilesAsync_NoSubtasks_MarkdownOmitsSubtaskSection` — empty subtask list
5. `GetFilesAsync_EmptyResults_YieldsNothing` — empty data array
6. `GetFilesAsync_Pagination_FetchesAllPages` — multi-page with offset
7. `GetFilesAsync_NullModifiedAt_ETagIsEmpty` — ETag fallback
8. `GetFilesAsync_CancellationRequested_Throws` — cancellation mid-enumeration

Each test follows the same FakeHandler pattern. ProjectGid test uses a FakeCapturingHandler to verify the URL contains `/projects/{gid}/tasks`.

```bash
dotnet test tests/Rag.NET.DataProviders.Asana.Tests/ -v normal
git add tests/Rag.NET.DataProviders.Asana.Tests/
git commit -m "test: add comprehensive Asana connector tests"
```

---

## Task 8: Slack comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Slack.Tests/SlackDataProviderTests.cs`

**New tests to add:**
1. `GetFilesAsync_ApiReturnsOkFalse_Throws` — channels endpoint returns ok:false
2. `GetFilesAsync_ChannelIdPinned_SkipsChannelListApi` — verify single-channel mode
3. `GetFilesAsync_MultipleChannels_YieldsFilesPerChannel` — 2 channels, each with messages
4. `GetFilesAsync_MultiDayMessages_CreatesMultipleFileHandles` — messages spanning 2 UTC days
5. `GetFilesAsync_EmptyChannel_YieldsNoFiles` — channel with no messages
6. `GetFilesAsync_UserCacheMiss_FallsBackToUserId` — user lookup returns ok:false
7. `GetFilesAsync_MessagesSortedByTimestamp` — verify ascending order in output
8. `GetFilesAsync_CancellationRequested_Throws` — cancellation
9. `GetFilesAsync_NullUserInMessage_ShowsUnknown` — msg.User is null

Each test extends the FakeSlackApi pattern from existing tests.

```bash
dotnet test tests/Rag.NET.DataProviders.Slack.Tests/ -v normal
git add tests/Rag.NET.DataProviders.Slack.Tests/
git commit -m "test: add comprehensive Slack connector tests"
```

---

## Task 9: Microsoft Teams comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/MicrosoftTeamsDataProviderTests.cs`

**New tests to add:**
1. `GetFilesAsync_OnlyTeamIdPinned_FetchesAllChannels` — TeamId set, ChannelId null
2. `GetFilesAsync_NullBodyContent_MessageSkipped` — message with null Body.Content filtered
3. `GetFilesAsync_HtmlInBody_TagsStripped` — verify HTML removal
4. `GetFilesAsync_NullCreatedDateTime_UsesUtcNow` — fallback date grouping
5. `GetFilesAsync_NullAuthor_ShowsUnknown` — From.User.DisplayName null
6. `GetFilesAsync_MultiDayMessages_CreatesMultipleFiles` — messages spanning days
7. `GetFilesAsync_EmptyMessageList_YieldsNothing` — no messages in channel
8. `GetFilesAsync_CancellationRequested_Throws` — cancellation
9. `GetFilesAsync_MessagesSortedByCreatedDateTime` — verify ascending order

Each test uses the existing FakeGraphHandler pattern.

```bash
dotnet test tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/ -v normal
git add tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/
git commit -m "test: add comprehensive Microsoft Teams connector tests"
```

---

## Task 10: Gmail comprehensive tests

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Gmail.Tests/GmailDataProviderTests.cs`

**New tests to add:**
1. `GetFilesAsync_DeltaToken_SearchesUidsAboveWatermark` — verify UID range search
2. `GetFilesAsync_InvalidDeltaToken_FallsBackToAll` — non-parseable token uses SearchQuery.All
3. `GetFilesAsync_MaxResultsLimitsOutput` — 5 UIDs, MaxResults=3, yields 3
4. `GetFilesAsync_NullSubject_FallbackToMessageUid` — filename uses `message-{uid}`
5. `GetFilesAsync_SpecialCharsInSubject_Sanitized` — `/\:*?"<>|` replaced with `_`
6. `GetFilesAsync_HtmlOnlyBody_TagsStripped` — TextBody null, HtmlBody set
7. `GetFilesAsync_BothBodies_PrefersTextBody` — TextBody and HtmlBody both set
8. `GetFilesAsync_NeitherBody_EmptyContent` — both null
9. `GetFilesAsync_CancellationRequested_Throws` — cancellation
10. `GetFilesAsync_MessageMetadata_IncludedInMarkdown` — From, Date, To rendered

Each test extends the existing NSubstitute pattern with `MakeMocks` and `MakeMessage`.

```bash
dotnet test tests/Rag.NET.DataProviders.Gmail.Tests/ -v normal
git add tests/Rag.NET.DataProviders.Gmail.Tests/
git commit -m "test: add comprehensive Gmail connector tests"
```

---

## Task 11: Shared `ConnectorIngestionBenchmarks.cs`

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/ConnectorIngestionBenchmarks.cs`
- Modify: `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj` (add package refs if needed)

**Step 1: Create shared benchmark class**

```csharp
using BenchmarkDotNet.Attributes;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class ConnectorIngestionBenchmarks
{
    // Uses mocked HTTP handlers returning canned JSON with 50 items per connector.
    // Measures GetFilesAsync() enumeration throughput (no pipeline, just provider + stream reads).

    [Params("Confluence", "Jira", "Notion", "Asana", "Slack", "Teams", "Gmail")]
    public string Connector { get; set; } = default!;

    private IFileContentProvider _provider = default!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = Connector switch
        {
            "Confluence" => CreateConfluenceProvider(),
            "Jira"       => CreateJiraProvider(),
            "Notion"     => CreateNotionProvider(),
            "Asana"      => CreateAsanaProvider(),
            "Slack"      => CreateSlackProvider(),
            "Teams"      => CreateTeamsProvider(),
            "Gmail"      => CreateGmailProvider(),
            _            => throw new ArgumentException($"Unknown: {Connector}")
        };
    }

    [Benchmark]
    public async Task<int> EnumerateFiles()
    {
        int count = 0;
        await foreach (var file in _provider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    // Factory methods create providers with FakeHandlers returning 50-item JSON.
    // Implement each using the same patterns from the test suites.
}
```

Each factory method reuses the FakeHandler pattern from tests, returning a single page of 50 items. The Refit clients are created from `HttpClient` with canned JSON. Gmail uses NSubstitute mocks.

**Step 2: Add project references**

Ensure `Rag.NET.Benchmarks.csproj` references all 7 connector projects.

**Step 3: Verify build, commit**

```bash
dotnet build benchmarks/Rag.NET.Benchmarks/
git add benchmarks/
git commit -m "bench: add shared ConnectorIngestionBenchmarks"
```

---

## Task 12: Per-connector benchmark classes

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/ConfluenceBenchmarks.cs`
- Create: `benchmarks/Rag.NET.Benchmarks/JiraBenchmarks.cs`
- Create: `benchmarks/Rag.NET.Benchmarks/NotionBenchmarks.cs`
- Create: `benchmarks/Rag.NET.Benchmarks/AsanaBenchmarks.cs`
- Create: `benchmarks/Rag.NET.Benchmarks/SlackBenchmarks.cs`
- Create: `benchmarks/Rag.NET.Benchmarks/TeamsBenchmarks.cs`
- Create: `benchmarks/Rag.NET.Benchmarks/GmailBenchmarks.cs`

Each file follows this pattern:

```csharp
using BenchmarkDotNet.Attributes;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class ConfluenceBenchmarks
{
    // Connector-specific scenarios:
    // 1. Full traversal (50 pages, single page response)
    // 2. Delta traversal (same 50 pages via search endpoint)
    // 3. HTML stripping overhead (large HTML bodies)

    [Benchmark(Baseline = true)]
    public async Task<int> FullTraversal() { /* ... */ }

    [Benchmark]
    public async Task<int> DeltaTraversal() { /* ... */ }

    [Benchmark]
    public async Task<int> LargeHtmlBodies() { /* ... */ }
}
```

Connector-specific benchmark scenarios:

- **Confluence**: Full vs delta, HTML stripping with large bodies
- **Jira**: Full vs delta, JQL construction overhead, issue with many comments
- **Notion**: Search + block fetch (2 API calls per page), many block types per page
- **Asana**: Task + subtask fetch overhead, large subtask lists
- **Slack**: Day-batching grouping with many messages, thread reply expansion
- **Teams**: Day-batching, HTML stripping overhead
- **Gmail**: IMAP message fetch, TextBody vs HtmlBody selection

```bash
dotnet build benchmarks/Rag.NET.Benchmarks/
git add benchmarks/
git commit -m "bench: add per-connector benchmark classes"
```

---

## Task 13: Update `docs/reference/benchmarks.md`

**Files:**
- Modify: `docs/reference/benchmarks.md`

**Step 1: Add "Data Connectors" section**

Append a new section after the existing content:

```markdown
## Data Connectors

Benchmarks measure `GetFilesAsync()` enumeration throughput with mocked HTTP/IMAP backends (50 items per run). No network I/O — isolates serialization, markdown generation, and grouping overhead.

### Shared Ingestion (50 items)

| Connector        | Mean          | Allocated     |
|-----------------|---------------|---------------|
| Confluence       | TBD           | TBD           |
| Jira             | TBD           | TBD           |
| Notion           | TBD           | TBD           |
| Asana            | TBD           | TBD           |
| Slack            | TBD           | TBD           |
| Microsoft Teams  | TBD           | TBD           |
| Gmail            | TBD           | TBD           |

### Connector-Specific

| Benchmark                         | Mean          | Allocated     |
|----------------------------------|---------------|---------------|
| Confluence: Full vs Delta         | TBD           | TBD           |
| Confluence: Large HTML bodies     | TBD           | TBD           |
| Jira: Full vs Delta              | TBD           | TBD           |
| Notion: Search + block fetch     | TBD           | TBD           |
| Slack: Day-batching grouping     | TBD           | TBD           |
| Slack: Thread reply expansion    | TBD           | TBD           |
| Teams: Day-batching + HTML strip | TBD           | TBD           |
| Gmail: IMAP message fetch        | TBD           | TBD           |

> **Note:** Results marked TBD — run `dotnet run -c Release --project benchmarks/Rag.NET.Benchmarks` to populate.
```

**Step 2: Commit**

```bash
git add docs/reference/benchmarks.md
git commit -m "docs: add data connectors section to benchmarks reference"
```

---

## Task 14: Build + test full solution

**Step 1: Build entire solution**

```bash
dotnet build Rag.NET.slnx
```

Expected: 0 errors, 0 warnings (except known info messages).

**Step 2: Run all tests**

```bash
dotnet test Rag.NET.slnx
```

Expected: All tests pass, including ~85-100 new tests.

**Step 3: Verify benchmark project builds**

```bash
dotnet build benchmarks/Rag.NET.Benchmarks/ -c Release
```

---

## Task 15: Update `docs/reference/features.md` if needed

**Files:**
- Modify: `docs/reference/features.md` (if any status fields need updating)

Check the Group 2 / Group 3 sections. If test/doc/benchmark columns exist, update them. Otherwise skip this task.

**Step 1: Final commit**

```bash
git add -A
git commit -m "chore: connector quality sweep complete — tests, docs, benchmarks"
```

---

## Summary

| Group | Tasks | Parallelizable | Estimated new tests |
|-------|-------|---------------|-------------------|
| Documentation | 1, 2, 3 | Yes (all independent) | — |
| Tests | 4-10 | Yes (one per connector) | ~85-100 |
| Benchmarks | 11, 12, 13 | 11 first, then 12+13 parallel | — |
| Finalization | 14, 15 | Sequential | — |
