# Collaboration & Communication Connectors Design

**Date:** 2026-03-28

## Overview

Add seven connectors covering Group 2 (Collaboration) and Group 3 (Communication) from the SaaS connectors backlog. Each connector exposes `IFileContentProvider`, serializes its native content to markdown, and stores structured fields in `DocumentMetadata`. Airtable is excluded (its row-per-entry model requires a separate design pass).

**Group 2 — Collaboration:** Confluence, Notion, Jira, Asana
**Group 3 — Communication:** Slack, Microsoft Teams, Gmail

---

## Package Structure

```
src/
  Rag.NET.DataProviders.Confluence/
  Rag.NET.DataProviders.Notion/
  Rag.NET.DataProviders.Jira/
  Rag.NET.DataProviders.Asana/
  Rag.NET.DataProviders.Slack/
  Rag.NET.DataProviders.MicrosoftTeams/
  Rag.NET.DataProviders.Gmail/

tests/
  Rag.NET.DataProviders.Confluence.Tests/
  Rag.NET.DataProviders.Notion.Tests/
  Rag.NET.DataProviders.Jira.Tests/
  Rag.NET.DataProviders.Asana.Tests/
  Rag.NET.DataProviders.Slack.Tests/
  Rag.NET.DataProviders.MicrosoftTeams.Tests/
  Rag.NET.DataProviders.Gmail.Tests/
```

All packages reference `Rag.NET.DataProviders` (shared foundation) and extend `FileContentProviderBase`.

| Connector | NuGet dependency |
|---|---|
| Confluence | `Refit` + `Microsoft.Extensions.Http.Resilience` |
| Notion | `Refit` + `Microsoft.Extensions.Http.Resilience` |
| Jira | `Refit` + `Microsoft.Extensions.Http.Resilience` |
| Asana | `Refit` + `Microsoft.Extensions.Http.Resilience` |
| Slack | `Refit` + `Microsoft.Extensions.Http.Resilience` |
| MicrosoftTeams | `Microsoft.Graph` + `Azure.Identity` |
| Gmail | `MailKit` |

---

## Auth & Options

### Confluence (`Rag.NET.DataProviders.Confluence`)

**Auth:** Atlassian API token + email as HTTP Basic Auth (`{email}:{token}` base64-encoded).

```csharp
public sealed class ConfluenceOptions : CloudStorageOptions
{
    public required string BaseUrl  { get; init; }  // "https://myorg.atlassian.net"
    public required string Email    { get; init; }
    public string? SpaceKey         { get; init; }  // null = all spaces
}

services.AddConfluenceDataProvider("https://myorg.atlassian.net", "me@co.com", apiToken, opts => {
    opts.SpaceKey = "ENG";
});
```

---

### Jira (`Rag.NET.DataProviders.Jira`)

**Auth:** Same Atlassian API token + email as Confluence.

```csharp
public sealed class JiraOptions : CloudStorageOptions
{
    public required string BaseUrl   { get; init; }
    public required string Email     { get; init; }
    public string? ProjectKey        { get; init; }  // null = all projects
    public string  Jql { get; init; } = "order by updated DESC";
}

services.AddJiraDataProvider("https://myorg.atlassian.net", "me@co.com", apiToken, opts => {
    opts.ProjectKey = "PROJ";
});
```

---

### Notion (`Rag.NET.DataProviders.Notion`)

**Auth:** Notion integration token (Bearer).

```csharp
public sealed class NotionOptions : CloudStorageOptions
{
    public string? DatabaseId { get; init; }  // null = all accessible pages
}

services.AddNotionDataProvider(integrationToken, opts => {
    opts.DatabaseId = "abc123";
});
```

---

### Asana (`Rag.NET.DataProviders.Asana`)

**Auth:** Personal Access Token or OAuth 2.0 via `ITokenProvider`.

```csharp
public sealed class AsanaOptions : CloudStorageOptions
{
    public required string WorkspaceGid { get; init; }
    public string? ProjectGid           { get; init; }  // null = all projects
}

services.AddAsanaDataProvider(tokenProvider, workspaceGid, opts => { });
services.AddAsanaDataProvider(personalAccessToken, workspaceGid, opts => { });
```

---

### Slack (`Rag.NET.DataProviders.Slack`)

**Auth:** Bot token (`xoxb-...`) via `StaticTokenProvider`.

```csharp
public sealed class SlackOptions : CloudStorageOptions
{
    public string? ChannelId    { get; init; }  // null = all joined channels
    public int     MessageLimit { get; init; } = 200;  // per channel per run
}

services.AddSlackDataProvider(botToken, opts => {
    opts.ChannelId = "C0123456";
});
```

---

### Microsoft Teams (`Rag.NET.DataProviders.MicrosoftTeams`)

**Auth:** `ClientSecretCredential` → `GraphServiceClient` (same as SharePoint/OneDrive).

```csharp
public sealed class MicrosoftTeamsOptions : CloudStorageOptions
{
    public string? TeamId    { get; init; }   // null = all joined teams
    public string? ChannelId { get; init; }   // null = all channels in team
}

services.AddMicrosoftTeamsDataProvider(tenantId, clientId, clientSecret, opts => {
    opts.TeamId = "team-guid";
});
```

---

### Gmail (`Rag.NET.DataProviders.Gmail`)

**Auth:** OAuth 2.0 via `ITokenProvider`; passed to MailKit's `SaslMechanismOAuth2`.

