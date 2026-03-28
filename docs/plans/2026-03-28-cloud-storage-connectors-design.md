# Cloud Storage Connectors Design

**Date:** 2026-03-28

## Overview

Add six cloud storage connectors that expose `IFileContentProvider` so any Rag.NET pipeline can ingest files from Azure Blob Storage, SharePoint, OneDrive, Google Drive, Dropbox, and Box. A new shared `Rag.NET.DataProviders` package provides the OAuth token abstraction, base class, and resilience wiring that all connectors build on.

---

## Package Structure

```
src/
  Rag.NET.DataProviders/              ← new: shared foundation
  Rag.NET.DataProviders.AzureBlob/    ← new
  Rag.NET.DataProviders.SharePoint/   ← new
  Rag.NET.DataProviders.OneDrive/     ← new
  Rag.NET.DataProviders.GoogleDrive/  ← new
  Rag.NET.DataProviders.Dropbox/      ← new
  Rag.NET.DataProviders.Box/          ← new
  Rag.NET.DataProviders.GitHub/       ← existing — migrated to base class (non-breaking)
  Rag.NET.DataProviders.Web/          ← existing — no changes
```

---

## Shared Package — `Rag.NET.DataProviders`

### Token Provider

```csharp
public interface ITokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

// Pre-issued token: API key, PAT, SAS token
public sealed class StaticTokenProvider(string token) : ITokenProvider;

// Client credentials OAuth 2.0 — auto-fetches and refreshes
public sealed class OAuthClientCredentialsTokenProvider : ITokenProvider
{
    public OAuthClientCredentialsTokenProvider(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string[]? scopes = null,
        HttpClient? httpClient = null);
}
```

`OAuthClientCredentialsTokenProvider` caches the token and refreshes it proactively 60 seconds before expiry.

### Options Base

```csharp
public abstract class CloudStorageOptions
{
    /// File extensions to include. Defaults to ["*"] (all).
    public IReadOnlyList<string> Extensions { get; init; } = ["*"];

    /// Optional predicate to exclude files by path.
    public Func<string, bool>? Filter { get; init; }

    /// Opaque cursor string enabling delta runs (connector-specific format).
    /// Null = full traversal. Set to the value returned by the previous run.
    public string? DeltaToken { get; init; }
}
```

### FileHandle (internal transfer type)

```csharp
public sealed record FileHandle(
    string Id,
    string FileName,
    string? ETag,
    Func<CancellationToken, Task<Stream>> OpenAsync);
```

### Base Class

```csharp
public abstract class FileContentProviderBase : IFileContentProvider
{
    /// Connectors implement this — enumerate FileHandle objects from the vendor SDK.
    /// No filtering or watermark logic required.
    protected abstract IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken);

    /// Applies Extensions + Filter, wraps into FileEntry, yields results.
    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default);
}
```

### DI Helper

```csharp
// Registers a named HttpClient with standard resilience for use by connectors
public static IHttpClientBuilder AddDataProviderHttpClient(
    this IServiceCollection services,
    string name)
    => services.AddHttpClient(name).AddStandardResilienceHandler();
```

---

## Connector Designs

### Azure Blob Storage (`Rag.NET.DataProviders.AzureBlob`)

**SDK:** `Azure.Storage.Blobs`

**Auth:**
- Connection string → `new BlobContainerClient(connectionString, containerName)`
- OAuth / managed identity → `new BlobContainerClient(uri, new ClientSecretCredential(...))`

**Options:**
```csharp
public sealed class AzureBlobOptions : CloudStorageOptions
{
    public required string ContainerName { get; init; }
}
```

**Delta:** The `DeltaToken` stores the most recent blob `ETag`. On delta runs, blobs whose `ETag` matches the stored value are skipped.

**Resilience:** Configured via `BlobClientOptions.Retry` (Azure SDK built-in). No `IHttpClientFactory` wrapping to avoid competing retry loops.

**DI:**
```csharp
// Connection string
services.AddAzureBlobDataProvider(connectionString, containerName, options => { ... });

// OAuth / managed identity
services.AddAzureBlobDataProvider(tokenCredential, accountUri, containerName, options => { ... });
```

---

### SharePoint (`Rag.NET.DataProviders.SharePoint`)

**SDK:** `Microsoft.Graph`

**Auth:** `ClientSecretCredential` or any `TokenCredential` passed to `GraphServiceClient`.

**Options:**
```csharp
public sealed class SharePointOptions : CloudStorageOptions
{
    public required string SiteId  { get; init; }
    public required string DriveId { get; init; }
}
```

**Delta:** Graph `/drive/root/delta` endpoint returns a `@odata.deltaLink` token stored as `DeltaToken`. Subsequent runs call the delta URL directly, receiving only changed items.

**Resilience:** `IHttpClientFactory` named client with `AddStandardResilienceHandler()` injected into `GraphServiceClient`.

