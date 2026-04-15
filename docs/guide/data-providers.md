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
| GitLab | Commit SHA (string) | HEAD commit SHA at last successful ingest; compare API returns changed files |
| Bitbucket | Commit hash (string) | HEAD commit hash at last successful ingest; diffstat API returns changed files |
| Zendesk | Unix epoch (string) | Passed as `start_time` to the incremental export API |
| Airtable | ISO 8601 timestamp (string) | Used in `filterByFormula` against the Last Modified field |
| Azure Blob | Not applicable | Uses per-file ETag comparison rather than a cursor |

> Azure Blob Storage does not use a `DeltaToken` cursor. Instead, the pipeline's content hash store compares each blob's ETag against the stored value. A stale ETag simply means the blob is re-ingested — no data is lost.

> For SharePoint and OneDrive, stale or expired delta tokens (Graph error codes `resyncRequired` or `itemNotFound`) cause the connector to automatically fall back to a full traversal. No intervention is needed.

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

**Static request headers** (`Accept: application/json`, `Notion-Version: 2022-06-28`) are set via `HttpClient.DefaultRequestHeaders` in each connector's registration method. The ZeroAlloc.Rest 0.2.0 `[Header]` attribute only supports method- and parameter-level targets, not interface-level — so headers cannot be declared on the API interface directly. This will be revisited when class-level header support is added to the library.