```csharp
public sealed class GmailOptions : CloudStorageOptions
{
    public string Query      { get; init; } = "in:inbox";
    public int    MaxResults { get; init; } = 500;
}

services.AddGmailDataProvider(tokenProvider, opts => {
    opts.Query = "in:inbox label:support";
});
```

---

## Content Serialization

Every connector serializes native content to markdown and yields a `FileHandle`. Structured fields go into `DocumentMetadata` on the resulting `FileEntry`.

### Confluence

```
# {title}

{body — Confluence storage format converted to markdown via regex/string transforms}
```

- `FileHandle.Id` = page ID
- `FileHandle.FileName` = `{title}.md`
- `FileHandle.ETag` = version number (string)

### Jira

```
# {summary}

**Status:** {status}  **Priority:** {priority}  **Assignee:** {assignee}

{description}

## Comments

**{author}** ({created}): {body}
...
```

- `FileHandle.Id` = issue key (e.g. `PROJ-123`)
- `FileHandle.FileName` = `PROJ-123.md`
- `FileHandle.ETag` = `updated` timestamp

### Notion

```
# {title}

{blocks flattened to markdown — paragraphs, headings, bullet lists, code blocks, quotes}
```

- `FileHandle.Id` = page ID
- `FileHandle.FileName` = `{title}.md`
- `FileHandle.ETag` = `last_edited_time`

### Asana

```
# {name}

**Due:** {due_on}  **Assignee:** {assignee}  **Status:** {completion status}

{notes}

## Subtasks

- {subtask name}
...
```

- `FileHandle.Id` = task GID
- `FileHandle.FileName` = `{name}.md`
- `FileHandle.ETag` = `modified_at`

### Slack

Messages are batched **per channel per day** into a single `FileEntry` — avoids one entry per message (thousands of tiny entries).

```
# #{channel-name} — {YYYY-MM-DD}

**{username}** (HH:mm): {text}

> **{reply-username}** (HH:mm): {thread reply}

**{username}** (HH:mm): {text}
...
```

- `FileHandle.Id` = `{channel_id}/{YYYY-MM-DD}`
- `FileHandle.FileName` = `{channel-name}-{YYYY-MM-DD}.md`
- `FileHandle.ETag` = latest message `ts` in the batch

### Microsoft Teams

Same day-batch pattern as Slack.

```
# {team-name} / #{channel-name} — {YYYY-MM-DD}

**{displayName}** (HH:mm): {content}

> **{reply-displayName}** (HH:mm): {thread reply}
...
```

- `FileHandle.Id` = `{teamId}/{channelId}/{YYYY-MM-DD}`
- `FileHandle.FileName` = `{channel-name}-{YYYY-MM-DD}.md`
- `FileHandle.ETag` = `lastModifiedDateTime` of latest message

### Gmail

```
# {subject}

**From:** {from}  **Date:** {date}  **To:** {to}

{decoded plain-text body}
```

- `FileHandle.Id` = message ID
- `FileHandle.FileName` = `{subject}.md`
- `FileHandle.ETag` = `internalDate`

---

## Delta Sync

| Connector | Mechanism | `DeltaToken` format |
|---|---|---|
| Confluence | CQL: `lastModified > "{timestamp}"` | ISO-8601 timestamp |
| Jira | JQL: `updated > "{timestamp}"` | ISO-8601 timestamp |
| Notion | Search API `filter.last_edited_time.after` | ISO-8601 timestamp |
| Asana | `modified_since` query param | ISO-8601 timestamp |
| Slack | `oldest` param on `conversations.history` | Unix timestamp string |
| MicrosoftTeams | Graph `deltaLink` token (same as SharePoint) | `deltaLink` URL |
| Gmail | `users.history.list` with `startHistoryId` | history ID string |

On stale/invalid `DeltaToken`: connector catches the platform-specific error, logs a warning, falls back to full traversal.

---

## Refit Client Pattern

Each Refit-based connector defines its interface internally:

```csharp
// Example — Confluence
[Headers("Accept: application/json")]
internal interface IConfluenceApi
{
    [Get("/wiki/rest/api/content")]
    Task<ConfluencePageList> GetPagesAsync(
        [Query] string? spaceKey,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);
}
```

The `HttpClient` for each Refit client is registered with `AddDataProviderHttpClient(name).AddStandardResilienceHandler()` — the same helper used in Group 1.

Basic Auth for Atlassian connectors is set via `DefaultRequestHeaders.Authorization` on the named `HttpClient` before passing to `RestService.For<T>()`.

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| 401 / 403 | Propagated as-is |
| 429 (rate limit) | Handled by `AddStandardResilienceHandler()` |
| Stale / invalid `DeltaToken` | Log warning, fall back to full traversal |
| Empty page / task / message batch | Yielded as `FileEntry` with minimal markdown body |
| Channel / team with no messages | No `FileEntry` emitted |
| Gmail OAuth token expiry | `ITokenProvider` refreshes transparently |

---

## Testing

| Connector | Approach |
|---|---|
| Confluence, Notion, Jira, Asana, Slack | Fake `HttpMessageHandler` returning canned JSON (same pattern as GoogleDrive tests) |
| MicrosoftTeams | Fake Graph HTTP handler (same pattern as SharePoint/OneDrive tests) |
| Gmail | NSubstitute mocks on MailKit's `IMailFolder` / `ImapClient` interfaces |

Each connector has tests covering: full traversal, delta traversal, extension filtering, stale delta token fallback.

---

## Migration: `features.md`

On completion, mark Group 2 and Group 3 rows as `[x]` in the priority table and add `**Status:** ✅ Done` to each group section.
