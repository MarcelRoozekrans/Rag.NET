# Data Providers + Content-Hash Record Manager — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `IFileContentProvider` abstraction, `LocalFilesDataProvider`, web providers (crawler, sitemap, RSS), `GitHubDataProvider`, and a `SqliteContentHashStore` that makes `IngestFromProviderAsync` skip unchanged files across restarts.

**Architecture:** `IFileContentProvider` yields lazy `FileEntry` objects; `IngestFromProviderAsync` (extension on `IRagPipeline`) checks ETags and SHA-256 content hashes against `IContentHashStore` (SQLite-backed) before calling `IngestAsync`. `CleanupMode.Full` deletes disappeared documents. Web and GitHub providers live in separate packages. All new code follows the existing patterns: `SqliteStoreHelper.OpenConnection`, `TreatWarningsAsErrors`, xUnit v3 + NSubstitute.

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite` (already in core), `AngleSharp` (already in `Rag.NET.Parsers.Html`, add to web project), `Octokit` NuGet for GitHub, `System.Xml.Linq` (BCL) for XML parsing, xUnit v3, NSubstitute.

---

### Task 1: Core abstractions — `IFileContentProvider`, `FileEntry`, `CleanupMode`, `ProviderIngestionResult`

**Files:**
- Create: `src/Rag.NET/DataProviders/IFileContentProvider.cs`
- Create: `src/Rag.NET/DataProviders/FileEntry.cs`
- Create: `src/Rag.NET/DataProviders/CleanupMode.cs`
- Create: `src/Rag.NET/DataProviders/ProviderIngestionResult.cs`

**Step 1: Create the four files**

`src/Rag.NET/DataProviders/IFileContentProvider.cs`:
```csharp
namespace Rag.NET.DataProviders;

/// <summary>
/// Provides a stream of file entries from an arbitrary source (local disk, web, GitHub, etc.).
/// </summary>
public interface IFileContentProvider
{
    IAsyncEnumerable<FileEntry> GetFilesAsync(CancellationToken cancellationToken = default);
}
```

`src/Rag.NET/DataProviders/FileEntry.cs`:
```csharp
namespace Rag.NET.DataProviders;

