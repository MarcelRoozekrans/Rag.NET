# Connector WireMock Tests + ZeroAlloc.Rest Header Migration — Design

**Date:** 2026-04-14

---

## Goal

Two tightly coupled maturity improvements:

1. **Header migration** — move static `DefaultRequestHeaders` entries (`Accept: application/json`, `Notion-Version`) from `*Extensions.cs` into `[Header]` class-level attributes on the ZeroAlloc.Rest interfaces, using the `[Header]` attribute added in ZeroAlloc.Rest 0.2.0.
2. **WireMock integration tests** — add HTTP-level replay tests for all 7 ZeroAlloc.Rest-backed and Graph-backed connectors that currently have no cassette coverage.

These go together: the WireMock tests assert that the expected headers actually arrive on the wire, making them the regression gate for the migration.

---

## Key Decisions

| Question | Decision |
|---|---|
| Which headers move to `[Header]`? | Static-only: `Accept: application/json`, `Notion-Version: 2022-06-28`. Auth (`Authorization: Basic/Bearer`) stays in `*Extensions.cs` — it is computed at setup time from user credentials and must not be interface-hardcoded. |
| Asana token handler | No change — auth flows through `AsanaTokenHandler` (per-request DelegatingHandler). `Accept` moves to `[Header]` on `IAsanaApi`. |
| MicrosoftTeams | Uses Microsoft Graph SDK, not ZeroAlloc.Rest — no header migration. Gets WireMock tests only. |
| WireMock base-URL rewriting | Same as GitHub/Sitemap pattern: `LoadCassettes("Confluence", "https://test.atlassian.net")` rewrites the base URL in cassette stubs to the WireMock port. |
| Header assertion in tests | Each test verifies WireMock received `Accept: application/json`. Notion tests also verify `Notion-Version: 2022-06-28`. |
| Zendesk | Two providers (`ZendeskTicketsDataProvider`, `ZendeskArticlesDataProvider`) share one `IZendeskApi` — one test class covers both. |

---

## Part 1 — Interface changes

### Pattern

```csharp
// Before
[ZeroAllocRestClient]
internal interface IConfluenceApi { ... }

// After
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
internal interface IConfluenceApi { ... }
```

```csharp
// Before
[ZeroAllocRestClient]
internal interface INotionApi { ... }

// After
[ZeroAllocRestClient]
[Header("Accept", Value = "application/json")]
[Header("Notion-Version", Value = "2022-06-28")]
internal interface INotionApi { ... }
```

### Files changed

| File | Change |
|---|---|
| `src/Rag.NET.DataProviders.Confluence/IConfluenceApi.cs` | Add `[Header("Accept", Value = "application/json")]` |
| `src/Rag.NET.DataProviders.Jira/IJiraApi.cs` | Same |
| `src/Rag.NET.DataProviders.Notion/INotionApi.cs` | Add `Accept` + `Notion-Version` headers |
| `src/Rag.NET.DataProviders.Slack/ISlackApi.cs` | Add `Accept` header |
| `src/Rag.NET.DataProviders.Asana/IAsanaApi.cs` | Add `Accept` header |
| `src/Rag.NET.DataProviders.Zendesk/IZendeskApi.cs` | Add `Accept` header |
| `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProviderExtensions.cs` | Remove `Accept.Add(...)` line |
| `src/Rag.NET.DataProviders.Jira/JiraDataProviderExtensions.cs` | Same |
| `src/Rag.NET.DataProviders.Notion/NotionDataProviderExtensions.cs` | Remove `Accept.Add(...)` + `Notion-Version` lines |
| `src/Rag.NET.DataProviders.Slack/SlackDataProviderExtensions.cs` | Remove `Accept.Add(...)` line |
| `src/Rag.NET.DataProviders.Asana/AsanaDataProviderExtensions.cs` | Remove `Accept.Add(...)` line; if `ConfigureHttpClient` block becomes empty, remove it |
| `src/Rag.NET.DataProviders.Zendesk/ZendeskDataProviderExtensions.cs` | Remove `Accept.Add(...)` from both registration methods |

---

## Part 2 — WireMock integration tests

### Location

All new files go in `tests/Rag.NET.DataProviders.IntegrationTests/`:

```
tests/Rag.NET.DataProviders.IntegrationTests/
  Cassettes/
    Confluence/
      get-pages.json
    Jira/
      search-issues.json
    Notion/
      search-pages.json
      get-blocks.json
    Slack/
      list-channels.json
      get-history.json
    Asana/
      get-workspace-tasks.json
    Zendesk/
      get-incremental-tickets.json
      get-ticket-comments.json
      get-incremental-articles.json
    MicrosoftTeams/
      get-joined-teams.json
      get-channels.json
      get-messages.json
  ConfluenceDataProviderTests.cs
  JiraDataProviderTests.cs
  NotionDataProviderTests.cs
  SlackDataProviderTests.cs
  AsanaDataProviderTests.cs
  ZendeskDataProviderTests.cs
  MicrosoftTeamsDataProviderTests.cs
```

