# Data Providers + Content-Hash Record Manager — Design

**Date:** 2026-03-17
**Status:** Approved

---

## Goal

Decouple "where files come from" from "how to ingest them" via an `IFileContentProvider` abstraction, and add a Content-Hash Record Manager that skips unchanged documents across restarts. Covers local files, web (crawler, sitemap, RSS), and GitHub as first-party providers.

## Architecture

Three packages, two new:

- **`Rag.NET` (core)** — `IFileContentProvider`, `FileEntry`, `IContentHashStore`, `SqliteContentHashStore`, `UseContentHashRecordManager(dbPath)`, `IngestFromProviderAsync` extension, `LocalFilesDataProvider`
- **`Rag.NET.DataProviders.Web`** — `WebCrawlerDataProvider`, `SitemapDataProvider`, `RssDataProvider`
- **`Rag.NET.DataProviders.GitHub`** — `GitHubDataProvider` (Octokit)

`IngestFromProviderAsync` is a static extension on `IRagPipeline`. `IContentHashStore` is opt-in — if `UseContentHashRecordManager` is never called the extension still works, it just doesn't skip unchanged files.

```
IFileContentProvider ──→ IngestFromProviderAsync ──→ IRagPipeline.IngestAsync
                                  │
                         IContentHashStore? (opt-in)
```

---

## Abstractions

### `IFileContentProvider`

```csharp
public interface IFileContentProvider
{
    IAsyncEnumerable<FileEntry> GetFilesAsync(CancellationToken cancellationToken = default);
}

public record FileEntry(
    string Id,                                               // stable identifier — path, URL, GitHub path
    string FileName,                                         // used for MIME/parser detection
    Func<CancellationToken, Task<Stream>> OpenContentAsync,  // lazy — only called if content is needed
    string? ETag = null,                                     // cheap provider-supplied fingerprint
    IReadOnlyDictionary<string, string>? Metadata = null     // forwarded to DocumentMetadata
);
```

`OpenContentAsync` is only called when the ETag check fails or no ETag is present. Each provider supplies an ETag where it is cheap to compute:

| Provider | ETag value |
|---|---|
| `LocalFilesDataProvider` | `"{lastWriteUtc.Ticks}:{fileSize}"` — no I/O |
| `SitemapDataProvider` | `<lastmod>` element if present |
| `RssDataProvider` | `<guid>` + `<pubDate>` |
| `GitHubDataProvider` | blob SHA — Git's own content hash |
| `WebCrawlerDataProvider` | none |

---

### `IContentHashStore`

```csharp
public interface IContentHashStore
{
    Task<string?> GetETagAsync(string providerId, string entryId, CancellationToken ct = default);
    Task<string?> GetHashAsync(string providerId, string entryId, CancellationToken ct = default);
    Task SetAsync(string providerId, string entryId, string etag, string hash, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetAllIdsAsync(string providerId, CancellationToken ct = default);
    Task RemoveAsync(string providerId, string entryId, CancellationToken ct = default);
}

public enum CleanupMode { None, Full }
```

`SqliteContentHashStore` backs this with a single `content_hashes` table:

| Column | Type |
|---|---|
| `provider_id` | TEXT |
| `entry_id` | TEXT |
| `etag` | TEXT |
| `hash` | TEXT |
| `updated_at` | TEXT (ISO-8601) |

Registered via `builder.UseContentHashRecordManager(dbPath)`.

---

### `IngestFromProviderAsync`

```csharp
public static async Task<ProviderIngestionResult> IngestFromProviderAsync(
    this IRagPipeline pipeline,
    IFileContentProvider provider,
    string providerId,
    DocumentMetadata? metadata = null,
    IngestionOptions? options = null,
    CleanupMode cleanupMode = CleanupMode.None,
    IProgress<IngestionProgress>? progress = null,
    CancellationToken cancellationToken = default);

public record ProviderIngestionResult(
    int Ingested,
    int Skipped,
    int Deleted,
    IReadOnlyList<string> Errors
);
```

**Algorithm:**

