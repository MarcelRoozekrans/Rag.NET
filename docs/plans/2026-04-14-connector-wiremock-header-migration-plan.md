# Connector Header Migration + WireMock Integration Tests — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Move static `Accept: application/json` (and `Notion-Version`) headers from `DefaultRequestHeaders` in `*Extensions.cs` onto `[Header]` class attributes on the ZeroAlloc.Rest interfaces, then add WireMock integration tests for all 7 connectors (Confluence, Jira, Notion, Slack, Asana, Zendesk, MicrosoftTeams) that assert those headers actually arrive on the wire.

**Architecture:** The `[Header("Accept", Value = "application/json")]` attribute is added at the interface level — the ZeroAlloc.Rest source generator then emits the header on every call. Auth headers (`Authorization: Basic/Bearer`) stay in `*Extensions.cs` because they are computed at registration time from user credentials. WireMock tests point a real `*ApiClient` at the WireMock server instead of the real API; cassette JSON files replay static responses and `LogEntries` assertions verify headers.

**Tech Stack:** ZeroAlloc.Rest 0.2.0 (`[Header]` attribute), WireMock.Net 1.x, `ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer`, xunit.v3, `Rag.NET.Testing.WireMockServerFixture`.

---

## Context for the implementer

### Project layout

```
src/
  Rag.NET.DataProviders.Confluence/   IConfluenceApi.cs, ConfluenceDataProviderExtensions.cs
  Rag.NET.DataProviders.Jira/         IJiraApi.cs, JiraDataProviderExtensions.cs
  Rag.NET.DataProviders.Notion/       INotionApi.cs, NotionDataProviderExtensions.cs
  Rag.NET.DataProviders.Slack/        ISlackApi.cs, SlackDataProviderExtensions.cs
  Rag.NET.DataProviders.Asana/        IAsanaApi.cs, AsanaDataProviderExtensions.cs
  Rag.NET.DataProviders.Zendesk/      IZendeskApi.cs, ZendeskDataProviderExtensions.cs
tests/
  Rag.NET.DataProviders.IntegrationTests/
    Cassettes/GitHub/  ← existing cassettes, use as format template
    GitHubDataProviderTests.cs        ← existing test, use as code template
    WireMockCollection.cs             ← already exists, no changes needed
    Rag.NET.DataProviders.IntegrationTests.csproj  ← needs new ProjectReferences
  Rag.NET.Testing/
    WireMockServerFixture.cs          ← call LoadCassettes("ConnectorName", baseUrl)
```

### Cassette file format (copy this pattern exactly)

```json
{
  "Guid": "<unique-uuid>",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/path", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{ ... }"
  }
}
```

### How to instantiate API clients in tests

```csharp
using ZeroAlloc.Rest.SystemTextJson;
private static readonly IRestSerializer JsonSerializer = new SystemTextJsonSerializer();

// Point the HttpClient at WireMock instead of the real API
var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
var api  = new ConfluenceApiClient(http, JsonSerializer);
// Same pattern: JiraApiClient, NotionApiClient, SlackApiClient, AsanaApiClient, ZendeskApiClient
```

### How to assert headers in WireMock

```csharp
var requests = _fixture.Server.LogEntries.ToList();
Assert.All(requests, r =>
    Assert.Contains("application/json",
        r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
```

### WireMock LogEntries reset

`_fixture.LoadCassettes(...)` calls `Server.ResetMappings()` but does NOT reset log entries. To avoid cross-test contamination, reset log entries explicitly before each test that checks them:

```csharp
_fixture.Server.ResetLogEntries();
```

### Build and test commands

```bash
# Build everything
dotnet build Rag.NET.slnx

# Run just the integration tests
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/

# Run a single test class
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~ConfluenceDataProviderTests"
```

---

## Task 1: Add `[Header]` attributes to all 6 ZeroAlloc.Rest interfaces

**Files:**
- Modify: `src/Rag.NET.DataProviders.Confluence/IConfluenceApi.cs`
- Modify: `src/Rag.NET.DataProviders.Jira/IJiraApi.cs`
- Modify: `src/Rag.NET.DataProviders.Notion/INotionApi.cs`
- Modify: `src/Rag.NET.DataProviders.Slack/ISlackApi.cs`
- Modify: `src/Rag.NET.DataProviders.Asana/IAsanaApi.cs`
- Modify: `src/Rag.NET.DataProviders.Zendesk/IZendeskApi.cs`