**DI:**
```csharp
services.AddSharePointDataProvider(tenantId, clientId, clientSecret, siteId, driveId, options => { ... });
```

---

### OneDrive (`Rag.NET.DataProviders.OneDrive`)

**SDK:** `Microsoft.Graph`

**Auth:** Same as SharePoint — `TokenCredential` → `GraphServiceClient`.

**Options:**
```csharp
public sealed class OneDriveOptions : CloudStorageOptions
{
    public required string UserId { get; init; }  // or "me" for delegated auth
}
```

**Delta:** Graph `/users/{userId}/drive/root/delta` — same `deltaLink` mechanism as SharePoint.

**Resilience:** Same `IHttpClientFactory` + `AddStandardResilienceHandler()` pattern.

**DI:**
```csharp
services.AddOneDriveDataProvider(tenantId, clientId, clientSecret, userId, options => { ... });
```

---

### Google Drive (`Rag.NET.DataProviders.GoogleDrive`)

**SDK:** `Google.Apis.Drive.v3`

**Auth:** `ServiceAccountCredential` (server-to-server) or `UserCredential` (OAuth 2.0).

**Options:**
```csharp
public sealed class GoogleDriveOptions : CloudStorageOptions
{
    /// Google Drive folder ID to enumerate. Null = entire drive.
    public string? FolderId { get; init; }
}
```

**Delta:** `Changes.List` API with a `pageToken`. The token is stored as `DeltaToken`; subsequent runs call `Changes.List(pageToken)` to receive only modified files.

**Resilience:** `IHttpClientFactory` named client with `AddStandardResilienceHandler()` passed to `DriveService` via `BaseClientService.Initializer.HttpClientFactory`.

**DI:**
```csharp
// Service account (JSON key file path)
services.AddGoogleDriveDataProvider(serviceAccountKeyPath, options => { ... });

// OAuth user credential
services.AddGoogleDriveDataProvider(userCredential, options => { ... });
```

---

### Dropbox (`Rag.NET.DataProviders.Dropbox`)

**SDK:** `Dropbox.Api`

**Auth:** Static access token or OAuth 2.0 refresh token. `ITokenProvider` wraps either — `DropboxClient` is constructed per-call with the current token.

**Options:**
```csharp
public sealed class DropboxOptions : CloudStorageOptions
{
    /// Dropbox folder path to enumerate. Null = root ("/").
    public string? FolderPath { get; init; }
}
```

**Delta:** `ListFolder` cursor stored as `DeltaToken`. Subsequent runs call `ListFolderContinue(cursor)` to receive only changed entries.

**Stale cursor:** Dropbox cursors do not expire. No stale-cursor fallback needed.

**Resilience:** `IHttpClientFactory` named client with `AddStandardResilienceHandler()` injected into `DropboxClient` constructor.

**DI:**
```csharp
services.AddDropboxDataProvider(accessToken, options => { ... });
services.AddDropboxDataProvider(tokenProvider, options => { ... });
```

---

### Box (`Rag.NET.DataProviders.Box`)

**SDK:** `Box.V2`

**Auth:** JWT service account (`BoxJWTAuth`) or OAuth 2.0 (`BoxOAuth2Auth`). `ITokenProvider` wraps either.

**Options:**
```csharp
public sealed class BoxOptions : CloudStorageOptions
{
    /// Box folder ID to enumerate. "0" = root.
    public string RootFolderId { get; init; } = "0";
}
```

**Delta:** Box Events API with a stream position cursor stored as `DeltaToken`. On delta runs, only `UPLOAD` and `UPDATE` events since the last cursor are processed.

**Stale cursor:** Box stream positions do not expire.

**Resilience:** `IHttpClientFactory` named client with `AddStandardResilienceHandler()` passed to `BoxConfig`.

**DI:**
```csharp
services.AddBoxDataProvider(jwtConfig, options => { ... });
services.AddBoxDataProvider(tokenProvider, options => { ... });
```

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| Transient HTTP failure (429, 503, network) | Handled by vendor SDK retry (Azure Blob) or `AddStandardResilienceHandler` (all others) |
| 401 / 403 | Propagated as-is — caller decides to log/skip or fail |
| Stale / expired `DeltaToken` | Connector catches the platform-specific error, logs a warning, falls back to full traversal |
| Empty file | Yielded as a `FileEntry` with an empty stream — pipeline handles it downstream |

---

## Testing

- `Rag.NET.DataProviders` — unit tests for `FileContentProviderBase` filtering, extension matching, and `OAuthClientCredentialsTokenProvider` token fetch + refresh (mock HTTP handler).
- Each connector — unit tests with mocked vendor SDK client covering: full traversal, delta traversal, extension filtering, stale-cursor fallback.
- No live cloud calls in CI.

---

## Migration: `Rag.NET.DataProviders.GitHub`

`GitHubDataProvider` is refactored to extend `FileContentProviderBase`. The public API and `FileEntry` shape are unchanged — no breaking change for existing users.
