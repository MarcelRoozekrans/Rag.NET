---
id: data-providers
title: Data Providers
sidebar_label: Data Providers
sidebar_position: 10
---

# Data Providers

Data providers are connectors that enumerate remote files and stream their content into the Rag.NET ingestion pipeline. They implement `IFileContentProvider`, which the pipeline calls during `IngestFromProviderAsync` to receive a sequence of `Result<FileEntry, RagError>` items. Each successful item carries a stable ID, a filename, an optional ETag for deduplication, and a factory that opens the file as a stream. HTTP failures from the remote API are surfaced as `RagError.HttpFailed` results and collected in `ProviderIngestionResult.Errors` — the pipeline continues processing remaining files rather than aborting.

```csharp
// Typical usage: provider registered in DI, pipeline receives it via injection
var result = await pipeline.IngestFromProviderAsync(provider, "my-corpus",
    hashStore: sp.GetRequiredService<IContentHashStore>(),
    cleanupMode: CleanupMode.Full);

Console.WriteLine($"Ingested: {result.Ingested}, Skipped: {result.Skipped}, Deleted: {result.Deleted}");
```

The pipeline compares each file's ETag against the content hash store. Files whose ETag is unchanged since the last run are skipped automatically — no re-embedding, no re-storing.

---

## Shared options (`CloudStorageOptions`)

Every cloud connector inherits from `CloudStorageOptions`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Extensions` | `IReadOnlyList<string>` | `["*"]` | File extensions to include. `"*"` matches all extensions. Pass `[".md", ".pdf"]` to restrict. |
| `Filter` | `Func<string, bool>?` | `null` | Optional predicate applied to the provider-specific file ID (path or opaque key). Return `false` to exclude a file. |
| `DeltaToken` | `string?` | `null` | Opaque cursor for incremental runs. `null` triggers a full traversal. See [Delta ingestion](#delta-incremental-ingestion). |

---

## Token providers

Several connectors accept an `ITokenProvider` for bearer-token authentication.

### `StaticTokenProvider`

Wraps a single fixed token — suitable for long-lived API keys, Personal Access Tokens, and SAS tokens.

```csharp
var tokenProvider = new StaticTokenProvider("ghp_MyPersonalAccessToken");
```

### `OAuthClientCredentialsTokenProvider`

Fetches an access token from a standard OAuth 2.0 token endpoint using the client credentials flow and refreshes it automatically 60 seconds before it expires.

```csharp
var tokenProvider = new OAuthClientCredentialsTokenProvider(
    tokenEndpoint: "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
    clientId:      "my-client-id",
    clientSecret:  "my-client-secret",
    scopes:        ["https://graph.microsoft.com/.default"]);
```

| Parameter | Required | Description |
|-----------|----------|-------------|
| `tokenEndpoint` | yes | Full URL of the OAuth token endpoint |
| `clientId` | yes | Application (client) ID |
| `clientSecret` | yes | Application secret |
| `scopes` | no | Space-separated scope strings; omit for endpoints that do not require a `scope` parameter |

`OAuthClientCredentialsTokenProvider` implements `IDisposable`; dispose it when it owns the `HttpClient` (i.e., when you do not pass one in the constructor).

---

## Connector reference

| Connector | Package | Auth | Delta support | Notes |
|-----------|---------|------|---------------|-------|
| Azure Blob Storage | `Rag.NET.DataProviders.AzureBlob` | Connection string or `TokenCredential` | ETag-based | Resilience via Azure SDK built-in retry; do not add an external retry policy |
| SharePoint | `Rag.NET.DataProviders.SharePoint` | `ClientSecretCredential` (tenant/client/secret) | Graph deltaLink | Enumerates root drive children recursively |
| OneDrive | `Rag.NET.DataProviders.OneDrive` | `ClientSecretCredential` (tenant/client/secret) | Graph deltaLink | Requires a `UserId` or `"me"` for delegated auth |
| Google Drive | `Rag.NET.DataProviders.GoogleDrive` | Service account JSON key file or `DriveService` | Changes.List pageToken | Whole-drive or folder-scoped via `FolderId`; recursive |
| Dropbox | `Rag.NET.DataProviders.Dropbox` | Access token or `ITokenProvider` | ListFolder cursor | Cursors do not expire |
| Box | `Rag.NET.DataProviders.Box` | JWT config JSON or `BoxClient` | Events stream position | Root folder configurable via `RootFolderId` |
| GitHub | `Rag.NET.DataProviders.GitHub` | PAT via `StaticTokenProvider` | Commit SHA (`LastIngestedCommitSha`) | ETag is the blob SHA — byte-identical content is guaranteed |
| Confluence | `Rag.NET.DataProviders.Confluence` | Basic Auth (email + API token) | CQL `lastModified>` cursor | Atlassian Cloud; pages exported as HTML |
| Jira | `Rag.NET.DataProviders.Jira` | Basic Auth (email + API token) | JQL `updated >` timestamp | Atlassian Cloud; issues exported as HTML |
| Notion | `Rag.NET.DataProviders.Notion` | Bearer integration token | Client-side `last_edited_time` | Exports pages as Markdown |
| Asana | `Rag.NET.DataProviders.Asana` | Bearer PAT or OAuth2 | `modified_since` parameter | Requires `workspaceGid`; tasks exported as HTML |
| Slack | `Rag.NET.DataProviders.Slack` | Bearer bot token | `oldest` Unix timestamp | Channel messages exported as plain text |
| Microsoft Teams | `Rag.NET.DataProviders.MicrosoftTeams` | OAuth2 client credentials | Not yet supported | Graph SDK; messages exported as HTML |
| Gmail | `Rag.NET.DataProviders.Gmail` | OAuth2 (`SaslMechanismOAuth2`) | IMAP UniqueId watermark | MailKit IMAP; emails exported as plain text |
| Exchange / Outlook | `Rag.NET.DataProviders.Exchange` | `ClientSecretCredential` (tenant/client/secret) | `receivedDateTime` watermark | Graph SDK; emits raw RFC 822 `.eml` — requires `AddEmailParser()` |
| Linear | `Rag.NET.DataProviders.Linear` | Personal API key (bare `Authorization` header) | `updatedAt` watermark | GraphQL API; issues + comments exported as Markdown |
| GitLab | `Rag.NET.DataProviders.GitLab` | PAT (`PRIVATE-TOKEN` header) | Commit SHA compare | Repository files; same delta pattern as GitHub |
| Bitbucket | `Rag.NET.DataProviders.Bitbucket` | App Password (Basic Auth) | Commit hash diffstat | Repository files via REST API |
| Zendesk (Tickets) | `Rag.NET.DataProviders.Zendesk` | API Token (Basic Auth `email/token:key`) | Incremental cursor (`start_time`) | Tickets exported as HTML |
| Zendesk (Articles) | `Rag.NET.DataProviders.Zendesk` | API Token (Basic Auth) | Incremental (`start_time`) | Help Center articles exported as HTML |
| Airtable | `Rag.NET.DataProviders.Airtable` | PAT (Bearer token) | `filterByFormula` on Last Modified field | Rows and attachments |
| Web (Sitemap / RSS / Crawler) | `Rag.NET.DataProviders.Web` | None | None | Construct directly; no DI extension method |

---

## DI registration examples

### Azure Blob Storage — connection string

```csharp
services.AddAzureBlobDataProvider(
    connectionString: "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    containerName:    "my-documents",
    configure: opts =>
    {
        opts.Extensions = [".pdf", ".docx", ".md"];
        opts.Prefix     = "reports/";
    });