**Step 1: Add `[Header]` to IConfluenceApi**

Replace the top of `src/Rag.NET.DataProviders.Confluence/IConfluenceApi.cs`:

```csharp
using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Confluence;

[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
internal interface IConfluenceApi
{
    // ... existing methods unchanged ...
}
```

**Step 2: Add `[Header]` to IJiraApi**

```csharp
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
internal interface IJiraApi
```

**Step 3: Add `[Header]` to INotionApi** (two headers)

```csharp
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
[Header("Notion-Version", Value = "2022-06-28")]
internal interface INotionApi
```

**Step 4: Add `[Header]` to ISlackApi**

```csharp
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
public interface ISlackApi
```

**Step 5: Add `[Header]` to IAsanaApi**

```csharp
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
internal interface IAsanaApi
```

**Step 6: Add `[Header]` to IZendeskApi**

```csharp
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
internal interface IZendeskApi
```

**Step 7: Build to confirm generator accepts the new attributes**

```bash
dotnet build Rag.NET.slnx
```

Expected: build succeeds, 0 errors.

**Step 8: Commit**

```bash
git add src/Rag.NET.DataProviders.Confluence/IConfluenceApi.cs \
        src/Rag.NET.DataProviders.Jira/IJiraApi.cs \
        src/Rag.NET.DataProviders.Notion/INotionApi.cs \
        src/Rag.NET.DataProviders.Slack/ISlackApi.cs \
        src/Rag.NET.DataProviders.Asana/IAsanaApi.cs \
        src/Rag.NET.DataProviders.Zendesk/IZendeskApi.cs
git commit -m "feat(connectors): move Accept/Notion-Version headers to [Header] interface attributes"
```

---

## Task 2: Remove Accept from `*Extensions.cs` ConfigureHttpClient blocks