```
1. knownIds ← store.GetAllIdsAsync(providerId)     // for CleanupMode.Full
2. seenIds  ← ∅

foreach entry in provider.GetFilesAsync():
    seenIds.Add(entry.Id)

    if entry.ETag != null && entry.ETag == store.GetETagAsync(providerId, entry.Id):
        skip                                        // ETag match — no content fetch

    stream ← entry.OpenContentAsync()              // fetch content
    hash   ← SHA-256(stream)

    if hash == store.GetHashAsync(providerId, entry.Id):
        store.SetAsync(..., etag: entry.ETag, ...)  // refresh ETag, skip ingest
        skip

    pipeline.IngestAsync(stream, metadata + entry.Metadata, options)
    store.SetAsync(providerId, entry.Id, entry.ETag, hash)

if CleanupMode.Full:
    foreach id in (knownIds − seenIds):
        pipeline.DeleteAsync(id)
        store.RemoveAsync(providerId, id)
```

If no `IContentHashStore` is registered, all ETag/hash checks are skipped and every file is ingested.

---

## Provider implementations

### `LocalFilesDataProvider` (core)

```csharp
new LocalFilesDataProvider("/data/docs", new LocalFilesOptions
{
    Extensions   = [".pdf", ".docx", ".md"],
    SearchOption = SearchOption.AllDirectories,
    Filter       = path => !path.Contains(".git"),
})
```

- `Id` = absolute path
- `ETag` = `"{lastWriteUtc.Ticks}:{length}"`
- No I/O until `OpenContentAsync`

---

### `SitemapDataProvider` (`Rag.NET.DataProviders.Web`)

```csharp
new SitemapDataProvider("https://example.com/sitemap.xml", httpClient)
```

- Fetches sitemap XML; follows `<sitemapindex>` links recursively
- `Id` = URL
- `ETag` = `<lastmod>` if present
- `FileName` inferred from URL path

---

### `RssDataProvider` (`Rag.NET.DataProviders.Web`)

```csharp
new RssDataProvider("https://example.com/feed.rss", httpClient)
```

- Parses RSS 2.0 and Atom
- `Id` = `<guid>` or `<link>`
- `ETag` = `<pubDate>` / `<updated>`
- `FileName` = `"{id}.html"`

---

### `WebCrawlerDataProvider` (`Rag.NET.DataProviders.Web`)

```csharp
new WebCrawlerDataProvider("https://example.com/docs", httpClient, new WebCrawlerOptions
{
    MaxDepth         = 3,
    MaxPages         = 200,
    SameDomain       = true,
    RespectRobotsTxt = true,
})
```

- BFS link-following; uses `Rag.NET.Parsers.Html` to extract links
- `Id` = URL
- No `ETag`
- `FileName` = sanitised URL path + `.html`

---

### `GitHubDataProvider` (`Rag.NET.DataProviders.GitHub`)

```csharp
new GitHubDataProvider("owner", "repo", githubClient, new GitHubDataProviderOptions
{
    Branch                = "main",
    Extensions            = [".md", ".cs"],
    Filter                = path => !path.StartsWith("docs/plans/"),
    LastIngestedCommitSha = "abc123",   // null → full tree traversal on first run
})
```

- First run: full tree via `git/trees?recursive=1`
- Subsequent runs: `repos/{owner}/{repo}/commits` since `LastIngestedCommitSha` — only changed files fetched
- `Id` = file path; `ETag` = blob SHA
- `LastIngestedCommitSha` updated by caller after successful run

---

## Testing

### `Rag.NET.Tests` (existing)

- `LocalFilesDataProvider` — temp directory; assert `Id`, `ETag`, `FileName`; delta: modify one file, assert only that entry changes
- `ContentHashStore` — SQLite round-trip: set, get, remove, `GetAllIdsAsync`
- `IngestFromProviderAsync` — fake provider + fake pipeline; assert ETag-skip, hash-skip, ingest on new/changed, delete on `CleanupMode.Full`

### `Rag.NET.DataProviders.Web.Tests` (new project)

- `SitemapDataProvider` — mock `HttpClient`; sitemap index recursion; `<lastmod>` ETag
- `RssDataProvider` — RSS 2.0 and Atom; `<guid>` Id; `<pubDate>` ETag
- `WebCrawlerDataProvider` — static 3-page site; BFS discovers expected URLs; `MaxDepth` / `MaxPages` limits; `SameDomain` excludes externals; `robots.txt` disallow

### `Rag.NET.DataProviders.GitHub.Tests` (new project)

- Full-tree run: mock Octokit 5-file tree; assert all 5 entries, ETag = blob SHA
- Delta run: mock commits-since-SHA 2 changed files; assert only 2 entries
- Extension filter: assert only matching extensions returned