```

### Azure Blob Storage — managed identity / `TokenCredential`

```csharp
services.AddAzureBlobDataProvider(
    credential:    new DefaultAzureCredential(),
    containerUri:  new Uri("https://myaccount.blob.core.windows.net/my-documents"));
```

### SharePoint

```csharp
services.AddSharePointDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: "my-app-client-secret",
    siteId:       "contoso.sharepoint.com,site-guid,web-guid",
    driveId:      "drive-guid",
    configure: opts =>
    {
        opts.Extensions = [".docx", ".pdf"];
        opts.DeltaToken = settings.SharePointDeltaToken; // null on first run
    });
```

### OneDrive

```csharp
services.AddOneDriveDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: "my-app-client-secret",
    userId:       "user@contoso.com",
    configure: opts =>
    {
        opts.Extensions = [".md", ".txt"];
        opts.DeltaToken = settings.OneDriveDeltaToken;
    });
```

### Google Drive — service account key file

```csharp
services.AddGoogleDriveDataProvider(
    serviceAccountKeyPath: "/secrets/service-account.json",
    configure: opts =>
    {
        opts.FolderId  = "1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs74OgVE2upms"; // null = entire drive
        opts.Extensions = [".pdf", ".docx"];
        opts.DeltaToken = settings.GoogleDriveDeltaToken;
    });
```

### Dropbox — access token

```csharp
services.AddDropboxDataProvider(
    accessToken: "sl.MyDropboxAccessToken",
    configure: opts =>
    {
        opts.FolderPath = "/Engineering/Docs"; // "" = root
        opts.DeltaToken = settings.DropboxCursor;
    });
```

### Dropbox — `ITokenProvider` (OAuth refresh)

```csharp
var tokenProvider = new OAuthClientCredentialsTokenProvider(
    tokenEndpoint: "https://api.dropbox.com/oauth2/token",
    clientId:      "my-app-key",
    clientSecret:  "my-app-secret");

services.AddDropboxDataProvider(tokenProvider, opts =>
{
    opts.DeltaToken = settings.DropboxCursor;
});
```

### Box — JWT config JSON

```csharp
services.AddBoxDataProvider(
    jwtConfigJson: File.ReadAllText("/secrets/box-config.json"),
    configure: opts =>
    {
        opts.RootFolderId = "0"; // "0" = root
        opts.Extensions   = [".pdf", ".docx"];
        opts.DeltaToken   = settings.BoxStreamPosition;
    });
```

### GitHub — PAT

```csharp
var gitHubClient = new GitHubClient(new ProductHeaderValue("my-app"))
{
    Credentials = new Credentials("ghp_MyPersonalAccessToken"),
};

var provider = new GitHubDataProvider(
    owner:  "my-org",
    repo:   "my-repo",
    client: gitHubClient,
    options: new GitHubDataProviderOptions
    {
        Branch               = "main",
        Extensions           = [".md", ".cs"],
        Filter               = path => !path.StartsWith("docs/plans/"),
        LastIngestedCommitSha = settings.LastIngestedCommitSha, // null on first run
    });

// Register manually (no DI extension method for GitHub)
services.AddSingleton<IFileContentProvider>(provider);
```

### Confluence

```csharp
services.AddConfluenceDataProvider(
    baseUrl:  "https://your-domain.atlassian.net/wiki",
    email:    "user@example.com",
    apiToken: "ATATT3xFfGF0...",
    configure: opts =>
    {
        opts.SpaceKeys  = ["ENG", "OPS"];         // null = all spaces
        opts.Extensions = [".html"];
        opts.DeltaToken = settings.ConfluenceDeltaToken;
    });
```

### Jira

```csharp
services.AddJiraDataProvider(
    baseUrl:  "https://your-domain.atlassian.net",
    email:    "user@example.com",
    apiToken: "ATATT3xFfGF0...",
    configure: opts =>
    {
        opts.Jql        = "project = ENG";         // null = all issues
        opts.Extensions = [".html"];
        opts.DeltaToken = settings.JiraDeltaToken;
    });
```

### Notion

```csharp
services.AddNotionDataProvider(
    integrationToken: "ntn_...",
    configure: opts =>
    {
        opts.Extensions = [".md"];
        opts.DeltaToken = settings.NotionDeltaToken;
    });
```

> **`baseUrl`** (optional) — overrides the default base URL (`https://api.notion.com`). Useful when routing through a proxy or pointing at a local mock during testing.

### Asana — PAT

```csharp
services.AddAsanaDataProvider(
    personalAccessToken: "1/12345:abcdef...",
    workspaceGid:        "1234567890",
    configure: opts =>
    {
        opts.ProjectGid = "9876543210";            // null = all projects in workspace
        opts.DeltaToken = settings.AsanaDeltaToken;
    });
```

### Asana — `ITokenProvider` (OAuth2)

```csharp
var tokenProvider = new OAuthClientCredentialsTokenProvider(
    tokenEndpoint: "https://app.asana.com/-/oauth_token",
    clientId:      "my-client-id",
    clientSecret:  "my-client-secret");

services.AddAsanaDataProvider(tokenProvider, workspaceGid: "1234567890", opts =>
{
    opts.DeltaToken = settings.AsanaDeltaToken;
});
```

