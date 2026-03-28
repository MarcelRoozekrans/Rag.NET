---
id: data-providers
title: Data Providers
sidebar_label: Data Providers
sidebar_position: 10
---

# Data Providers

Data providers are connectors that enumerate remote files and stream their content into the Rag.NET ingestion pipeline. They implement `IFileContentProvider`, which the pipeline calls during `IngestFromProviderAsync` to receive a sequence of `FileEntry` objects — each carrying a stable ID, a filename, an optional ETag for deduplication, and a factory that opens the file as a stream.

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
| LLM / embedding failures during ingestion | Propagated from the core pipeline — not connector-specific |