/// <summary>
/// Represents a single file from an <see cref="IFileContentProvider"/>.
/// Content is loaded lazily — <see cref="OpenContentAsync"/> is only called when the file needs to be ingested.
/// </summary>
/// <param name="Id">Stable identifier for this file (absolute path, URL, or GitHub path).</param>
/// <param name="FileName">File name used for MIME/parser detection (e.g. <c>"report.pdf"</c>).</param>
/// <param name="OpenContentAsync">Opens a stream of the file's content. Caller is responsible for disposal.</param>
/// <param name="ETag">
/// Optional cheap provider-supplied fingerprint (last-modified+size, <c>&lt;lastmod&gt;</c>, blob SHA, etc.).
/// When the stored ETag matches, content is not fetched at all.
/// </param>
/// <param name="Metadata">Optional key/value pairs forwarded to <see cref="Rag.NET.Models.DocumentMetadata.Tags"/>.</param>
public sealed record FileEntry(
    string Id,
    string FileName,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    string? ETag = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

`src/Rag.NET/DataProviders/CleanupMode.cs`:
```csharp
namespace Rag.NET.DataProviders;

/// <summary>Controls whether disappeared documents are deleted from the vector store.</summary>
public enum CleanupMode
{
    /// <summary>No cleanup — disappeared documents are left in the vector store.</summary>
    None,

    /// <summary>
    /// Full cleanup — documents present in the hash store but absent from the current provider
    /// enumeration are deleted from the vector store and removed from the hash store.
    /// Requires <see cref="Rag.NET.Abstractions.IContentHashStore"/> to be registered.
    /// </summary>
    Full,
}
```

`src/Rag.NET/DataProviders/ProviderIngestionResult.cs`:
```csharp
namespace Rag.NET.DataProviders;

/// <summary>Summary of a completed <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> run.</summary>
public sealed record ProviderIngestionResult(
    int Ingested,
    int Skipped,
    int Deleted,
    IReadOnlyList<string> Errors);
```

**Step 2: Build to verify compilation**

```bash
cd c:/Projects/Prive/Rag.NET
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Rag.NET/DataProviders/
git commit -m "feat: add IFileContentProvider, FileEntry, CleanupMode, ProviderIngestionResult abstractions"
```

---

### Task 2: `IContentHashStore` + `SqliteContentHashStore`

**Files:**
- Create: `src/Rag.NET/Abstractions/IContentHashStore.cs`
- Create: `src/Rag.NET/Storage/SqliteContentHashStore.cs`
- Test: `tests/Rag.NET.Tests/Storage/SqliteContentHashStoreTests.cs`

**Step 1: Write the failing tests first**

`tests/Rag.NET.Tests/Storage/SqliteContentHashStoreTests.cs`:
```csharp
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public sealed class SqliteContentHashStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-hash-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task SetAsync_ThenGetHash_ReturnsStoredHash()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", etag: null, hash: "abc123");
        var result = await sut.GetHashAsync("prov-1", "entry-1");
        Assert.Equal("abc123", result);
    }

    [Fact]
    public async Task SetAsync_WithETag_GetETagReturnsIt()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", etag: "etag-xyz", hash: "abc123");
        var result = await sut.GetETagAsync("prov-1", "entry-1");
        Assert.Equal("etag-xyz", result);
    }

    [Fact]
    public async Task GetHashAsync_UnknownEntry_ReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        var result = await sut.GetHashAsync("prov-1", "missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllIdsAsync_ReturnsScopedIds()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "a", null, "h1");
        await sut.SetAsync("prov-1", "b", null, "h2");
        await sut.SetAsync("prov-2", "c", null, "h3");

        var ids = await sut.GetAllIdsAsync("prov-1");

        Assert.Equal(2, ids.Count);
        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
        Assert.DoesNotContain("c", ids);
    }

    [Fact]
    public async Task RemoveAsync_EntryGone_GetHashReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", null, "abc123");
        await sut.RemoveAsync("prov-1", "entry-1");
        var result = await sut.GetHashAsync("prov-1", "entry-1");
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingRow()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", null, "hash-v1");
        await sut.SetAsync("prov-1", "entry-1", null, "hash-v2");
        var result = await sut.GetHashAsync("prov-1", "entry-1");
        Assert.Equal("hash-v2", result);
    }

    [Fact]
    public async Task SurvivesRestart_DataPersistedToSqlite()
    {
        var sut1 = new SqliteContentHashStore(_dbPath);
        await sut1.SetAsync("prov-1", "entry-1", "etag-1", "hash-1");

        // Simulate restart — new instance, same db file
        var sut2 = new SqliteContentHashStore(_dbPath);
        Assert.Equal("hash-1", await sut2.GetHashAsync("prov-1", "entry-1"));
        Assert.Equal("etag-1", await sut2.GetETagAsync("prov-1", "entry-1"));
    }
}
```

**Step 2: Run the tests — they should fail to compile**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~SqliteContentHashStoreTests" 2>&1 | head -20
```
Expected: Build error — `SqliteContentHashStore` and `IContentHashStore` do not exist yet.

**Step 3: Create `IContentHashStore`**

`src/Rag.NET/Abstractions/IContentHashStore.cs`:
```csharp
namespace Rag.NET.Abstractions;

/// <summary>
/// Persists per-provider file identity records (ETag + SHA-256 hash) to enable
/// incremental ingestion — files unchanged since the last run are skipped.
/// </summary>
public interface IContentHashStore
{
    /// <summary>Returns the stored ETag for the entry, or <see langword="null"/> if unknown.</summary>
    Task<string?> GetETagAsync(string providerId, string entryId, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored SHA-256 content hash for the entry, or <see langword="null"/> if unknown.</summary>
    Task<string?> GetHashAsync(string providerId, string entryId, CancellationToken cancellationToken = default);

    /// <summary>Upserts the ETag and hash for an entry.</summary>
    Task SetAsync(string providerId, string entryId, string? etag, string hash, CancellationToken cancellationToken = default);

    /// <summary>Returns all entry IDs known for the given provider (used by <see cref="Rag.NET.DataProviders.CleanupMode.Full"/>).</summary>
    Task<IReadOnlySet<string>> GetAllIdsAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Removes a single entry record.</summary>
    Task RemoveAsync(string providerId, string entryId, CancellationToken cancellationToken = default);
}
```

**Step 4: Create `SqliteContentHashStore`**

`src/Rag.NET/Storage/SqliteContentHashStore.cs`:
```csharp
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;

namespace Rag.NET.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IContentHashStore"/>.
/// Stores ETag + SHA-256 hash per (providerId, entryId) pair in a <c>content_hashes</c> table.
/// </summary>
public sealed class SqliteContentHashStore : IContentHashStore
{
    private readonly string _dbPath;

    public SqliteContentHashStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        using var conn = SqliteStoreHelper.OpenConnection(dbPath);
        EnsureTable(conn);
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS content_hashes (
                provider_id TEXT NOT NULL,
                entry_id    TEXT NOT NULL,
                etag        TEXT,
                hash        TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                PRIMARY KEY (provider_id, entry_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Task<string?> GetETagAsync(string providerId, string entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT etag FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task<string?> GetHashAsync(string providerId, string entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task SetAsync(string providerId, string entryId, string? etag, string hash, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO content_hashes (provider_id, entry_id, etag, hash, updated_at)
            VALUES ($pid, $eid, $etag, $hash, $now)
            """;
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetAllIdsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_id FROM content_hashes WHERE provider_id = $pid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return Task.FromResult<IReadOnlySet<string>>(ids);
    }

    public Task RemoveAsync(string providerId, string entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
```

**Step 5: Run the tests — they should pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~SqliteContentHashStoreTests" -v minimal
```
Expected: All 7 tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Abstractions/IContentHashStore.cs src/Rag.NET/Storage/SqliteContentHashStore.cs tests/Rag.NET.Tests/Storage/SqliteContentHashStoreTests.cs
git commit -m "feat: add IContentHashStore and SqliteContentHashStore"
```

---

### Task 3: `UseContentHashRecordManager` on `RagBuilder`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Test: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Write the failing test**

Add to `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs` (open the file first to find the right location, then append):
```csharp
[Fact]
public void AddRagNet_WithContentHashRecordManager_RegistersIContentHashStore()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-di-{Guid.NewGuid():N}.db");
    try
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddRagNet(b => b.UseContentHashRecordManager(dbPath));

        using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IContentHashStore>();
        Assert.NotNull(store);
    }
    finally
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }
}
```

Also add the required usings at the top of the file:
```csharp
using Rag.NET.Abstractions;
using Rag.NET.Storage;
```

**Step 2: Run the test — should fail to compile**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AddRagNet_WithContentHashRecordManager" 2>&1 | head -10
```
Expected: Build error — `UseContentHashRecordManager` does not exist yet.

**Step 3: Add `UseContentHashRecordManager` to `RagBuilder`**

Open `src/Rag.NET/DependencyInjection/RagBuilder.cs` and add after `UseSqlitePersistence`:

```csharp
/// <summary>
/// Registers <see cref="SqliteContentHashStore"/> as the <see cref="IContentHashStore"/>.
/// When registered, <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> automatically skips
/// files that have not changed since the last ingestion run.
/// </summary>
/// <param name="dbPath">Path to the SQLite file. Created if it does not exist.</param>
public RagBuilder UseContentHashRecordManager(string dbPath)
{
    Services.AddSingleton<IContentHashStore>(_ => new SqliteContentHashStore(dbPath));
    return this;
}
```

Also add the missing using at the top of `RagBuilder.cs`:
```csharp
using Rag.NET.Abstractions;
using Rag.NET.Storage;
```

**Step 4: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AddRagNet_WithContentHashRecordManager" -v minimal
```
Expected: 1 test passes.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "feat: add RagBuilder.UseContentHashRecordManager()"
```

---

### Task 4: `LocalFilesDataProvider`

**Files:**
- Create: `src/Rag.NET/DataProviders/LocalFilesOptions.cs`
- Create: `src/Rag.NET/DataProviders/LocalFilesDataProvider.cs`
- Test: `tests/Rag.NET.Tests/DataProviders/LocalFilesDataProviderTests.cs`

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/DataProviders/LocalFilesDataProviderTests.cs`:
```csharp
using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.Tests.DataProviders;

public sealed class LocalFilesDataProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ragnet-local-{Guid.NewGuid():N}");

    public LocalFilesDataProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content = "hello")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task GetFilesAsync_ReturnsAllFiles_WhenNoFilter()
    {
        WriteFile("a.txt");
        WriteFile("b.txt");

        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task GetFilesAsync_FiltersByExtension()
    {
        WriteFile("a.md");
        WriteFile("b.txt");

        var sut = new LocalFilesDataProvider(_dir, new LocalFilesOptions { Extensions = [".md"] });
        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Single(entries);
        Assert.Equal("a.md", entries[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_Entry_HasAbsolutePathAsId()
    {
        var path = WriteFile("readme.md");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(path, entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_Entry_HasETagFromLastWriteAndSize()
    {
        WriteFile("readme.md", "some content");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync().ToListAsync();

        var info = new FileInfo(Path.Combine(_dir, "readme.md"));
        var expected = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        Assert.Equal(expected, entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_OpenContentAsync_ReturnsFileContents()
    {
        WriteFile("readme.md", "hello world");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync().ToListAsync();

        await using var stream = await entries[0].OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        Assert.Equal("hello world", content);
    }

    [Fact]
    public async Task GetFilesAsync_PredicateFilter_ExcludesMatchedFiles()
    {
        WriteFile("keep.md");
        WriteFile("skip.md");

        var sut = new LocalFilesDataProvider(_dir, new LocalFilesOptions
        {
            Filter = path => !path.Contains("skip"),
        });
        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Single(entries);
        Assert.Equal("keep.md", entries[0].FileName);
    }
}
```

**Step 2: Run tests — should fail to compile**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~LocalFilesDataProviderTests" 2>&1 | head -10
```
Expected: Build error.

**Step 3: Create `LocalFilesOptions`**

`src/Rag.NET/DataProviders/LocalFilesOptions.cs`:
```csharp
namespace Rag.NET.DataProviders;

/// <summary>Configuration for <see cref="LocalFilesDataProvider"/>.</summary>
public sealed class LocalFilesOptions
{
    /// <summary>
    /// File extensions to include (e.g. <c>[".md", ".pdf"]</c>).
    /// Defaults to <c>["*"]</c> which matches all extensions.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = ["*"];

    /// <summary>
    /// Whether to enumerate subdirectories. Defaults to <see cref="SearchOption.AllDirectories"/>.
    /// </summary>
    public SearchOption SearchOption { get; init; } = SearchOption.AllDirectories;

    /// <summary>
    /// Optional predicate to exclude files by absolute path.
    /// Return <see langword="false"/> to skip a file.
    /// </summary>
    public Func<string, bool>? Filter { get; init; }
}
```

**Step 4: Create `LocalFilesDataProvider`**

`src/Rag.NET/DataProviders/LocalFilesDataProvider.cs`:
```csharp
using System.Runtime.CompilerServices;

namespace Rag.NET.DataProviders;

/// <summary>
/// Enumerates files from a local directory as <see cref="FileEntry"/> objects.
/// ETag is computed cheaply from last-write timestamp and file size — no I/O until
/// <see cref="FileEntry.OpenContentAsync"/> is called.
/// </summary>
public sealed class LocalFilesDataProvider : IFileContentProvider
{
    private readonly string _rootPath;
    private readonly LocalFilesOptions _options;

    public LocalFilesDataProvider(string rootPath, LocalFilesOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
        _options = options ?? new LocalFilesOptions();
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // ensure async, avoid CS1998

        var files = Directory.EnumerateFiles(_rootPath, "*", _options.SearchOption);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!MatchesExtension(path)) continue;
            if (_options.Filter is not null && !_options.Filter(path)) continue;

            var info = new FileInfo(path);
            var etag = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
            var capturedPath = path;

            yield return new FileEntry(
                Id: path,
                FileName: Path.GetFileName(path),
                OpenContentAsync: _ => Task.FromResult<Stream>(File.OpenRead(capturedPath)),
                ETag: etag);
        }
    }

    private bool MatchesExtension(string path)
    {
        if (_options.Extensions is ["*"]) return true;
        var ext = Path.GetExtension(path);
        return _options.Extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, "*", StringComparison.Ordinal));
    }
}
```

**Step 5: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~LocalFilesDataProviderTests" -v minimal
```
Expected: All 6 tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/DataProviders/ tests/Rag.NET.Tests/DataProviders/
git commit -m "feat: add LocalFilesDataProvider with ETag fingerprinting"
```

---

### Task 5: `IngestFromProviderAsync` extension method

**Files:**
- Create: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`
- Test: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`:
```csharp
using System.Text;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.DataProviders;

public sealed class IngestFromProviderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-ingest-{Guid.NewGuid():N}.db");
    private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static IFileContentProvider MakeProvider(params (string id, string fileName, string content, string? etag)[] entries)
    {
        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(entries.Select(e => new FileEntry(
                Id: e.id,
                FileName: e.fileName,
                OpenContentAsync: _ => Task.FromResult<Stream>(
                    new MemoryStream(Encoding.UTF8.GetBytes(e.content))),
                ETag: e.etag)).ToAsyncEnumerable());
        return provider;
    }

    [Fact]
    public async Task IngestFromProviderAsync_NoHashStore_IngestsAllFiles()
    {
        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", null),
            ("id-2", "b.txt", "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov");

        Assert.Equal(2, result.Ingested);
        Assert.Equal(0, result.Skipped);
        await _pipeline.Received(2).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_ETagMatch_SkipsFile()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync("prov", "id-1", etag: "etag-abc", hash: "any");
        var provider = MakeProvider(("id-1", "a.txt", "hello", "etag-abc"));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov", hashStore: hashStore);

        Assert.Equal(0, result.Ingested);
        Assert.Equal(1, result.Skipped);
        await _pipeline.DidNotReceive().IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_HashMatch_SkipsIngestButRefreshesETag()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        // SHA-256 of "hello" in hex
        var helloHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("hello"u8.ToArray()));
        await hashStore.SetAsync("prov", "id-1", etag: "old-etag", hash: helloHash);

        var provider = MakeProvider(("id-1", "a.txt", "hello", "new-etag"));
        var result = await _pipeline.IngestFromProviderAsync(provider, "prov", hashStore: hashStore);

        Assert.Equal(0, result.Ingested);
        Assert.Equal(1, result.Skipped);
        // ETag should be refreshed
        Assert.Equal("new-etag", await hashStore.GetETagAsync("prov", "id-1"));
    }

    [Fact]
    public async Task IngestFromProviderAsync_NewFile_IngestsAndStoresHash()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        await _pipeline.IngestFromProviderAsync(provider, "prov", hashStore: hashStore);

        var hash = await hashStore.GetHashAsync("prov", "id-1");
        Assert.NotNull(hash);
        await _pipeline.Received(1).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupModeFull_DeletesDisappearedDocuments()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync("prov", "old-id", null, "old-hash");

        var provider = MakeProvider(("new-id", "new.txt", "content", null));
        var result = await _pipeline.IngestFromProviderAsync(
            provider, "prov", hashStore: hashStore, cleanupMode: CleanupMode.Full);

        Assert.Equal(1, result.Deleted);
        await _pipeline.Received(1).DeleteAsync("old-id", Arg.Any<CancellationToken>());
        Assert.Null(await hashStore.GetHashAsync("prov", "old-id"));
    }

    [Fact]
    public async Task IngestFromProviderAsync_MetadataForwarded_DocumentIdIsEntryId()
    {
        var capturedMetadata = new List<DocumentMetadata>();
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Do<DocumentMetadata>(m => capturedMetadata.Add(m)),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new IngestionResult { DocumentId = "id-1", ChunksStored = 1 });

        var provider = MakeProvider(("id-1", "report.pdf", "content", null));
        await _pipeline.IngestFromProviderAsync(provider, "prov");

        Assert.Single(capturedMetadata);
        Assert.Equal("id-1", capturedMetadata[0].DocumentId);
        Assert.Equal("report.pdf", capturedMetadata[0].FileName);
    }
}
```

**Step 2: Run tests — should fail to compile**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~IngestFromProviderTests" 2>&1 | head -10
```
Expected: Build error — `RagPipelineExtensions` does not exist.

**Step 3: Create `RagPipelineExtensions`**

`src/Rag.NET/DataProviders/RagPipelineExtensions.cs`:
```csharp
using System.Security.Cryptography;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.DataProviders;

/// <summary>Extension methods for batch ingestion via <see cref="IFileContentProvider"/>.</summary>
public static class RagPipelineExtensions
{
    /// <summary>
    /// Ingests all files from <paramref name="provider"/>, skipping unchanged files when
    /// <paramref name="hashStore"/> is supplied. Optionally deletes disappeared documents
    /// when <paramref name="cleanupMode"/> is <see cref="CleanupMode.Full"/>.
    /// </summary>
    public static async Task<ProviderIngestionResult> IngestFromProviderAsync(
        this IRagPipeline pipeline,
        IFileContentProvider provider,
        string providerId,
        IContentHashStore? hashStore = null,
        DocumentMetadata? baseMetadata = null,
        IngestionOptions? options = null,
        CleanupMode cleanupMode = CleanupMode.None,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ingested = 0;
        var skipped = 0;
        var deleted = 0;
        var errors = new List<string>();

        IReadOnlySet<string> knownIds = hashStore is not null
            ? await hashStore.GetAllIdsAsync(providerId, cancellationToken)
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var entry in provider.GetFilesAsync(cancellationToken))
        {
            seenIds.Add(entry.Id);

            try
            {
                // ETag pre-check: if provider supplies an ETag and it matches, skip without fetching content
                if (hashStore is not null && entry.ETag is not null)
                {
                    var storedETag = await hashStore.GetETagAsync(providerId, entry.Id, cancellationToken);
                    if (entry.ETag == storedETag)
                    {
                        skipped++;
                        continue;
                    }
                }

                // Fetch content
                await using var rawStream = await entry.OpenContentAsync(cancellationToken);

                if (hashStore is null)
                {
                    var metadata = BuildMetadata(entry, baseMetadata);
                    await pipeline.IngestAsync(rawStream, metadata, options, progress, cancellationToken);
                    ingested++;
                }
                else
                {
                    // Buffer to hash, then ingest from buffer
                    using var buffer = new MemoryStream();
                    await rawStream.CopyToAsync(buffer, cancellationToken);
                    var hash = ComputeHash(buffer.GetBuffer(), (int)buffer.Length);

                    var storedHash = await hashStore.GetHashAsync(providerId, entry.Id, cancellationToken);
                    if (hash == storedHash)
                    {
                        // Content unchanged — refresh ETag but skip re-ingestion
                        await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken);
                        skipped++;
                        continue;
                    }

                    buffer.Position = 0;
                    var metadata = BuildMetadata(entry, baseMetadata);
                    await pipeline.IngestAsync(buffer, metadata, options, progress, cancellationToken);
                    ingested++;
                    await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{entry.Id}: {ex.Message}");
            }
        }

        // CleanupMode.Full: delete documents that were known but are no longer present
        if (cleanupMode == CleanupMode.Full && hashStore is not null)
        {
            foreach (var id in knownIds)
            {
                if (seenIds.Contains(id)) continue;

                try
                {
                    await pipeline.DeleteAsync(id, cancellationToken);
                    await hashStore.RemoveAsync(providerId, id, cancellationToken);
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"delete {id}: {ex.Message}");
                }
            }
        }

        return new ProviderIngestionResult(ingested, skipped, deleted, errors);
    }

    private static string ComputeHash(byte[] buffer, int length)
    {
        var hashBytes = SHA256.HashData(buffer.AsSpan(0, length));
        return Convert.ToHexString(hashBytes);
    }

    private static DocumentMetadata BuildMetadata(FileEntry entry, DocumentMetadata? baseMetadata)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (baseMetadata?.Tags is not null)
        {
            foreach (var (k, v) in baseMetadata.Tags)
                tags[k] = v;
        }

        if (entry.Metadata is not null)
        {
            foreach (var (k, v) in entry.Metadata)
                tags[k] = v;
        }

        return new DocumentMetadata
        {
            DocumentId = entry.Id,
            FileName = entry.FileName,
            ContentType = baseMetadata?.ContentType,
            Tags = tags,
        };
    }
}
```

**Step 4: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~IngestFromProviderTests" -v minimal
```
Expected: All 6 tests pass.

**Step 5: Run the full test suite**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v minimal
```
Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/DataProviders/RagPipelineExtensions.cs tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs
git commit -m "feat: add IngestFromProviderAsync with ETag/hash deduplication and CleanupMode.Full"
```

---

### Task 6: `Rag.NET.DataProviders.Web` project scaffold + `SitemapDataProvider`

**Files:**
- Create: `src/Rag.NET.DataProviders.Web/Rag.NET.DataProviders.Web.csproj`
- Create: `src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs`
- Create: `tests/Rag.NET.DataProviders.Web.Tests/Rag.NET.DataProviders.Web.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Web.Tests/SitemapDataProviderTests.cs`
- Modify: solution file (add both new projects)

**Step 1: Create the project files**

`src/Rag.NET.DataProviders.Web/Rag.NET.DataProviders.Web.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Web</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Web</PackageId>
    <Description>Web data providers (crawler, sitemap, RSS) for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="AngleSharp" Version="1.*" />
  </ItemGroup>

</Project>
```

`tests/Rag.NET.DataProviders.Web.Tests/Rag.NET.DataProviders.Web.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.Web\Rag.NET.DataProviders.Web.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

Add both projects to the solution (find the .sln file):
```bash
ls c:/Projects/Prive/Rag.NET/*.sln
```

Then:
```bash
dotnet sln add src/Rag.NET.DataProviders.Web/Rag.NET.DataProviders.Web.csproj
dotnet sln add tests/Rag.NET.DataProviders.Web.Tests/Rag.NET.DataProviders.Web.Tests.csproj
```

**Step 2: Write failing tests for `SitemapDataProvider`**

`tests/Rag.NET.DataProviders.Web.Tests/SitemapDataProviderTests.cs`:
```csharp
using System.Net;
using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

public sealed class SitemapDataProviderTests
{
    private static HttpClient MakeClient(Dictionary<string, string> responses)
    {
        var handler = new FakeHttpMessageHandler(responses);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task GetFilesAsync_ParsesUrlElements()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/page1</loc></url>
              <url><loc>https://example.com/page2</loc></url>
            </urlset>
            """;
        var client = MakeClient(new() { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "https://example.com/page1");
        Assert.Contains(entries, e => e.Id == "https://example.com/page2");
    }

    [Fact]
    public async Task GetFilesAsync_LastmodBecomesETag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://example.com/page1</loc>
                <lastmod>2024-01-15</lastmod>
              </url>
            </urlset>
            """;
        var client = MakeClient(new() { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal("2024-01-15", entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_SitemapIndex_RecursesIntoChildSitemaps()
    {
        var responses = new Dictionary<string, string>
        {
            ["https://example.com/sitemap-index.xml"] = """
                <?xml version="1.0"?>
                <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <sitemap><loc>https://example.com/sitemap1.xml</loc></sitemap>
                  <sitemap><loc>https://example.com/sitemap2.xml</loc></sitemap>
                </sitemapindex>
                """,
            ["https://example.com/sitemap1.xml"] = """
                <?xml version="1.0"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.com/page1</loc></url>
                </urlset>
                """,
            ["https://example.com/sitemap2.xml"] = """
                <?xml version="1.0"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.com/page2</loc></url>
                </urlset>
                """,
        };
        var sut = new SitemapDataProvider("https://example.com/sitemap-index.xml", MakeClient(responses));

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "https://example.com/page1");
        Assert.Contains(entries, e => e.Id == "https://example.com/page2");
    }

    [Fact]
    public async Task GetFilesAsync_InferredFileName_EndsWithHtml()
    {
        const string xml = """
            <?xml version="1.0"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/docs/getting-started</loc></url>
            </urlset>
            """;
        var client = MakeClient(new() { ["https://example.com/sitemap.xml"] = xml });
        var sut = new SitemapDataProvider("https://example.com/sitemap.xml", client);

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal("getting-started.html", entries[0].FileName);
    }
}
```

Add shared `FakeHttpMessageHandler` — create as its own file in the test project:

`tests/Rag.NET.DataProviders.Web.Tests/FakeHttpMessageHandler.cs`:
```csharp
using System.Net;

namespace Rag.NET.DataProviders.Web.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public FakeHttpMessageHandler(Dictionary<string, string> responses)
        => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (_responses.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
```

**Step 3: Run tests — should fail to compile**

```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests/ 2>&1 | head -10
```
Expected: Build error — `SitemapDataProvider` does not exist.

**Step 4: Create `SitemapDataProvider`**

`src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Enumerates URLs from a <c>sitemap.xml</c> or sitemap index file.
/// Follows <c>&lt;sitemapindex&gt;</c> links recursively.
/// ETag is set from the <c>&lt;lastmod&gt;</c> element when present.
/// </summary>
public sealed class SitemapDataProvider : IFileContentProvider
{
    private static readonly XNamespace s_ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly string _sitemapUrl;
    private readonly HttpClient _httpClient;

    public SitemapDataProvider(string sitemapUrl, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sitemapUrl);
        _sitemapUrl = sitemapUrl;
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in LoadSitemapAsync(_sitemapUrl, cancellationToken))
            yield return entry;
    }

    private async IAsyncEnumerable<FileEntry> LoadSitemapAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var xml = await _httpClient.GetStringAsync(url, cancellationToken);
        var root = XDocument.Parse(xml).Root!;

        if (root.Name.LocalName == "sitemapindex")
        {
            foreach (var sitemap in root.Elements(s_ns + "sitemap"))
            {
                var loc = sitemap.Element(s_ns + "loc")?.Value;
                if (loc is null) continue;
                await foreach (var entry in LoadSitemapAsync(loc, cancellationToken))
                    yield return entry;
            }
        }
        else
        {
            foreach (var urlEl in root.Elements(s_ns + "url"))
            {
                var loc = urlEl.Element(s_ns + "loc")?.Value;
                if (loc is null) continue;
                var lastMod = urlEl.Element(s_ns + "lastmod")?.Value;
                var capturedLoc = loc;

                yield return new FileEntry(
                    Id: loc,
                    FileName: InferFileName(loc),
                    OpenContentAsync: async ct => await _httpClient.GetStreamAsync(capturedLoc, ct),
                    ETag: lastMod);
            }
        }
    }

    private static string InferFileName(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "index";
        return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
    }
}
```

**Step 5: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests/ -v minimal
```
Expected: All 4 tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET.DataProviders.Web/ tests/Rag.NET.DataProviders.Web.Tests/
git commit -m "feat: add Rag.NET.DataProviders.Web project and SitemapDataProvider"
```

---

### Task 7: `RssDataProvider`

**Files:**
- Create: `src/Rag.NET.DataProviders.Web/RssDataProvider.cs`
- Create: `tests/Rag.NET.DataProviders.Web.Tests/RssDataProviderTests.cs`

**Step 1: Write failing tests**

`tests/Rag.NET.DataProviders.Web.Tests/RssDataProviderTests.cs`:
```csharp
using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

public sealed class RssDataProviderTests
{
    private static HttpClient MakeClient(string feedUrl, string feedXml)
        => new HttpClient(new FakeHttpMessageHandler(new() { [feedUrl] = feedXml }));

    [Fact]
    public async Task GetFilesAsync_Rss2_ParsesItems()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                </item>
                <item>
                  <guid>https://example.com/post-2</guid>
                  <link>https://example.com/post-2</link>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss", MakeClient("https://example.com/feed.rss", xml));

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "https://example.com/post-1");
        Assert.Contains(entries, e => e.Id == "https://example.com/post-2");
    }

    [Fact]
    public async Task GetFilesAsync_Rss2_PubDateBecomesETag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item>
                  <guid>https://example.com/post-1</guid>
                  <link>https://example.com/post-1</link>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;
        var sut = new RssDataProvider("https://example.com/feed.rss", MakeClient("https://example.com/feed.rss", xml));
        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal("Mon, 01 Jan 2024 00:00:00 GMT", entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_Atom_ParsesEntries()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <updated>2024-01-01T00:00:00Z</updated>
              </entry>
              <entry>
                <id>https://example.com/post-2</id>
                <link href="https://example.com/post-2"/>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml", MakeClient("https://example.com/atom.xml", xml));

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "https://example.com/post-1");
    }

    [Fact]
    public async Task GetFilesAsync_Atom_UpdatedBecomesETag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.com/post-1</id>
                <link href="https://example.com/post-1"/>
                <updated>2024-01-01T00:00:00Z</updated>
              </entry>
            </feed>
            """;
        var sut = new RssDataProvider("https://example.com/atom.xml", MakeClient("https://example.com/atom.xml", xml));
        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal("2024-01-01T00:00:00Z", entries[0].ETag);
    }
}
```

**Step 2: Run tests — should fail**

```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests/ --filter "FullyQualifiedName~RssDataProviderTests" 2>&1 | head -10
```
Expected: Build error — `RssDataProvider` does not exist.

**Step 3: Create `RssDataProvider`**

`src/Rag.NET.DataProviders.Web/RssDataProvider.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Enumerates items from an RSS 2.0 or Atom feed.
/// ETag is the <c>&lt;pubDate&gt;</c> (RSS) or <c>&lt;updated&gt;</c> (Atom) value when present.
/// </summary>
public sealed class RssDataProvider : IFileContentProvider
{
    private static readonly XNamespace s_atomNs = "http://www.w3.org/2005/Atom";

    private readonly string _feedUrl;
    private readonly HttpClient _httpClient;

    public RssDataProvider(string feedUrl, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);
        _feedUrl = feedUrl;
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var xml = await _httpClient.GetStringAsync(_feedUrl, cancellationToken);
        var root = XDocument.Parse(xml).Root!;

        if (root.Name.LocalName == "feed")
        {
            // Atom
            foreach (var entry in root.Elements(s_atomNs + "entry"))
            {
                var id = entry.Element(s_atomNs + "id")?.Value
                      ?? entry.Element(s_atomNs + "link")?.Attribute("href")?.Value;
                if (id is null) continue;

                var link = entry.Element(s_atomNs + "link")?.Attribute("href")?.Value ?? id;
                var updated = entry.Element(s_atomNs + "updated")?.Value;
                var capturedLink = link;

                yield return new FileEntry(
                    Id: id,
                    FileName: InferFileName(id),
                    OpenContentAsync: async ct => await _httpClient.GetStreamAsync(capturedLink, ct),
                    ETag: updated);
            }
        }
        else
        {
            // RSS 2.0
            foreach (var item in root.Descendants("item"))
            {
                var guid = item.Element("guid")?.Value;
                var link = item.Element("link")?.Value;
                var id = guid ?? link;
                if (id is null) continue;

                var pubDate = item.Element("pubDate")?.Value;
                var capturedLink = link ?? id;

                yield return new FileEntry(
                    Id: id,
                    FileName: InferFileName(id),
                    OpenContentAsync: async ct => await _httpClient.GetStreamAsync(capturedLink, ct),
                    ETag: pubDate);
            }
        }
    }

    private static string InferFileName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "item";
            return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
        }
        catch (UriFormatException)
        {
            return "item.html";
        }
    }
}
```

**Step 4: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests/ -v minimal
```
Expected: All 8 tests pass (4 sitemap + 4 RSS).

**Step 5: Commit**

```bash
git add src/Rag.NET.DataProviders.Web/RssDataProvider.cs tests/Rag.NET.DataProviders.Web.Tests/RssDataProviderTests.cs
git commit -m "feat: add RssDataProvider (RSS 2.0 and Atom)"
```

---

### Task 8: `WebCrawlerDataProvider`

**Files:**
- Create: `src/Rag.NET.DataProviders.Web/WebCrawlerOptions.cs`
- Create: `src/Rag.NET.DataProviders.Web/WebCrawlerDataProvider.cs`
- Create: `tests/Rag.NET.DataProviders.Web.Tests/WebCrawlerDataProviderTests.cs`

**Step 1: Write failing tests**

`tests/Rag.NET.DataProviders.Web.Tests/WebCrawlerDataProviderTests.cs`:
```csharp
using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

public sealed class WebCrawlerDataProviderTests
{
    private const string SeedUrl = "https://example.com/";

    // Minimal 3-page site: index links to page1 and page2; page1 links back to index; page2 is a leaf
    private static readonly Dictionary<string, string> s_site = new()
    {
        [SeedUrl] = """
            <html><body>
              <a href="/page1">Page 1</a>
              <a href="/page2">Page 2</a>
            </body></html>
            """,
        ["https://example.com/page1"] = """
            <html><body>
              <a href="/">Back home</a>
              <p>Page one content</p>
            </body></html>
            """,
        ["https://example.com/page2"] = "<html><body><p>Page two content</p></body></html>",
    };

    private static HttpClient MakeClient(Dictionary<string, string>? responses = null)
        => new HttpClient(new FakeHttpMessageHandler(responses ?? s_site));

    [Fact]
    public async Task GetFilesAsync_BfsDiscoversPagesFromSeed()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Contains(entries, e => e.Id == SeedUrl);
        Assert.Contains(entries, e => e.Id == "https://example.com/page1");
        Assert.Contains(entries, e => e.Id == "https://example.com/page2");
    }

    [Fact]
    public async Task GetFilesAsync_MaxPages_LimitsResults()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { MaxPages = 2, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task GetFilesAsync_MaxDepth_StopsFollowingLinksAtDepth()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { MaxDepth = 0, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync().ToListAsync();

        // Depth 0 → only the seed page; links are not followed
        Assert.Single(entries);
        Assert.Equal(SeedUrl, entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_SameDomain_ExcludesExternalLinks()
    {
        var responses = new Dictionary<string, string>
        {
            [SeedUrl] = """
                <html><body>
                  <a href="/internal">Internal</a>
                  <a href="https://other.com/page">External</a>
                </body></html>
                """,
            ["https://example.com/internal"] = "<html><body>Internal page</body></html>",
        };
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(responses),
            new WebCrawlerOptions { SameDomain = true, RespectRobotsTxt = false });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.DoesNotContain(entries, e => e.Id.StartsWith("https://other.com"));
    }

    [Fact]
    public async Task GetFilesAsync_RobotsTxt_DisallowedPathSkipped()
    {
        var responses = new Dictionary<string, string>(s_site)
        {
            ["https://example.com/robots.txt"] = """
                User-agent: *
                Disallow: /page2
                """,
        };
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(responses),
            new WebCrawlerOptions { RespectRobotsTxt = true });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.DoesNotContain(entries, e => e.Id == "https://example.com/page2");
    }

    [Fact]
    public async Task GetFilesAsync_OpenContentAsync_ReturnsPageHtml()
    {
        var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(),
            new WebCrawlerOptions { MaxDepth = 0, RespectRobotsTxt = false });
        var entries = await sut.GetFilesAsync().ToListAsync();

        await using var stream = await entries[0].OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        Assert.Contains("Page 1", content);
    }
}
```

**Step 2: Run tests — should fail**

```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests/ --filter "FullyQualifiedName~WebCrawlerDataProviderTests" 2>&1 | head -10
```
Expected: Build error.

**Step 3: Create `WebCrawlerOptions`**

`src/Rag.NET.DataProviders.Web/WebCrawlerOptions.cs`:
```csharp
namespace Rag.NET.DataProviders.Web;

/// <summary>Configuration for <see cref="WebCrawlerDataProvider"/>.</summary>
public sealed class WebCrawlerOptions
{
    /// <summary>Maximum link-following depth from the seed URL. Default: 3.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Maximum number of pages to crawl. Default: 200.</summary>
    public int MaxPages { get; init; } = 200;

    /// <summary>
    /// Only follow links whose host matches the seed URL's host. Default: <see langword="true"/>.
    /// </summary>
    public bool SameDomain { get; init; } = true;

    /// <summary>
    /// Fetch <c>/robots.txt</c> and skip disallowed paths. Default: <see langword="true"/>.
    /// </summary>
    public bool RespectRobotsTxt { get; init; } = true;
}
```

**Step 4: Create `WebCrawlerDataProvider`**

`src/Rag.NET.DataProviders.Web/WebCrawlerDataProvider.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Html.Parser;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Crawls a website via BFS link-following from a seed URL, yielding each discovered page.
/// Content is captured at crawl time; <see cref="FileEntry.OpenContentAsync"/> returns the already-fetched HTML.
/// No ETag — no cheap pre-check is available for web pages without server cooperation.
/// </summary>
public sealed class WebCrawlerDataProvider : IFileContentProvider
{
    private readonly string _seedUrl;
    private readonly HttpClient _httpClient;
    private readonly WebCrawlerOptions _options;

    public WebCrawlerDataProvider(string seedUrl, HttpClient httpClient, WebCrawlerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedUrl);
        _seedUrl = seedUrl;
        _httpClient = httpClient;
        _options = options ?? new WebCrawlerOptions();
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seedUri = new Uri(_seedUrl);
        var disallowed = _options.RespectRobotsTxt
            ? await LoadRobotsAsync(seedUri, cancellationToken)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string url, int depth)>();
        queue.Enqueue((_seedUrl, 0));
        var pageCount = 0;

        while (queue.Count > 0 && pageCount < _options.MaxPages)
        {
            var (url, depth) = queue.Dequeue();
            if (!visited.Add(url)) continue;
            if (IsDisallowed(url, disallowed)) continue;
            if (_options.SameDomain && new Uri(url).Host != seedUri.Host) continue;

            string html;
            try
            {
                html = await _httpClient.GetStringAsync(url, cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            pageCount++;
            var capturedHtml = html;

            yield return new FileEntry(
                Id: url,
                FileName: InferFileName(url),
                OpenContentAsync: _ =>
                {
                    var bytes = Encoding.UTF8.GetBytes(capturedHtml);
                    return Task.FromResult<Stream>(new MemoryStream(bytes));
                });

            if (depth < _options.MaxDepth)
            {
                foreach (var link in ExtractLinks(html, url))
                {
                    if (!visited.Contains(link))
                        queue.Enqueue((link, depth + 1));
                }
            }
        }
    }

    private async Task<HashSet<string>> LoadRobotsAsync(Uri seedUri, CancellationToken ct)
    {
        try
        {
            var robotsUrl = new Uri(seedUri, "/robots.txt").ToString();
            var content = await _httpClient.GetStringAsync(robotsUrl, ct);
            return ParseRobotsDisallowed(content);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static HashSet<string> ParseRobotsDisallowed(string content)
    {
        var disallowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applyToUs = false;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
            {
                var agent = trimmed["User-agent:".Length..].Trim();
                applyToUs = agent == "*";
            }
            else if (applyToUs && trimmed.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
            {
                var path = trimmed["Disallow:".Length..].Trim();
                if (!string.IsNullOrEmpty(path))
                    disallowed.Add(path);
            }
        }

        return disallowed;
    }

    private static bool IsDisallowed(string url, HashSet<string> disallowed)
    {
        var path = new Uri(url).AbsolutePath;
        foreach (var rule in disallowed)
        {
            if (path.StartsWith(rule, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractLinks(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var baseUri = new Uri(baseUrl);

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            Uri uri;
            try
            {
                uri = new Uri(baseUri, href);
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (uri.Scheme != "http" && uri.Scheme != "https") continue;

            // Normalise: strip fragment, trailing slash
            var normalised = new UriBuilder(uri) { Fragment = string.Empty }.Uri
                .ToString().TrimEnd('/');
            yield return normalised;
        }
    }

    private static string InferFileName(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "index";
        return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
    }
}
```

**Step 5: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests/ -v minimal
```
Expected: All 14 tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET.DataProviders.Web/WebCrawlerOptions.cs src/Rag.NET.DataProviders.Web/WebCrawlerDataProvider.cs tests/Rag.NET.DataProviders.Web.Tests/WebCrawlerDataProviderTests.cs
git commit -m "feat: add WebCrawlerDataProvider with BFS, SameDomain, MaxDepth, robots.txt"
```

---

### Task 9: `Rag.NET.DataProviders.GitHub` project + `GitHubDataProvider`

**Files:**
- Create: `src/Rag.NET.DataProviders.GitHub/Rag.NET.DataProviders.GitHub.csproj`
- Create: `src/Rag.NET.DataProviders.GitHub/GitHubDataProviderOptions.cs`
- Create: `src/Rag.NET.DataProviders.GitHub/GitHubDataProvider.cs`
- Create: `tests/Rag.NET.DataProviders.GitHub.Tests/Rag.NET.DataProviders.GitHub.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.GitHub.Tests/GitHubDataProviderTests.cs`

**Step 1: Create project files**

`src/Rag.NET.DataProviders.GitHub/Rag.NET.DataProviders.GitHub.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.GitHub</RootNamespace>
    <PackageId>Rag.NET.DataProviders.GitHub</PackageId>
    <Description>GitHub data provider for Rag.NET using Octokit</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Octokit" Version="13.*" />
  </ItemGroup>

</Project>
```

`tests/Rag.NET.DataProviders.GitHub.Tests/Rag.NET.DataProviders.GitHub.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.GitHub\Rag.NET.DataProviders.GitHub.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>

</Project>
```

Add to solution:
```bash
dotnet sln add src/Rag.NET.DataProviders.GitHub/Rag.NET.DataProviders.GitHub.csproj
dotnet sln add tests/Rag.NET.DataProviders.GitHub.Tests/Rag.NET.DataProviders.GitHub.Tests.csproj
```

**Step 2: Write failing tests**

`tests/Rag.NET.DataProviders.GitHub.Tests/GitHubDataProviderTests.cs`:
```csharp
using NSubstitute;
using Octokit;
using Rag.NET.DataProviders.GitHub;
using Xunit;

namespace Rag.NET.DataProviders.GitHub.Tests;

public sealed class GitHubDataProviderTests
{
    private const string Owner = "org";
    private const string Repo = "repo";

    // Helper: build a mock IGitHubClient whose Repository.Content.GetRawContent returns empty bytes
    private static IGitHubClient MakeClient(
        TreeResponse tree,
        CompareResult? compareResult = null)
    {
        var client = Substitute.For<IGitHubClient>();
        var gitClient = Substitute.For<IGitHubClient>().Git;

        client.Git.Tree.GetRecursive(Owner, Repo, Arg.Any<string>())
            .Returns(tree);

        client.Repository.Content
            .GetRawContent(Owner, Repo, Arg.Any<string>())
            .Returns(Array.Empty<byte>());

        if (compareResult is not null)
        {
            client.Repository.Commit
                .Compare(Owner, Repo, Arg.Any<string>(), Arg.Any<string>())
                .Returns(compareResult);
        }

        return client;
    }

    private static TreeResponse MakeTree(params (string path, string sha)[] items)
    {
        var treeItems = items
            .Select(i => new TreeItem(i.path, "100644", TreeType.Blob, 100, i.sha, $"https://api.github.com/repos/{Owner}/{Repo}/git/blobs/{i.sha}"))
            .ToList();
        return new TreeResponse("abc123", "https://api.github.com", treeItems, truncated: false);
    }

    [Fact]
    public async Task GetFilesAsync_FullTree_ReturnsAllBlobs()
    {
        var tree = MakeTree(("docs/readme.md", "sha-1"), ("src/main.cs", "sha-2"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree));

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "docs/readme.md");
        Assert.Contains(entries, e => e.Id == "src/main.cs");
    }

    [Fact]
    public async Task GetFilesAsync_FullTree_BlobShaBecomesETag()
    {
        var tree = MakeTree(("docs/readme.md", "sha-abc"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree));

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal("sha-abc", entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var tree = MakeTree(("readme.md", "sha-1"), ("build.yaml", "sha-2"), ("src/main.cs", "sha-3"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree),
            new GitHubDataProviderOptions { Extensions = [".md", ".cs"] });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => e.Id == "build.yaml");
    }

    [Fact]
    public async Task GetFilesAsync_PredicateFilter_ExcludesMatchedPaths()
    {
        var tree = MakeTree(("docs/guide.md", "sha-1"), ("docs/plans/internal.md", "sha-2"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree),
            new GitHubDataProviderOptions { Filter = path => !path.StartsWith("docs/plans/") });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Single(entries);
        Assert.Equal("docs/guide.md", entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_OnlyReturnsChangedFiles()
    {
        var compareResult = new CompareResult(
            url: "", htmlUrl: "", permalink_url: "", diffUrl: "", patchUrl: "",
            baseCommit: null!, mergeBaseCommit: null!,
            status: "ahead", aheadBy: 2, behindBy: 0, totalCommits: 2,
            commits: [],
            files:
            [
                new GitHubCommitFile { Filename = "changed.md", Sha = "new-sha", Status = "modified" },
            ]);

        var tree = MakeTree(); // full tree not called in delta mode
        var client = MakeClient(tree, compareResult);
        var sut = new GitHubDataProvider(Owner, Repo, client,
            new GitHubDataProviderOptions { LastIngestedCommitSha = "old-sha" });

        var entries = await sut.GetFilesAsync().ToListAsync();

        Assert.Single(entries);
        Assert.Equal("changed.md", entries[0].Id);
        // Verify full tree was NOT called
        await client.Git.Tree.DidNotReceive().GetRecursive(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
```

**Step 3: Create `GitHubDataProviderOptions`**

`src/Rag.NET.DataProviders.GitHub/GitHubDataProviderOptions.cs`:
```csharp
namespace Rag.NET.DataProviders.GitHub;

/// <summary>Configuration for <see cref="GitHubDataProvider"/>.</summary>
public sealed class GitHubDataProviderOptions
{
    /// <summary>Branch or ref to traverse. Default: <c>"main"</c>.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>
    /// File extensions to include (e.g. <c>[".md", ".cs"]</c>).
    /// Defaults to <c>["*"]</c> which matches all extensions.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = ["*"];

    /// <summary>Optional predicate to exclude files by repository path.</summary>
    public Func<string, bool>? Filter { get; init; }

    /// <summary>
    /// When set, performs a delta run: only files changed since this commit SHA are returned.
    /// When <see langword="null"/>, performs a full tree traversal.
    /// Update this value after a successful run to enable incremental ingestion.
    /// </summary>
    public string? LastIngestedCommitSha { get; init; }
}
```

**Step 4: Create `GitHubDataProvider`**

`src/Rag.NET.DataProviders.GitHub/GitHubDataProvider.cs`:
```csharp
using System.Runtime.CompilerServices;
using Octokit;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitHub;

/// <summary>
/// Enumerates files from a GitHub repository.
/// On first run (no <see cref="GitHubDataProviderOptions.LastIngestedCommitSha"/>): full recursive tree.
/// On subsequent runs: only files changed since <c>LastIngestedCommitSha</c> via compare API.
/// ETag is the blob SHA — Git's own content hash, so ETag matches guarantee byte-identical content.
/// </summary>
public sealed class GitHubDataProvider : IFileContentProvider
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly IGitHubClient _client;
    private readonly GitHubDataProviderOptions _options;

    public GitHubDataProvider(
        string owner,
        string repo,
        IGitHubClient client,
        GitHubDataProviderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        _owner = owner;
        _repo = repo;
        _client = client;
        _options = options ?? new GitHubDataProviderOptions();
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_options.LastIngestedCommitSha is not null)
        {
            await foreach (var entry in GetDeltaFilesAsync(cancellationToken))
                yield return entry;
        }
        else
        {
            await foreach (var entry in GetFullTreeFilesAsync(cancellationToken))
                yield return entry;
        }
    }

    private async IAsyncEnumerable<FileEntry> GetFullTreeFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tree = await _client.Git.Tree.GetRecursive(_owner, _repo, _options.Branch);

        foreach (var item in tree.Tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type != TreeType.Blob) continue;
            if (!MatchesExtension(item.Path)) continue;
            if (_options.Filter is not null && !_options.Filter(item.Path)) continue;

            var capturedPath = item.Path;
            yield return new FileEntry(
                Id: item.Path,
                FileName: Path.GetFileName(item.Path),
                OpenContentAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content.GetRawContent(_owner, _repo, capturedPath);
                    return new MemoryStream(bytes);
                },
                ETag: item.Sha);
        }
    }

    private async IAsyncEnumerable<FileEntry> GetDeltaFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comparison = await _client.Repository.Commit
            .Compare(_owner, _repo, _options.LastIngestedCommitSha!, _options.Branch);

        foreach (var file in comparison.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Status == "removed") continue;
            if (!MatchesExtension(file.Filename)) continue;
            if (_options.Filter is not null && !_options.Filter(file.Filename)) continue;

            var capturedPath = file.Filename;
            yield return new FileEntry(
                Id: file.Filename,
                FileName: Path.GetFileName(file.Filename),
                OpenContentAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content.GetRawContent(_owner, _repo, capturedPath);
                    return new MemoryStream(bytes);
                },
                ETag: file.Sha);
        }
    }

    private bool MatchesExtension(string path)
    {
        if (_options.Extensions is ["*"]) return true;
        var ext = Path.GetExtension(path);
        return _options.Extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, "*", StringComparison.Ordinal));
    }
}
```

**Step 5: Run tests — should pass**

```bash
dotnet test tests/Rag.NET.DataProviders.GitHub.Tests/ -v minimal
```
Expected: All 5 tests pass.

**Step 6: Run all tests**

```bash
dotnet test -v minimal
```
Expected: All tests pass across all projects.

**Step 7: Commit**

```bash
git add src/Rag.NET.DataProviders.GitHub/ tests/Rag.NET.DataProviders.GitHub.Tests/
git commit -m "feat: add Rag.NET.DataProviders.GitHub with full-tree and delta ingestion"
```

---

### Task 10: Update docs and features.md

**Files:**
- Modify: `docs/guide/ingestion.md` — add a "Data Providers" section
- Modify: `docs/reference/features.md` — mark 3 items as done

**Step 1: Add a Data Providers section to `docs/guide/ingestion.md`**

Open `docs/guide/ingestion.md`. Append the following section before the final heading (or at the end of the file):

```markdown
## Data providers

For batch ingestion from a directory, website, or GitHub repository, use `IngestFromProviderAsync` instead of calling `IngestAsync` in a loop. It handles ETag/hash deduplication and optional cleanup automatically.

### `LocalFilesDataProvider`

```csharp
var provider = new LocalFilesDataProvider("/data/docs", new LocalFilesOptions
{
    Extensions   = [".pdf", ".docx", ".md"],
    SearchOption = SearchOption.AllDirectories,
    Filter       = path => !path.Contains(".git"),
});

var result = await pipeline.IngestFromProviderAsync(provider, "my-corpus",
    hashStore: sp.GetRequiredService<IContentHashStore>(),
    cleanupMode: CleanupMode.Full);

Console.WriteLine($"Ingested: {result.Ingested}, Skipped: {result.Skipped}, Deleted: {result.Deleted}");
```

### `SitemapDataProvider`

```csharp
var provider = new SitemapDataProvider("https://docs.example.com/sitemap.xml", httpClient);
await pipeline.IngestFromProviderAsync(provider, "docs-site", hashStore: hashStore);
```

### `WebCrawlerDataProvider`

```csharp
var provider = new WebCrawlerDataProvider("https://docs.example.com", httpClient, new WebCrawlerOptions
{
    MaxDepth = 3,
    MaxPages = 500,
    SameDomain = true,
    RespectRobotsTxt = true,
});
await pipeline.IngestFromProviderAsync(provider, "docs-site", hashStore: hashStore);
```

### `GitHubDataProvider`

```csharp
var provider = new GitHubDataProvider("my-org", "my-repo", githubClient, new GitHubDataProviderOptions
{
    Branch                = "main",
    Extensions            = [".md", ".cs"],
    Filter                = path => !path.StartsWith("docs/plans/"),
    LastIngestedCommitSha = settings.LastIngestedCommitSha, // null on first run
});
await pipeline.IngestFromProviderAsync(provider, "github-repo", hashStore: hashStore);
// Save result to settings for next run: settings.LastIngestedCommitSha = latestCommitSha;
```

### Registration

```csharp
services.AddRagNet(b => b
    .UsePgVector(connectionString, vectorDimensions: 1536)
    .UseContentHashRecordManager("ragnet-hashes.db"));
```
```

**Step 2: Update `docs/reference/features.md`**

In the priority table, change the three `[ ]` rows to `[x]`:
- `Data Provider Abstraction` → `[x]`
- `Web Crawler / Sitemap / RSS` → `[x]`
- `Content-Hash Record Manager` → `[x]`

**Step 3: Build and test one final time**

```bash
dotnet test -v minimal
```
Expected: All tests pass.

**Step 4: Commit**

```bash
git add docs/guide/ingestion.md docs/reference/features.md
git commit -m "docs: add data providers usage guide; mark data-provider features as done"
```