**Files:**
- Modify: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Jira/JiraDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Notion/NotionDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Slack/SlackDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Asana/AsanaDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Zendesk/ZendeskDataProviderExtensions.cs`

**Step 1: Remove Accept line from Confluence, Jira, Slack**

In `ConfluenceDataProviderExtensions.cs`, `JiraDataProviderExtensions.cs`, and `SlackDataProviderExtensions.cs`:

Remove these two lines from each `ConfigureHttpClient` block:
```csharp
client.DefaultRequestHeaders.Accept.Add(
    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
```

The `Authorization` line stays. The `using System.Net.Http.Headers;` stays (still needed for `AuthenticationHeaderValue`).

**Step 2: Remove Accept + Notion-Version from Notion**

In `NotionDataProviderExtensions.cs`, remove:
```csharp
client.DefaultRequestHeaders.Accept.Add(
    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
client.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
```

The `Authorization` line stays.

**Step 3: Remove Accept from Asana**

In `AsanaDataProviderExtensions.cs`, the `ConfigureHttpClient` block currently contains only the `Accept.Add` line:

```csharp
.ConfigureHttpClient(client =>
{
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
})
```

Remove the entire `.ConfigureHttpClient(...)` call. Also remove `using System.Net.Http.Headers;` from the top of the file if it's no longer used.

**Step 4: Remove Accept from both Zendesk registration methods**

`ZendeskDataProviderExtensions.cs` has two separate registration methods (`AddZendeskTicketsDataProvider` and `AddZendeskArticlesDataProvider`), each with their own `ConfigureHttpClient` block. Remove the `Accept.Add` line from both. Auth lines stay.

**Step 5: Build**

```bash
dotnet build Rag.NET.slnx
```

Expected: 0 errors, 0 warnings about unused usings.

**Step 6: Run unit tests to verify nothing regressed**

```bash
dotnet test tests/Rag.NET.DataProviders.Confluence.Tests/ tests/Rag.NET.DataProviders.Jira.Tests/ tests/Rag.NET.DataProviders.Notion.Tests/ tests/Rag.NET.DataProviders.Slack.Tests/ tests/Rag.NET.DataProviders.Asana.Tests/ tests/Rag.NET.DataProviders.Zendesk.Tests/
```

Expected: all tests pass (these tests use `FakeHandler` directly, not WireMock).

**Step 7: Commit**

```bash
git add src/Rag.NET.DataProviders.Confluence/ConfluenceDataProviderExtensions.cs \
        src/Rag.NET.DataProviders.Jira/JiraDataProviderExtensions.cs \
        src/Rag.NET.DataProviders.Notion/NotionDataProviderExtensions.cs \
        src/Rag.NET.DataProviders.Slack/SlackDataProviderExtensions.cs \
        src/Rag.NET.DataProviders.Asana/AsanaDataProviderExtensions.cs \
        src/Rag.NET.DataProviders.Zendesk/ZendeskDataProviderExtensions.cs
git commit -m "refactor(connectors): remove Accept header from DefaultRequestHeaders (now on interface)"
```

---

## Task 3: Add Confluence WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Confluence/get-pages.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/ConfluenceDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add Confluence ProjectReference to csproj**

In `Rag.NET.DataProviders.IntegrationTests.csproj`, add inside `<ItemGroup>`:

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.Confluence\Rag.NET.DataProviders.Confluence.csproj" />
```

Also add the ZeroAlloc serializer package if not present:
```xml
<PackageReference Include="ZeroAlloc.Rest.SystemTextJson" Version="0.*" />
```

**Step 2: Create the cassette file**

Create `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Confluence/get-pages.json`:

```json
{
  "Guid": "c1000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/wiki/rest/api/content", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"results\":[{\"id\":\"111\",\"title\":\"Getting Started\",\"body\":{\"storage\":{\"value\":\"<p>Welcome to Confluence.</p>\"}},\"version\":{\"number\":2}},{\"id\":\"222\",\"title\":\"Architecture Guide\",\"body\":{\"storage\":{\"value\":\"<p>System overview.</p>\"}},\"version\":{\"number\":5}}],\"_links\":{}}"
  }
}
```

**Step 3: Write the test class**

Create `tests/Rag.NET.DataProviders.IntegrationTests/ConfluenceDataProviderTests.cs`:

```csharp
using System.Net.Http.Headers;
using Rag.NET.DataProviders.Confluence;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class ConfluenceDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public ConfluenceDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Confluence", "https://test.atlassian.net");
    }

    private ConfluenceDataProvider CreateProvider(ConfluenceOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        var api = new ConfluenceApiClient(http, JsonSerializer);
        return new ConfluenceDataProvider(api, opts ?? new ConfluenceOptions
        {
            BaseUrl = _fixture.BaseUrl,
            Email   = "test@test.com",
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsPages()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_EachFileHasETag()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e => Assert.NotEmpty(e.Value.ETag!));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesCqlSearchEndpoint()
    {
        _fixture.Server
            .Given(Request.Create().WithPath("/wiki/rest/api/content/search").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody("{\"results\":[{\"id\":\"333\",\"title\":\"Changed\",\"body\":{\"storage\":{\"value\":\"<p>Updated.</p>\"}},\"version\":{\"number\":3}}],\"_links\":{}}"));

        var opts = new ConfluenceOptions
        {
            BaseUrl    = _fixture.BaseUrl,
            Email      = "test@test.com",
            DeltaToken = "2026-01-01T00:00:00Z",
        };

        var entries = await CreateProvider(opts)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("Changed.md", entries[0].Value.FileName);
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~ConfluenceDataProviderTests"
```

Expected: 4 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Confluence/ \
        tests/Rag.NET.DataProviders.IntegrationTests/ConfluenceDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(confluence): add WireMock integration tests including Accept-header assertion"
```

---

## Task 4: Add Jira WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Jira/search-issues.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/JiraDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add Jira ProjectReference**

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.Jira\Rag.NET.DataProviders.Jira.csproj" />
```

**Step 2: Create cassette**

Create `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Jira/search-issues.json`:

```json
{
  "Guid": "c2000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/rest/api/3/search", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"issues\":[{\"key\":\"PROJ-1\",\"fields\":{\"summary\":\"Fix login bug\",\"description\":\"Users cannot log in.\",\"status\":{\"name\":\"Open\"},\"priority\":{\"name\":\"High\"},\"assignee\":null,\"updated\":\"2026-01-10T12:00:00.000+0000\",\"comment\":{\"comments\":[]}}},{\"key\":\"PROJ-2\",\"fields\":{\"summary\":\"Add dark mode\",\"description\":\"Feature request.\",\"status\":{\"name\":\"In Progress\"},\"priority\":{\"name\":\"Medium\"},\"assignee\":null,\"updated\":\"2026-01-11T09:00:00.000+0000\",\"comment\":{\"comments\":[]}}}],\"total\":2}"
  }
}
```

**Step 3: Write the test class**

Create `tests/Rag.NET.DataProviders.IntegrationTests/JiraDataProviderTests.cs`:

```csharp
using System.Net.Http.Headers;
using Rag.NET.DataProviders.Jira;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class JiraDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public JiraDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Jira", "https://test.atlassian.net");
    }

    private JiraDataProvider CreateProvider(JiraOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        var api = new JiraApiClient(http, JsonSerializer);
        return new JiraDataProvider(api, opts ?? new JiraOptions
        {
            BaseUrl = _fixture.BaseUrl,
            Email   = "test@test.com",
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsIssues()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetFilesAsync_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesUpdatedFilter()
    {
        // Delta runs append `updated >= "deltaToken"` to the JQL query.
        // The cassette /rest/api/3/search stub matches any GET to that path,
        // so it will respond. We just verify the query string contains "updated".
        var opts = new JiraOptions
        {
            BaseUrl    = _fixture.BaseUrl,
            Email      = "test@test.com",
            DeltaToken = "2026-01-01T00:00:00",
        };

        var entries = await CreateProvider(opts)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The stub returns both issues regardless of query; we just verify it ran.
        Assert.NotEmpty(entries);

        // Verify the JQL contained the delta filter.
        var request = _fixture.Server.LogEntries
            .First(e => e.RequestMessage.Path.Contains("/rest/api/3/search", StringComparison.Ordinal));
        Assert.Contains("updated", request.RequestMessage.Query!["jql"]![0], StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~JiraDataProviderTests"
```

Expected: 3 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Jira/ \
        tests/Rag.NET.DataProviders.IntegrationTests/JiraDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(jira): add WireMock integration tests including Accept-header assertion"
```

---

## Task 5: Add Notion WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Notion/search-pages.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Notion/get-blocks.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/NotionDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add Notion ProjectReference**

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.Notion\Rag.NET.DataProviders.Notion.csproj" />
```

**Step 2: Create cassettes**

`Cassettes/Notion/search-pages.json` — matches POST `/v1/search`:

```json
{
  "Guid": "c3000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/v1/search", "IgnoreCase": false }]
    },
    "Methods": [ "POST" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"results\":[{\"id\":\"page-aaa\",\"object\":\"page\",\"last_edited_time\":\"2026-01-15T10:00:00.000Z\",\"properties\":{\"title\":{\"title\":[{\"plain_text\":\"Team Wiki\"}]}}},{\"id\":\"page-bbb\",\"object\":\"page\",\"last_edited_time\":\"2026-01-16T10:00:00.000Z\",\"properties\":{\"title\":{\"title\":[{\"plain_text\":\"Onboarding Guide\"}]}}}],\"has_more\":false}"
  }
}
```

`Cassettes/Notion/get-blocks.json` — matches GET `/v1/blocks/{id}/children`:

```json
{
  "Guid": "c3000002-0000-0000-0000-000000000002",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/v1/blocks/*/children", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"results\":[{\"type\":\"paragraph\",\"paragraph\":{\"rich_text\":[{\"plain_text\":\"Welcome to the team.\"}]}}],\"has_more\":false}"
  }
}
```

**Step 3: Write test class**

Create `tests/Rag.NET.DataProviders.IntegrationTests/NotionDataProviderTests.cs`:

```csharp
using System.Net.Http.Headers;
using Rag.NET.DataProviders.Notion;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class NotionDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public NotionDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Notion", "https://api.notion.com");
    }

    private NotionDataProvider CreateProvider(NotionOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "secret_test");
        var api = new NotionApiClient(http, JsonSerializer);
        return new NotionDataProvider(api, opts ?? new NotionOptions());
    }

    [Fact]
    public async Task GetFilesAsync_Search_YieldsPages()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetFilesAsync_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetFilesAsync_NotionVersionHeaderSent()
    {
        _fixture.Server.ResetLogEntries();

        await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("2022-06-28",
                r.RequestMessage.Headers.GetValueOrDefault("Notion-Version", [])));
    }

    [Fact]
    public async Task GetFilesAsync_EachEntry_HasETag()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e => Assert.NotEmpty(e.Value.ETag!));
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~NotionDataProviderTests"
```

Expected: 4 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Notion/ \
        tests/Rag.NET.DataProviders.IntegrationTests/NotionDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(notion): add WireMock integration tests including Accept + Notion-Version header assertions"
```

---

## Task 6: Add Slack WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Slack/list-channels.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Slack/get-history.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/SlackDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add Slack ProjectReference**

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.Slack\Rag.NET.DataProviders.Slack.csproj" />
```

**Step 2: Create cassettes**

`Cassettes/Slack/list-channels.json`:

```json
{
  "Guid": "c4000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/conversations.list", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"ok\":true,\"channels\":[{\"id\":\"C001\",\"name\":\"general\"},{\"id\":\"C002\",\"name\":\"engineering\"}],\"response_metadata\":{\"next_cursor\":\"\"}}"
  }
}
```

`Cassettes/Slack/get-history.json`:

```json
{
  "Guid": "c4000002-0000-0000-0000-000000000002",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/conversations.history", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"ok\":true,\"messages\":[{\"ts\":\"1700000001.000001\",\"text\":\"Hello team!\",\"user\":\"U001\",\"reply_count\":0},{\"ts\":\"1700000002.000002\",\"text\":\"Standup at 9am\",\"user\":\"U002\",\"reply_count\":0}],\"has_more\":false}"
  }
}
```

**Step 3: Write test class**

Create `tests/Rag.NET.DataProviders.IntegrationTests/SlackDataProviderTests.cs`:

```csharp
using System.Net.Http.Headers;
using Rag.NET.DataProviders.Slack;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class SlackDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public SlackDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Slack", "https://slack.com");
    }

    private SlackDataProvider CreateProvider(SlackOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "xoxb-test");
        var api = new SlackApiClient(http, JsonSerializer);
        return new SlackDataProvider(api, opts ?? new SlackOptions());
    }

    [Fact]
    public async Task GetFilesAsync_ListChannels_YieldsMessages()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetFilesAsync_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesOldestParam()
    {
        var opts = new SlackOptions { DeltaToken = "1700000000.000000" };

        var entries = await CreateProvider(opts)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(entries);

        // Verify the history request included the oldest= param
        var historyReq = _fixture.Server.LogEntries
            .First(e => e.RequestMessage.Path.Contains("/api/conversations.history", StringComparison.Ordinal));
        Assert.NotNull(historyReq.RequestMessage.Query!.GetValueOrDefault("oldest"));
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~SlackDataProviderTests"
```

Expected: 3 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Slack/ \
        tests/Rag.NET.DataProviders.IntegrationTests/SlackDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(slack): add WireMock integration tests including Accept-header assertion"
```

---

## Task 7: Add Asana WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Asana/get-workspace-tasks.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/AsanaDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add Asana ProjectReference**

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.Asana\Rag.NET.DataProviders.Asana.csproj" />
```

**Step 2: Create cassette**

`Cassettes/Asana/get-workspace-tasks.json`:

```json
{
  "Guid": "c5000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/1.0/tasks", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"data\":[{\"gid\":\"task-001\",\"name\":\"Write tests\",\"notes\":\"Add integration tests for connectors.\",\"completed\":false,\"modified_at\":\"2026-01-10T08:00:00.000Z\",\"subtasks\":[]},{\"gid\":\"task-002\",\"name\":\"Review PR\",\"notes\":\"Review the open pull request.\",\"completed\":false,\"modified_at\":\"2026-01-11T09:00:00.000Z\",\"subtasks\":[]}],\"next_page\":null}"
  }
}
```

**Step 3: Write test class**

Asana uses `ITokenProvider` for auth (not `DefaultRequestHeaders`). For the integration test, inject a fake token directly via `DefaultRequestHeaders` after constructing `HttpClient` — the `AsanaApiClient` doesn't know the difference.

Create `tests/Rag.NET.DataProviders.IntegrationTests/AsanaDataProviderTests.cs`:

```csharp
using System.Net.Http.Headers;
using Rag.NET.DataProviders.Asana;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class AsanaDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public AsanaDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Asana", "https://app.asana.com");
    }

    private AsanaDataProvider CreateProvider(AsanaOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");
        var api = new AsanaApiClient(http, JsonSerializer);
        return new AsanaDataProvider(api, opts ?? new AsanaOptions { WorkspaceGid = "ws-001" });
    }

    [Fact]
    public async Task GetFilesAsync_GetTasks_YieldsTasks()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetFilesAsync_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesModifiedSince()
    {
        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-001",
            DeltaToken   = "2026-01-01T00:00:00Z",
        };

        await CreateProvider(opts)
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var req = _fixture.Server.LogEntries
            .First(e => e.RequestMessage.Path.Contains("/api/1.0/tasks", StringComparison.Ordinal));
        Assert.NotNull(req.RequestMessage.Query!.GetValueOrDefault("modified_since"));
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~AsanaDataProviderTests"
```

Expected: 3 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Asana/ \
        tests/Rag.NET.DataProviders.IntegrationTests/AsanaDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(asana): add WireMock integration tests including Accept-header assertion"
```

---

## Task 8: Add Zendesk WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Zendesk/get-incremental-tickets.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Zendesk/get-ticket-comments.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Zendesk/get-incremental-articles.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/ZendeskDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add Zendesk ProjectReference**

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.Zendesk\Rag.NET.DataProviders.Zendesk.csproj" />
```

**Step 2: Create cassettes**

`Cassettes/Zendesk/get-incremental-tickets.json`:

```json
{
  "Guid": "c6000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/v2/incremental/tickets/cursor.json", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"tickets\":[{\"id\":1001,\"subject\":\"Can't login\",\"description\":\"Login fails.\",\"status\":\"open\",\"updated_at\":\"2026-01-10T10:00:00Z\"},{\"id\":1002,\"subject\":\"Slow load\",\"description\":\"Dashboard is slow.\",\"status\":\"pending\",\"updated_at\":\"2026-01-11T10:00:00Z\"}],\"end_of_stream\":true,\"after_cursor\":null}"
  }
}
```

`Cassettes/Zendesk/get-ticket-comments.json`:

```json
{
  "Guid": "c6000002-0000-0000-0000-000000000002",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/v2/tickets/*/comments", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"comments\":[{\"id\":9001,\"body\":\"Please reset my password.\",\"public\":true}]}"
  }
}
```

`Cassettes/Zendesk/get-incremental-articles.json`:

```json
{
  "Guid": "c6000003-0000-0000-0000-000000000003",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/api/v2/help_center/incremental/articles.json", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"articles\":[{\"id\":2001,\"title\":\"How to reset password\",\"body\":\"<p>Go to settings.</p>\",\"updated_at\":\"2026-01-10T10:00:00Z\"},{\"id\":2002,\"title\":\"Billing FAQ\",\"body\":\"<p>See billing page.</p>\",\"updated_at\":\"2026-01-11T10:00:00Z\"}],\"end_of_stream\":true,\"next_page\":null}"
  }
}
```

**Step 3: Write test class**

Create `tests/Rag.NET.DataProviders.IntegrationTests/ZendeskDataProviderTests.cs`:

```csharp
using System.Net.Http.Headers;
using Rag.NET.DataProviders.Zendesk;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class ZendeskDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public ZendeskDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Zendesk", "https://test.zendesk.com");
    }

    private ZendeskApiClient CreateApiClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        return new ZendeskApiClient(http, JsonSerializer);
    }

    [Fact]
    public async Task GetTickets_YieldsTickets()
    {
        var provider = new ZendeskTicketsDataProvider(
            CreateApiClient(),
            new ZendeskTicketsOptions { Subdomain = "test", Email = "a@b.com" });

        var entries = await provider
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetTickets_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        var provider = new ZendeskTicketsDataProvider(
            CreateApiClient(),
            new ZendeskTicketsOptions { Subdomain = "test", Email = "a@b.com" });

        await provider
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetArticles_YieldsArticles()
    {
        var provider = new ZendeskArticlesDataProvider(
            CreateApiClient(),
            new ZendeskArticlesOptions { Subdomain = "test", Email = "a@b.com" });

        var entries = await provider
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~ZendeskDataProviderTests"
```

Expected: 3 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Zendesk/ \
        tests/Rag.NET.DataProviders.IntegrationTests/ZendeskDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(zendesk): add WireMock integration tests for tickets and articles including Accept-header assertion"
```

---

## Task 9: Add MicrosoftTeams WireMock integration test

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/MicrosoftTeams/get-joined-teams.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/MicrosoftTeams/get-channels.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/MicrosoftTeams/get-messages.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/MicrosoftTeamsDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Step 1: Add MicrosoftTeams ProjectReference**

```xml
<ProjectReference Include="..\..\src\Rag.NET.DataProviders.MicrosoftTeams\Rag.NET.DataProviders.MicrosoftTeams.csproj" />
```

Also add the Microsoft Graph package (already a transitive dep of MicrosoftTeams connector, but the test project needs it directly):

```xml
<PackageReference Include="Microsoft.Graph" Version="5.*" />
```

**Step 2: Create cassettes**

MicrosoftTeams uses the Graph SDK which always calls `https://graph.microsoft.com/v1.0/...`. The WireMock server rewrites the base URL so the cassette paths must match the Graph API paths.

`Cassettes/MicrosoftTeams/get-joined-teams.json`:

```json
{
  "Guid": "c7000001-0000-0000-0000-000000000001",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/v1.0/me/joinedTeams", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#teams\",\"value\":[{\"id\":\"team-1\",\"displayName\":\"Engineering\"}]}"
  }
}
```

`Cassettes/MicrosoftTeams/get-channels.json`:

```json
{
  "Guid": "c7000002-0000-0000-0000-000000000002",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/v1.0/teams/team-1/channels", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"value\":[{\"id\":\"chan-1\",\"displayName\":\"general\"}]}"
  }
}
```

`Cassettes/MicrosoftTeams/get-messages.json`:

```json
{
  "Guid": "c7000003-0000-0000-0000-000000000003",
  "Request": {
    "Path": {
      "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/v1.0/teams/team-1/channels/chan-1/messages", "IgnoreCase": false }]
    },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"value\":[{\"id\":\"msg-1\",\"createdDateTime\":\"2026-01-10T10:00:00Z\",\"lastModifiedDateTime\":\"2026-01-10T10:00:00Z\",\"from\":{\"user\":{\"displayName\":\"Alice\"}},\"body\":{\"content\":\"Hello team!\",\"contentType\":\"text\"}},{\"id\":\"msg-2\",\"createdDateTime\":\"2026-01-10T10:05:00Z\",\"lastModifiedDateTime\":\"2026-01-10T10:05:00Z\",\"from\":{\"user\":{\"displayName\":\"Bob\"}},\"body\":{\"content\":\"Standup at 9am\",\"contentType\":\"text\"}}]}"
  }
}
```

**Step 3: Write test class**

MicrosoftTeams uses `GraphServiceClient` with `ClientSecretCredential`. For the WireMock test, inject a `GraphServiceClient` pointed at WireMock using a no-auth `HttpClient` (same pattern as the existing unit test's `FakeGraphHandler` but using WireMock instead):

Create `tests/Rag.NET.DataProviders.IntegrationTests/MicrosoftTeamsDataProviderTests.cs`:

```csharp
using Microsoft.Graph;
using Rag.NET.DataProviders.MicrosoftTeams;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class MicrosoftTeamsDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public MicrosoftTeamsDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("MicrosoftTeams", "https://graph.microsoft.com");
    }

    private MicrosoftTeamsDataProvider CreateProvider(MicrosoftTeamsOptions? opts = null)
    {
        // Point the Graph SDK at the WireMock server instead of graph.microsoft.com.
        // No auth needed — WireMock accepts all requests.
        var http  = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl + "/") };
        var graph = new GraphServiceClient(http);
        return new MicrosoftTeamsDataProvider(graph, opts ?? new MicrosoftTeamsOptions());
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsMessages()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.IsSuccess));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_ContentIsNonEmpty()
    {
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var entry in entries)
        {
            await using var stream = await entry.Value.OpenContentAsync(TestContext.Current.CancellationToken);
            Assert.True(stream.Length > 0);
        }
    }
}
```

**Step 4: Build and run**

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~MicrosoftTeamsDataProviderTests"
```

Expected: 2 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/MicrosoftTeams/ \
        tests/Rag.NET.DataProviders.IntegrationTests/MicrosoftTeamsDataProviderTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test(teams): add WireMock integration tests for MicrosoftTeams connector"
```

---

## Task 10: Full test run and status line in plan

**Step 1: Run all integration tests**

```bash
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/
```

Expected: all tests pass (7 test classes, ~24 tests total).

**Step 2: Run full solution build and test**

```bash
dotnet build Rag.NET.slnx && dotnet test Rag.NET.slnx --no-build
```

Expected: all tests pass.

**Step 3: Update plan status**

Add `**Status:** ✅ Done` at the top of this plan file, under the header.

**Step 4: Final commit**

```bash
git add docs/plans/2026-04-14-connector-wiremock-header-migration-plan.md
git commit -m "docs: mark connector header migration + WireMock tests plan as done"
```