> **`baseUrl`** (optional) — overrides the default base URL (`https://app.asana.com`). Useful when routing through a proxy or pointing at a local mock during testing.

### Slack

```csharp
services.AddSlackDataProvider(
    botToken: "xoxb-...",
    configure: opts =>
    {
        opts.ChannelIds = ["C01ABCDEF", "C02GHIJKL"]; // null = all public channels
        opts.DeltaToken = settings.SlackDeltaToken;
    });
```

> **`baseUrl`** (optional) — overrides the default base URL (`https://slack.com`). Useful when routing through a proxy or pointing at a local mock during testing.

### Microsoft Teams

```csharp
services.AddMicrosoftTeamsDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: "my-app-client-secret",
    configure: opts =>
    {
        opts.TeamId   = "team-guid";               // required
        opts.ChannelId = "channel-guid";           // null = all channels in the team
    });
```

### Gmail

```csharp
var tokenProvider = new OAuthClientCredentialsTokenProvider(
    tokenEndpoint: "https://oauth2.googleapis.com/token",
    clientId:      "my-client-id.apps.googleusercontent.com",
    clientSecret:  "my-client-secret",
    scopes:        ["https://mail.google.com/"]);

services.AddGmailDataProvider(tokenProvider, opts =>
{
    opts.ImapHost   = "imap.gmail.com";            // default
    opts.ImapPort   = 993;                         // default
    opts.EmailAddress = "user@example.com";
    opts.DeltaToken = settings.GmailDeltaToken;    // IMAP UniqueId watermark
});
```

### Exchange / Outlook