### Cassette format

WireMock JSON cassettes follow the established pattern from `Cassettes/GitHub/`:

```json
{
  "Guid": "<unique-guid>",
  "Request": {
    "Path": { "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/wiki/rest/api/content" }] },
    "Methods": [ "GET" ]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{ ... minimal JSON response ... }"
  }
}
```

### Minimal response shapes per connector

**Confluence** (`get-pages.json`): 2 pages with `id`, `title`, `body.storage.value`, `version.number`, `_links: {}`.

**Jira** (`search-issues.json`): 2 issues with `key`, `fields.summary`, `fields.description`, `fields.status.name`, `fields.priority.name`, `fields.updated`, `fields.comment.comments[]`.

**Notion** (`search-pages.json`): 2 results with `id`, `object: "page"`, `properties.title.title[].plain_text`, `last_edited_time`. (`get-blocks.json`): 1 paragraph block with `type: "paragraph"`, `paragraph.rich_text[].plain_text`.

**Slack** (`list-channels.json`): 2 channels with `id`, `name`. (`get-history.json`): 2 messages with `ts`, `text`, `user`, `reply_count: 0`.

**Asana** (`get-workspace-tasks.json`): 2 tasks with `gid`, `name`, `notes`, `completed: false`, `modified_at`. No next page.

**Zendesk** (`get-incremental-tickets.json`): 2 tickets with `id`, `subject`, `description`, `status`, `updated_at`, `end_of_stream: true`. (`get-ticket-comments.json`): 1 comment with `id`, `body`, `public: true`. (`get-incremental-articles.json`): 2 articles with `id`, `title`, `body`, `updated_at`, `end_of_stream: true`.

**MicrosoftTeams** (`get-joined-teams.json`): 1 team with `id`, `displayName`. (`get-channels.json`): 1 channel. (`get-messages.json`): 2 messages with `id`, `body.content`, `body.contentType: "text"`, `createdDateTime`.

### Test class structure (same pattern as GitHubDataProviderTests)

```csharp
[Collection("WireMock")]
public sealed class ConfluenceDataProviderTests
{
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
        var api = new ConfluenceApiClient(http, new SystemTextJsonSerializer());
        return new ConfluenceDataProvider(api, opts ?? new ConfluenceOptions
        {
            BaseUrl = _fixture.BaseUrl,
            Email   = "test@test.com",
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsPages() { ... }

    [Fact]
    public async Task GetFilesAsync_ReturnsAcceptHeader() 
    {
        // Assert WireMock received Accept: application/json
        var entries = await CreateProvider()
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var requests = _fixture.Server.LogEntries;
        Assert.All(requests, r =>
            Assert.Contains("application/json",
                r.RequestMessage.Headers.GetValueOrDefault("Accept", [])));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesCqlSearchEndpoint() { /* inline stub */ }
}
```

### Tests per connector

| Connector | Tests |
|---|---|
| Confluence | `FullTraversal_YieldsPages`, `FullTraversal_AcceptsJsonHeader`, `FullTraversal_EachFileHasETag`, `DeltaRun_UsesCqlSearchEndpoint` |
| Jira | `FullTraversal_YieldsIssues`, `AcceptsJsonHeader`, `DeltaRun_UsesUpdatedFilter` |
| Notion | `Search_YieldsPages`, `AcceptsJsonHeader`, `NotionVersionHeaderSent`, `GetBlocks_ReturnsContent` |
| Slack | `ListChannels_YieldsChannels`, `AcceptsJsonHeader`, `GetHistory_YieldsMessages`, `DeltaRun_UsesOldestParam` |
| Asana | `GetTasks_YieldsTasks`, `AcceptsJsonHeader`, `DeltaRun_UsesModifiedSince` |
| Zendesk | `GetTickets_YieldsTickets`, `AcceptsJsonHeader`, `GetArticles_YieldsArticles`, `IncrementalCursor_Followed` |
| MicrosoftTeams | `GetMessages_YieldsMessages`, `FullTraversal_ReturnsNonEmptyStream` |

---

## What this does NOT change

- Auth header setup in `*Extensions.cs` — stays as `DefaultRequestHeaders.Authorization`
- `AsanaTokenHandler` — unchanged
- Any provider behaviour or options
- Unit tests in `tests/Rag.NET.DataProviders.*.Tests/` — these stay as-is

---

## Testing the migration

After Part 1 changes build cleanly, the WireMock tests in Part 2 serve as the proof: if the `[Header]` attribute doesn't emit the header, the `AcceptsJsonHeader` / `NotionVersionHeaderSent` assertions fail.