The Exchange connector emits each message as a raw RFC 822 **`.eml`** entry (fetched from
Graph's `/users/{mailbox}/messages/{id}/$value`) rather than pre-rendered Markdown. This is
deliberate: it lets `EmailDocumentParser` parse subject/body **and dispatch attachments to
the other registered parsers** (PDF, Word, text, …). Ingesting the emitted entries therefore
**requires `AddEmailParser()`** from `Rag.NET.Parsers.Email`:

```csharp
services.AddRagNet(rag => rag.AddEmailParser()); // .eml → message/rfc822 parser + attachment dispatch

services.AddExchangeMailDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: "my-app-client-secret",
    configure: opts =>
    {
        opts.Mailbox    = "ingest@contoso.com";        // required mailbox UPN
        opts.FolderIds  = ["inbox", "archive"];        // null = Inbox only
        opts.MaxResults = 500;                         // default
        opts.DeltaToken = settings.ExchangeDeltaToken; // receivedDateTime watermark; null on first run
    });
```

**App registration:** uses app-only authentication (client credentials flow); the Azure AD
app registration needs the **`Mail.Read` application permission** (Microsoft Graph →
Application permissions) with admin consent. Delegated `/me` flows are out of scope.
Note that app-only `Mail.Read` grants read access to **every mailbox in the tenant** —
scope the app to the ingest mailbox with an Exchange application access policy
(`New-ApplicationAccessPolicy`) or RBAC for Applications.

**Watermark persistence:** after a run, read the new watermark from the provider and persist
it for the next run — the connector filters with `receivedDateTime ge {DeltaToken}`.
Persist the token **only after an error-free run**: the watermark advances during
enumeration, before per-entry ingestion outcomes are known. When a run is truncated by
`MaxResults` in the **last (or only) folder**, the token advances to the truncation point
(messages are enumerated oldest-first, so everything unseen is newer) — a backlog larger
than `MaxResults` therefore drains at `MaxResults` per run. `GetDeltaToken()` returns
`null` when the run failed or was truncated **before the last folder was reached** — keep
the previous token in that case, otherwise the never-visited folders' messages would be
skipped forever:

```csharp
var provider = (ExchangeMailDataProvider)sp.GetRequiredService<IFileContentProvider>();
var result   = await pipeline.IngestFromProviderAsync(provider, new ProviderId("exchange"), hashStore);

if (result.Errors.Count == 0 && provider.GetDeltaToken() is { } token)
    settings.ExchangeDeltaToken = token;
```

> Graph delta queries (`/mailFolders/{id}/messages/delta`) are intentionally **not** used in
> v1 — the `receivedDateTime` watermark plus the hash-store ETag skip covers incremental
> ingestion; same-timestamp duplicates on the next run are skipped by content hash.

### Linear

The Linear connector is the repo's first **GraphQL** connector: it issues a single paginated
`issues` query against `https://api.linear.app/graphql` (POST with a typed request body via
the existing ZeroAlloc.Rest pattern — no dedicated GraphQL client dependency). Each issue is
emitted as a Markdown entry (`{identifier} {title}.md`) containing the title heading, a
state/project/assignee line, the description, and a `## Comments` section, with
team/state/state_type/project/url metadata (plus `comments_truncated` when an issue's comments exceed the fetched page — see Comments below).

```csharp
services.AddLinearDataProvider(
    apiKey: "lin_api_...",                        // personal API key (Settings → API)
    configure: opts =>
    {
        opts.TeamKeys   = ["ENG", "OPS"];         // null = all teams
        opts.States     = ["started", "completed"]; // state *types*; null = all
        opts.PageSize   = 50;                     // issues per GraphQL page (default)
        opts.DeltaToken = settings.LinearDeltaToken; // updatedAt watermark; null on first run
    });
```

**Authentication:** Linear personal API keys are sent as a **bare** `Authorization` header —
`Authorization: lin_api_...` with **no `Bearer` prefix** (`Bearer` is only used for OAuth2
access tokens).

**State filtering** uses Linear's workflow state *types* (categories), not display names:
`triage`, `backlog`, `unstarted`, `started`, `completed`, `canceled` — note the American
spelling of `canceled`. Invalid values throw at registration.

**Comments:** up to 100 comments per issue are fetched inline; an issue with more is still
emitted (with the first 100) but flagged with a `comments_truncated: "true"` metadata entry
and a logged warning.

**Watermark:** the connector filters with `updatedAt > DeltaToken` and tracks the max
`updatedAt` seen. Because Linear does not document the sort direction of
`orderBy: updatedAt`, `GetDeltaToken()` only returns a token after a **complete** traversal
(all pages consumed without a failure); a run that failed mid-pagination returns `null` —
keep the previous token in that case:

```csharp
var provider = (LinearDataProvider)sp.GetRequiredService<IFileContentProvider>();
var result   = await pipeline.IngestFromProviderAsync(provider, new ProviderId("linear"), hashStore);

if (result.Errors.Count == 0 && provider.GetDeltaToken() is { } token)
    settings.LinearDeltaToken = token;
```

> **`baseUrl`** (optional) — overrides the default base URL (`https://api.linear.app`). Useful when routing through a proxy or pointing at a local mock during testing.

### GitLab

```csharp
services.AddGitLabDataProvider(
    baseUrl:           "https://gitlab.com",
    projectIdOrPath:   "my-org/my-repo",
    token:             "glpat-xxxxxxxxxxxxxxxxxxxx",
    configure: opts =>
    {
        opts.Branch     = "main";
        opts.Extensions = [".md", ".cs"];
        opts.DeltaToken = settings.GitLabDeltaToken; // commit SHA; null on first run
    });
```

### Bitbucket

```csharp
services.AddBitbucketDataProvider(
    workspace:   "my-workspace",
    repoSlug:    "my-repo",
    username:    "my-username",
    appPassword: "my-app-password",
    configure: opts =>
    {
        opts.Branch     = "main";
        opts.Extensions = [".md", ".cs"];
        opts.DeltaToken = settings.BitbucketDeltaToken; // commit hash; null on first run
    });
```

### Zendesk — Tickets

```csharp
services.AddZendeskTicketsDataProvider(
    subdomain: "mycompany",
    email:     "agent@example.com",
    apiToken:  "my-zendesk-api-token",
    configure: opts =>
    {
        opts.DeltaToken = settings.ZendeskTicketsDeltaToken; // Unix epoch; null on first run
    });
```

> **`baseUrl`** (optional) — overrides the default base URL (`https://{subdomain}.zendesk.com`). Useful when routing through a proxy or pointing at a local mock during testing.

### Zendesk — Articles

```csharp
services.AddZendeskArticlesDataProvider(
    subdomain: "mycompany",
    email:     "agent@example.com",
    apiToken:  "my-zendesk-api-token",
    configure: opts =>
    {
        opts.DeltaToken = settings.ZendeskArticlesDeltaToken; // Unix epoch; null on first run
    });
```

> **`baseUrl`** (optional) — overrides the default base URL (`https://{subdomain}.zendesk.com`). Useful when routing through a proxy or pointing at a local mock during testing.

### Airtable

```csharp
services.AddAirtableDataProvider(
    baseId:              "appXXXXXXXXXXXXXX",
    tableName:           "My Table",
    personalAccessToken: "patXXXXXXXXXXXXXX",
    configure: opts =>
    {
        opts.DeltaToken = settings.AirtableDeltaToken; // ISO 8601 timestamp; null on first run
    });
```

### Web — Sitemap

```csharp
var httpClient = new HttpClient();
var provider = new SitemapDataProvider("https://docs.example.com/sitemap.xml", httpClient);
services.AddSingleton<IFileContentProvider>(provider);
```

### Web — RSS / Atom feed

```csharp
var provider = new RssDataProvider("https://example.com/feed.rss", httpClient);
services.AddSingleton<IFileContentProvider>(provider);
```

### Web — Crawler

```csharp
var provider = new WebCrawlerDataProvider("https://docs.example.com", httpClient, new WebCrawlerOptions
{
    MaxDepth         = 3,
    MaxPages         = 500,
    SameDomain       = true,
    RespectRobotsTxt = true,
});
services.AddSingleton<IFileContentProvider>(provider);
```

---

## Delta (incremental) ingestion

Delta ingestion lets you process only the files that changed since the last run, rather than re-downloading the entire corpus.

### How it works

1. **First run** — set `DeltaToken = null`. The connector performs a full traversal and returns all matching files.
2. **Save the token** — after the run completes, read the new delta token from the connector and persist it (e.g., in a database or settings file).
3. **Subsequent runs** — pass the saved token back via `DeltaToken`. The connector queries only changes since the previous run.

```csharp
// First run
services.AddSharePointDataProvider(tenantId, clientId, clientSecret, siteId, driveId, opts =>
{
    opts.DeltaToken = null; // full traversal
});

// After the run, save the returned token:
// settings.SharePointDeltaToken = connector.LastDeltaToken;

// Subsequent runs
services.AddSharePointDataProvider(tenantId, clientId, clientSecret, siteId, driveId, opts =>
{
    opts.DeltaToken = settings.SharePointDeltaToken;
});
```

### Token formats by connector

| Connector | Token format | Notes |
|-----------|-------------|-------|
| SharePoint | Graph deltaLink URL | Opaque URL returned by the Graph delta API |
| OneDrive | Graph deltaLink URL | Same mechanism as SharePoint |
| Google Drive | Changes.List page token | Returned by `changes.getStartPageToken` or last `changes.list` call |
| Dropbox | ListFolder cursor | Does not expire; safe to store indefinitely |
| Box | Events stream position (string) | Numeric position in the Box events stream |
| GitHub | Commit SHA | The HEAD commit SHA at the time of the last successful ingest |
| Confluence | CQL `lastModified>` ISO date-time | Stored as the last-seen `lastModified` value; pass back via `DeltaToken` |
| Jira | JQL `updated >` ISO date-time | Stored as the last-seen `updated` value |
| Notion | ISO 8601 `last_edited_time` | Client-side filter; all pages are listed but only recently edited ones are returned |
| Asana | ISO 8601 `modified_since` | Passed to the API as a query parameter |
| Slack | Unix timestamp (string) | Passed as `oldest` to `conversations.history` |
| Microsoft Teams | Not yet supported | Delta ingestion is not yet implemented for this connector |
| Gmail | IMAP UniqueId (string) | Messages with a UID greater than the watermark are fetched |
| Exchange / Outlook | ISO 8601 `receivedDateTime` (string) | Applied as a `receivedDateTime ge` filter; `GetDeltaToken()` returns the max value seen, the truncation point when `MaxResults` fired in the last folder (backlogs drain per run), or `null` when the run failed or was truncated earlier (keep the previous token) |
| Linear | ISO 8601 `updatedAt` (string) | Applied as an `updatedAt >` GraphQL filter; `GetDeltaToken()` returns the max value seen after a complete traversal, or `null` when the run failed mid-pagination (keep the previous token) |
| GitLab | Commit SHA (string) | HEAD commit SHA at last successful ingest; compare API returns changed files |
| Bitbucket | Commit hash (string) | HEAD commit hash at last successful ingest; diffstat API returns changed files |
| Zendesk | Unix epoch (string) | Passed as `start_time` to the incremental export API |
| Airtable | ISO 8601 timestamp (string) | Used in `filterByFormula` against the Last Modified field |
| Azure Blob | Not applicable | Uses per-file ETag comparison rather than a cursor |

> Azure Blob Storage does not use a `DeltaToken` cursor. Instead, the pipeline's content hash store compares each blob's ETag against the stored value. A stale ETag simply means the blob is re-ingested — no data is lost.

> For SharePoint and OneDrive, stale or expired delta tokens (Graph error codes `resyncRequired` or `itemNotFound`) cause the connector to automatically fall back to a full traversal. No intervention is needed.

---

## Event-driven ingestion

`IngestFromProviderAsync` is pull-based — something must call it. Event-driven ingestion inverts that: producers push `IngestionJob`s onto a bounded in-memory queue and a background processor ingests them as they arrive. Two triggers ship today — an HMAC-verified webhook endpoint (`Rag.NET.Api`) and a background polling trigger. An Azure Service Bus trigger is deferred; the `IIngestionJobQueue` abstraction is the seam it will later plug into.

### Job queue + background processor

```csharp
services.AddRagNet(rag => rag
    .UseEventDrivenIngestion(o => o.QueueCapacity = 500)); // default 1000
```

`UseEventDrivenIngestion` (in `Rag.NET.DataProviders`) registers:

- `IIngestionJobQueue` → `ChannelIngestionJobQueue`, a bounded channel with `BoundedChannelFullMode.Wait`: a full queue applies backpressure (`EnqueueAsync` waits for space); jobs are never dropped.
- `IngestionJobProcessor`, a `BackgroundService` that drains the queue into `IIngestor.IngestAsync`. A job that fails — failure result or thrown exception — is logged as a warning with its document id and skipped; the processor never crashes. Host shutdown exits the loop cleanly.
- **Durability:** the queue is in-memory only — jobs still queued (and the one in flight) are lost on host stop or crash. Producers that need durable delivery should retry on missing acknowledgement (safe: ingestion upserts by `DocumentId`) or wait for the planned Service Bus trigger.

`IngestionJob` carries `byte[] Content` rather than a `Stream` because jobs outlive the enqueue call — e.g. an HTTP request body is long disposed by the time the processor runs. The host must support hosted services (`IHost` / ASP.NET Core; `AddHostedService` is used under the covers).

#### Capacity and throughput

- The processor is **deliberately sequential** — one job in flight at a time, so the drain rate equals your single-document ingest latency. A sustained producer rate above that fills the queue until backpressure kicks in (enqueues wait for space).
- Worst-case queue memory is `QueueCapacity × payload size`: with the default capacity of 1000 and 5 MB documents that is ~5 GB of buffered payload bytes. Tune `QueueCapacity` to your payload profile.
- Webhook request bodies are additionally bounded by Kestrel's default `MaxRequestBodySize` (~28.6 MB) unless the host overrides it.

### Webhook endpoint (`Rag.NET.Api`)

```csharp
builder.Services.AddRagNetWebhooks(o =>
{
    o.Secret = builder.Configuration["Webhooks:Secret"]!; // required, non-empty
    // o.SignatureHeader = "X-Signature-256";  // default
    // o.RoutePrefix     = "/rag/webhooks";    // default
});

app.UseRagNetApiAuthentication();
app.MapRagNetWebhooks(); // POST /rag/webhooks/ingest
```

Webhook requests are authenticated by an HMAC-SHA256 signature over the **raw request body**, hex-encoded in the signature header (a GitHub-style `sha256=` prefix is tolerated; the comparison is timing-safe). The webhook route prefix is exempted from `ApiKeyMiddleware` — the signature replaces the API key for webhook callers, while all other API routes keep requiring the key.

The built-in `GenericWebhookPayloadParser` accepts a single object or an array of:

```json
{ "documentId": "doc-1", "content": "full document text", "metadata": { "source": "github" } }
```

`documentId` and `content` are required and non-empty; `metadata` (optional) must be a flat string map and becomes `DocumentMetadata.Tags`; the file name defaults to `{documentId}.txt`. To handle provider-specific payload shapes (GitHub push events, Notion page updates, …) register a custom `IWebhookPayloadParser` **before** `AddRagNetWebhooks` — the default parser is registered with `TryAdd`, so an earlier registration wins.

Computing the signature — sender side in C#:

```csharp
var body = """{"documentId":"doc-1","content":"hello world"}""";
var signature = Convert.ToHexString(HMACSHA256.HashData(
    Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
// send as:  X-Signature-256: sha256=<signature>
```

…or with `curl` + `openssl`:

```bash
BODY='{"documentId":"doc-1","content":"hello world"}'
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -hex | awk '{print $NF}')
curl -X POST https://localhost:5001/rag/webhooks/ingest \
     -H "Content-Type: application/json" \
     -H "X-Signature-256: sha256=$SIG" \
     -d "$BODY"
```

Responses: `202 Accepted` with `{ "enqueued": n }`; `401` for a missing/invalid signature; `400` for invalid JSON or a payload the parser rejects; `503` (with an actionable message) when no `IIngestionJobQueue` is registered — call `UseEventDrivenIngestion` so accepted jobs actually get processed.

#### Security and delivery semantics

- **Replay protection**: the HMAC scheme authenticates the sender but carries no timestamp or nonce, so a captured request can be replayed verbatim — the same posture as GitHub's `X-Hub-Signature-256`. HTTPS transport is assumed. Because ingestion is an id-keyed upsert, a replay re-ingests the same content under the same `documentId` rather than duplicating it. Senders that need genuine replay resistance should include a timestamp (or nonce) in the payload and enforce a freshness window in a custom `IWebhookPayloadParser`.
- **At-least-once delivery**: a caller that times out mid-enqueue and retries can enqueue the same document twice. This is safe for the same reason — ingestion upserts by `DocumentId`, so the second job overwrites rather than duplicates.

### Background polling trigger

```csharp
services.AddRagNet(rag => rag
    .UseContentHashRecordManager("ragnet-hashes.db") // optional: enables hash-skip
    .UsePollingIngestion(
        sp => new LocalFilesDataProvider("./docs"),
        o =>
        {
            o.ProviderId      = "local-docs";              // required
            o.PollingInterval = TimeSpan.FromMinutes(10);  // default 5 min
            // o.CleanupMode  = CleanupMode.Full;          // also delete disappeared docs
        }));
```

Each `UsePollingIngestion` call registers an **independent** `BackgroundPollingTrigger` hosted service with its own provider and options — register it multiple times to poll multiple sources concurrently. Every cycle runs `IngestFromProviderAsync` (hash-skip applies automatically when an `IContentHashStore` is registered) and logs an ingested/skipped/deleted/errors summary; a failed cycle logs a warning and the next cycle proceeds. Set `CleanupMode = CleanupMode.Full` (requires the hash store) to also delete documents that disappeared from the source each cycle. Interval-based only — cron scheduling is out of scope.

### Azure Service Bus (deferred)

A Service Bus trigger (consume messages from a queue/topic and enqueue the referenced documents) is planned but not part of this phase. It will be a thin producer over the same `IIngestionJobQueue`.

---

## Extension and predicate filtering

### Extension filtering

Pass a list of extensions to `Extensions` to limit which files are downloaded. Extensions must include the leading dot:

```csharp
opts.Extensions = [".md", ".pdf", ".docx"];
```

The default `["*"]` matches everything. Extension matching is case-insensitive.

### Predicate filtering

Use `Filter` to exclude files by their provider-specific ID (typically a path or URL). Return `false` to exclude a file:

```csharp
// Exclude anything under a "drafts" folder
opts.Filter = path => !path.Contains("/drafts/", StringComparison.OrdinalIgnoreCase);
```

Both filters are applied before the file content is downloaded, so excluded files incur no network cost beyond the listing API call.

---

## Metadata

Connectors attach a small dictionary of tags to the entries they emit — `FileHandle.Metadata`, or `FileEntry.Metadata` for the three Web providers, which do not extend `FileContentProviderBase`. Nearly every entry carries one; a connector with nothing to say about a particular entry emits `null` instead (Box on a delta `UPLOAD` event, Google Drive when neither `mime_type` nor `folder_id` applies), which the table below marks per connector. `IngestFromProviderAsync` merges those tags into `DocumentMetadata.Tags`, and `MetadataBehavior` copies them onto every chunk produced from the document. They are what `HasTagSpec` filters on at query time.

Where a connector already renders a value into the Markdown body it emits (`**Status:** …`), that line **stays** and the value is *additionally* emitted as a tag. The body is what gets embedded, so it drives semantic recall; the tag is what gets filtered. Neither substitutes for the other.

### The convention

| Rule | Detail |
|-------|--------|
| Keys are `snake_case` | Lowercase letters, digits and underscores; unprefixed, no leading or trailing underscore. |
| Values are always `string` | The dictionary is `IReadOnlyDictionary<string, string>`. Numeric values (`depth`, `version`, `message_count`, `section_id`) are rendered with the invariant culture. |
| Booleans are `"true"` / `"false"` | Lowercase literals. `bool.ToString()` yields `"True"`, which does not match ordinally in `HasTagSpec` — so the tag deliberately differs from the same value's rendering in the Markdown body. |
| Timestamps a connector formats itself are ISO-8601 round-trip | `ToString("o", CultureInfo.InvariantCulture)`. Values passed through verbatim from a vendor API are **not** normalised — see the caveats below. |
| Optional fields are omitted, never written empty | An empty tag value is indistinguishable from a real one at query time, so a connector leaves the key out entirely. |
| The dictionary is ordinal | `new Dictionary<string, string>(StringComparer.Ordinal)`, matching `DocumentMetadata.Tags`. |
| Nothing to add → `null` | A connector with nothing to say returns `null`, not an empty dictionary — one representation, not two. |

### `provider_id`

`provider_id` is written **centrally**, by `IngestFromProviderAsync`, from the `ProviderId` you pass it. It is therefore present on **every** provider-ingested document regardless of connector, and it is what you filter on to scope a query to one source — or to find everything a given source contributed when you want to re-ingest it:

```csharp
await pipeline.IngestFromProviderAsync(provider, new ProviderId("eng-confluence"), hashStore);
// every resulting chunk carries provider_id = "eng-confluence"
```

`LocalFilesDataProvider` and any custom `IFileContentProvider` that returns no metadata still get `provider_id` — it costs the connector nothing.

### Per-connector keys

All 21 connector packages are listed. Zendesk ships two providers and Web ships three, so the table has 24 rows.

A key in the **Always** column is still omitted if its source value comes back empty — no connector ever writes an empty tag. In practice the vendor always supplies these.

| Connector | Always | Conditional |
|-----------|--------|-------------|
| Azure Blob Storage | `path` (blob name), `container` | — |
| SharePoint | `drive_id` | `parent_path` — when Graph returns `parentReference` (omitted on some delta payloads) |
| OneDrive | `drive_id` | `parent_path` — as SharePoint |
| Google Drive | — | `mime_type` — every call site's field selection fetches it, so in practice always; `folder_id` — folder-scoped traversal only, the whole-drive and Changes paths do not know the container. Metadata is `null` when neither applies. |
| Dropbox | `path` | `folder` — when `FolderPath` is set (omitted at root) |
| Box | — | `folder_id` — **full traversal only**; `change_status` — **delta runs, `COPY` events only**. A delta `UPLOAD` event yields no metadata at all. |
| GitHub | `path`, `repo` (`owner/name`), `ref` (configured branch) | `change_status` — delta runs only; a full tree traversal has no notion of change |
| GitLab | `path`, `project` (configured id or `namespace/project`), `ref` | `change_status` — delta runs only |
| Bitbucket | `path`, `repo` (`workspace/slug`), `ref` | `change_status` — diffstat (delta) runs only |
| Confluence | `page_id`, `version` | `space` — only when `SpaceKey` scoped the run; the API response does not carry it, so an unscoped run has no space to report |
| Jira | `issue_key`, `project`, `status`, `updated_at` | `priority` — when set; `assignee` — when assigned |
| Notion | `page_id`, `updated_at` | — (no container key; see the caveats) |
| Asana | `workspace`, `completed` (`"true"`/`"false"`) | `assignee`, `due_on`, `updated_at`; `project` — when `ProjectGid` narrowed the enumeration |
| Slack | `channel`, `channel_id`, `date` (`yyyy-MM-dd` — the day this rollup covers), `message_count` | — |
| Microsoft Teams | `team_id`, `channel_id`, `channel`, `date` (`yyyy-MM-dd` — the day this rollup covers), `message_count` | — |
| Gmail | `date` (ISO-8601), `has_attachments` | `from` — when the message has a `From` header |
| Exchange / Outlook | `folder` (the Graph mail-folder id or well-known name being enumerated, e.g. `inbox`), `has_attachments` | `received_at` (ISO-8601) — when Graph returned `receivedDateTime` |
| Linear | `url` | `team` (team key); `state` **and** `state_type` together — when the issue has a workflow state; `project` (project name); `comments_truncated` = `"true"` — only when the issue's comments exceeded the fetched page, never `"false"` |
| Zendesk (Tickets) | `ticket_id`, `status`, `updated_at`, `subdomain` | `priority` — when set |
| Zendesk (Articles) | `article_id`, `updated_at`, `subdomain` | `section_id` — when the article belongs to a Help Center section |
| Airtable | `base_id`, `table`, `record_id` | attachment entries additionally carry `field` (the source field name) and `attachment_id` |
| Web — Crawler | `url`, `depth` (BFS distance from the seed; the seed is `"0"`), `host` | — |
| Web — RSS / Atom | `url` | `author`; `published_at` — normalised to ISO-8601 when the feed's timestamp parses, otherwise passed through verbatim |
| Web — Sitemap | `url` | `lastmod` — passed through verbatim |

`LocalFilesDataProvider` emits no tags of its own; its documents carry `provider_id` only.

### Reserved keys

Seven keys are written (or read) by the framework itself and must never be emitted by a connector:

| Key | Written by | Read by |
|-----|-----------|---------|
| `document_id` | `MetadataBehavior` | — |
| `file_name` | `MetadataBehavior` | sanitisers, for diagnostics |
| `created_at` | `MetadataBehavior` | `TimeWeightedRetriever` |
| `provider_id` | `IngestFromProviderAsync` | — |
| `_parentKey` | parent/child chunking | parent-document retrieval |
| `allowed_roles` | *nobody* — supplied by the caller | `RbacRetrievalGuard` |
| `trust_level` | *nobody* — supplied by the caller | `TrustLevelRetrievalGuard` |

A connector that emits one of these throws `ReservedMetadataKeyException` out of `IngestFromProviderAsync`, naming the offending key, the provider id and the entry id.

**Why it throws rather than collecting a per-entry error.** Everywhere else in provider ingestion a failure becomes a `Result` in `ProviderIngestionResult.Errors` and the run continues. A reserved-key collision is different in kind: a connector's tag keys are string literals in connector code, so the collision is deterministic and repeats identically for *every* document in the run. Collecting it would produce N copies of one authoring bug — and, worse, would ship the corruption it describes, because `MetadataBehavior` applies connector tags **first** with `TryAdd`: a connector tag named `created_at` does not lose to the framework value, it *shadows* it, and `TimeWeightedRetriever` then ranks on connector data with no warning. This is a programming error, not a data error, so it surfaces on the first document.

Consequences worth knowing:

- **It arrives unwrapped.** Even under parallel ingestion — `Parallel.ForEachAsync` faults through an `AggregateException`, but awaiting unwraps it — so `catch (ReservedMetadataKeyException)` is enough; no `AggregateException` handling is needed.
- **Ingestion is left partially complete.** Entries processed before the collision surfaced stay ingested (and hash-recorded). Because the method throws rather than returns, the accumulated error bag is discarded and `CleanupMode.Full` cleanup is skipped — **nothing is deleted**.
- **Re-running after the fix is safe.** Whatever was ingested was collision-free by definition, and the hash store skips it as unchanged on the next run.

### Precedence

Tags are assembled in three passes:

1. `baseMetadata.Tags` (the `DocumentMetadata` you optionally pass to `IngestFromProviderAsync`).
2. The entry's own connector metadata — **wins** over base metadata on collision.
3. `provider_id` — written last, **wins over both**.

Only step 2 is reserved-key guarded. Base metadata is deliberately left unguarded, and this asymmetry is intentional: base metadata is the sanctioned — and only — channel for setting `allowed_roles` and `trust_level`, two reserved keys the framework never writes and only ever *reads*, in the RBAC and trust-level retrieval guards. Guarding base metadata would break RBAC and trust-level tagging outright.

### Caveats before you write a filter

**`change_status` can never be `removed`.** The vocabulary is normalised to `added` / `modified` / `removed` / `renamed` across GitHub, GitLab, Bitbucket and Box, and all four map `removed` — but no connector can ever emit it. Each one filters deleted entries out *before* building a handle, which is correct: a deleted file has no content to chunk. `removed` exists so the vocabulary is complete, not because it is reachable. A `change_status = "removed"` filter will never match anything; use the pipeline's `CleanupMode.Full` deletion path instead.

**Box's most common delta event carries no `change_status`.** Box raises a single `UPLOAD` event both for a brand-new file and for a new version of an existing file, and nothing verifiable in the payload distinguishes them. Since `added` and `modified` are disjoint in this vocabulary, guessing either would be outright false half the time — so the key is omitted. In practice `change_status` appears on Box only for `COPY` events.

**`path` and `parent_path` are not interchangeable.** Across the file/blob connectors `path` is the *file's own* full path — the value you would filter with `path` starts-with `docs/`. OneDrive and SharePoint emit **`parent_path`** instead, because Graph's `DriveItem` exposes `ParentReference.Path`, which is the *containing folder* and carries a `/drive/root:` namespace prefix. Filing that under `path` would make a cross-connector `path` filter silently match nothing on those two connectors.

**`updated_at` is not comparable across connectors.** Asana, Jira, Notion and Zendesk each write `updated_at` straight through from their API in whatever format that vendor returns — the connector does not reformat it. The values are useful for exact-match and for per-connector ordering when a vendor's format happens to sort lexically, but a cross-connector range filter over `updated_at` is not sound. RSS's `published_at` is the better-behaved case: it is normalised to ISO-8601 **whenever the feed's timestamp parses** (Atom carries ISO-8601, RSS 2.0 carries RFC 822 `pubDate`; both are parsed and re-rendered), and is ordered and comparable for those entries. A timestamp that does not parse — a hand-written `pubDate` such as `sometime last Tuesday` — is passed through **verbatim** rather than dropped, so a feed with malformed dates can still yield unsortable values on this key. Sitemap's `lastmod` is passed through verbatim by design, because the sitemap protocol permits both a full W3C datetime and a bare date and normalising would discard which precision the site published.

**`date` means two different things, in two different formats.** Slack and Teams emit `date` as `yyyy-MM-dd` — a *day-bucket label* identifying the day their per-day rollup document covers. Gmail emits `date` as a full ISO-8601 round-trip timestamp — the *instant* a single message carries. A cross-connector `date` filter therefore compares `2026-03-01` against `2026-03-01T10:00:00.0000000+00:00`, which matches nothing and sorts wrongly. This is more treacherous than the `updated_at` case above precisely because the values *look* comparable: filter `date` per connector, never across them.

**Several other keys are per-connector-scoped rather than globally comparable** — each table row is correct on its own, but a filter written against one connector's meaning will not transfer to another: `project` carries four unrelated meanings (GitLab's configured id or `namespace/project`, Jira's project key, Asana's `ProjectGid`, Linear's project *name*), `folder` two (Dropbox's configured root path vs Exchange's Graph mail-folder id), and `status` two disjoint vocabularies (Jira status names vs Zendesk ticket statuses).

**Notion has no container key.** It is the one record connector without one. `NotionOptions.DatabaseId` is not a filter the connector applies — the `POST /v1/search` request it issues returns every accessible page and accepts no database scope — so tagging pages with it would write a database id onto documents provably not in that database, and `HasTagSpec("database_id", …)` would return the wrong documents with no signal that anything was off. The absence is deliberate; the key becomes available honestly once the connector queries `/v1/databases/{id}/query`.

**Jira's `project` comes from the issue, not from options.** It is derived from the issue key (`ENG-42` → `ENG`), so it is present on unscoped runs, stays correct when an issue moves between projects, and stays correct under a custom `Jql` spanning several projects — where a single options-derived value would be wrong for most results. Confluence's `space` is the opposite case: it comes from `SpaceKey` and is simply absent when the run is unscoped, because the API response does not carry the space.

**Gmail's `from` is the full display form.** `"Alice" <alice@example.com>`, not a bare address — it is the message's `From` header as MailKit renders it. Match on it with a substring predicate rather than equality.

---

## Error handling

| Condition | Behaviour |
|-----------|-----------|
| 401 Unauthorized | Exception propagated to the caller; check credentials and token expiry |
| 403 Forbidden | Exception propagated; ensure the service principal or token has read access to the resource |
| Stale delta token (SharePoint / OneDrive) | Connector catches `resyncRequired` / `itemNotFound` and falls back to full traversal automatically |
| Stale delta token (other connectors) | Provider-specific behaviour; refer to the SDK documentation for the connector |
| Stale ETag (Azure Blob) | No special handling needed — a changed or absent ETag causes the blob to be re-ingested, not skipped |
| Azure SDK transient errors | Handled by the Azure SDK's built-in retry policy; do not add an external retry policy on top |
| 429 Too Many Requests (Confluence / Jira) | Atlassian rate limits; the ZeroAlloc.Rest HTTP client retries automatically via the resilience pipeline |
| 429 Too Many Requests (Notion) | Notion API rate-limits at ~3 requests/second; the resilience pipeline retries with back-off |
| 429 Too Many Requests (Asana / Slack) | Handled by the resilience pipeline; consider reducing concurrency if limits are hit frequently |
| Slack `invalid_auth` / `token_revoked` | Exception propagated; re-issue the bot token and redeploy |
| Microsoft Teams Graph errors | Handled the same way as SharePoint/OneDrive Graph errors; check app permissions (`ChannelMessage.Read.All`) |
| Gmail IMAP connection refused | Check that IMAP is enabled for the mailbox and that the OAuth2 token has the `https://mail.google.com/` scope |
| Exchange Graph errors | Surface as `RagError.HttpFailed` results; check the app registration has the `Mail.Read` application permission with admin consent |
| Exchange `NoParserFound (message/rfc822)` | Register `AddEmailParser()` — the connector emits raw `.eml` entries by design |
| 429 Too Many Requests (Linear) | Retried by the resilience pipeline; query-complexity rejections instead surface as GraphQL-error failures (`RagError.HttpFailed` naming the messages) — reduce `PageSize` |
| 429 Too Many Requests (GitLab) | GitLab rate-limits at 300–2000 requests/min depending on tier; the resilience pipeline retries with back-off |
| 401 Unauthorized (GitLab) | Verify the `PRIVATE-TOKEN` is valid and has `read_repository` scope |
| 429 Too Many Requests (Bitbucket) | Bitbucket Cloud rate-limits at 1000 requests/hour; the resilience pipeline retries with back-off |
| 401 Unauthorized (Bitbucket) | Verify the app password is valid and has `repository:read` permission |
| 429 Too Many Requests (Zendesk) | Zendesk rate-limits vary by plan; the resilience pipeline retries with back-off |
| 401 Unauthorized (Zendesk) | Verify the `email/token:apiToken` combination is correct |
| 422 Unprocessable Entity (Airtable) | Check `filterByFormula` syntax and field names; the Last Modified field name must match exactly |
| 429 Too Many Requests (Airtable) | Airtable rate-limits at 5 requests/second per base; the resilience pipeline retries with back-off |
| 401 Unauthorized (Airtable) | Verify the personal access token is valid and has access to the specified base |
| LLM / embedding failures during ingestion | Propagated from the core pipeline — not connector-specific |

---

## Implementation notes

**Box and Dropbox metadata coverage.** Every connector's metadata keys are pinned by a test, but Box and Dropbox are pinned one level in: `BoxClient` and `DropboxClient` are concrete SDK types with no injectable transport, so their enumeration paths cannot be driven offline. Their tests call the internal `ToHandle` helper directly. That pins the emitted keys and values, but **not** the call-site argument wiring — a mistake in which `folderId` or `changeStatus` an enumeration path passes would not be caught. The other 19 connectors are exercised through their enumeration paths.

**Static request headers** (`Accept: application/json`, `Notion-Version: 2022-06-28`) are set via `HttpClient.DefaultRequestHeaders` in each connector's registration method. The ZeroAlloc.Rest 0.2.0 `[Header]` attribute only supports method- and parameter-level targets, not interface-level — so headers cannot be declared on the API interface directly. This will be revisited when class-level header support is added to the library.
