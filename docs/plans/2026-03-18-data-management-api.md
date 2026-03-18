# Data Management API Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `IRagDataManager` — a SQLite-backed sidecar that tracks ingested documents and chunks, providing a read surface (`GetDocumentsAsync`, `GetChunksAsync`, `GetStatsAsync`) without touching `IVectorStore`.

**Architecture:** A new `SqliteDocumentStore` follows the exact same pattern as `SqliteBm25Index` — lazy init, stale guard via collection name, `SqliteStoreHelper` for connections. `DocumentIngestor` gets one new optional parameter `IRagDataManager? dataManager = null`; it calls `dataManager?.Add` after storing to the vector store and `dataManager?.Remove` in `DeleteAsync`. Zero changes to `IVectorStore` or any vector store implementation.

**Tech Stack:** C# / .NET 10 / xUnit v3 / NSubstitute / `Microsoft.Data.Sqlite`. `TreatWarningsAsErrors=true`. Always `TestContext.Current.CancellationToken` on async tests.

---

### Task 1 — Interface and models

**Files:**
- Create: `src/Rag.NET/Abstractions/IRagDataManager.cs`
- Create: `src/Rag.NET/Models/DocumentSummary.cs`
- Create: `src/Rag.NET/Models/DataManagerStats.cs`

No tests for this task — these are pure type definitions. The test suite will catch anything wrong at compile time in Task 2.

**Step 1: Create the interface**

```csharp
// src/Rag.NET/Abstractions/IRagDataManager.cs
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Sidecar metadata store tracking documents and chunks ingested by <see cref="Rag.NET.Ingestion.DocumentIngestor"/>.
/// Write methods are called internally; read methods are the public management surface.
/// </summary>
public interface IRagDataManager : IDisposable, IAsyncDisposable
{
    // Write — called internally by DocumentIngestor
    void Add(DocumentMetadata metadata, IReadOnlyList<TextChunk> chunks);
    void Remove(string documentId);

    // Read — public API
    Task<IReadOnlyList<DocumentSummary>> GetDocumentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TextChunk>> GetChunksAsync(string documentId, CancellationToken cancellationToken = default);
    Task<DataManagerStats> GetStatsAsync(CancellationToken cancellationToken = default);

    // Lifecycle
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
```

**Step 2: Create `DocumentSummary`**

```csharp
// src/Rag.NET/Models/DocumentSummary.cs
namespace Rag.NET.Models;

public sealed record DocumentSummary
{
    public required string DocumentId  { get; init; }
    public required string FileName    { get; init; }
    public string?         ContentType { get; init; }
    public required int    ChunkCount  { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public IDictionary<string, string> Tags { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
```

**Step 3: Create `DataManagerStats`**

```csharp
// src/Rag.NET/Models/DataManagerStats.cs
namespace Rag.NET.Models;

public sealed record DataManagerStats
{
    public required int DocumentCount   { get; init; }
    public required int TotalChunkCount { get; init; }
}
```

**Step 4: Build to verify it compiles**

```bash
cd c:/Projects/Prive/Rag.NET
dotnet build src/Rag.NET -q 2>&1 | tail -5
```

Expected: `Build succeeded.` with 0 warnings, 0 errors.

**Step 5: Commit**

```bash
git add src/Rag.NET/Abstractions/IRagDataManager.cs \
        src/Rag.NET/Models/DocumentSummary.cs \
        src/Rag.NET/Models/DataManagerStats.cs
git commit -m "feat: add IRagDataManager interface and DocumentSummary/DataManagerStats models"
```

---

### Task 2 — `SqliteDocumentStore` implementation

**Files:**
- Create: `tests/Rag.NET.Tests/Storage/SqliteDocumentStoreTests.cs`
- Create: `src/Rag.NET/Storage/SqliteDocumentStore.cs`

**Context:**
- `SqliteBm25Index` is in `src/Rag.NET/Storage/SqliteBm25Index.cs` — the pattern to follow exactly.
- `SqliteStoreHelper` provides `OpenConnection`, `ReadMetadata`, `WriteMetadata`, `EnsureMetadataTable`.
- The stale guard key is `"doc_store_collection_name"` (different from BM25's `"bm25_collection_name"`).
- Two tables: `rag_documents` (doc-level metadata) and `rag_chunks` (chunk text + metadata).
- `Add` uses a transaction to atomically write both tables.
- Async read methods use `Task.Run` to offload SQLite I/O (same pattern as `InitializeAsync`).

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Storage/SqliteDocumentStoreTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public sealed class SqliteDocumentStoreTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-dm-{Guid.NewGuid():N}.db");
    private SqliteDocumentStore? _sut;

    private SqliteDocumentStore CreateSut(string collection = "test-coll")
    {
        _sut = new SqliteDocumentStore(_dbPath, collection);
        return _sut;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static DocumentMetadata MakeMetadata(string docId, string fileName = "test.txt", string? contentType = "text/plain")
        => new() { DocumentId = docId, FileName = fileName, ContentType = contentType };

    private static TextChunk MakeChunk(string docId, int idx, string text, int start = 0, int end = 0)
        => new() { DocumentId = docId, ChunkIndex = idx, Text = text, StartPosition = start, EndPosition = end };

    [Fact]
    public async Task Add_ThenGetDocuments_ReturnsSummaryWithCorrectFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        var metadata = MakeMetadata("doc-1", "report.pdf", "application/pdf");

        sut.Add(metadata, [MakeChunk("doc-1", 0, "hello"), MakeChunk("doc-1", 1, "world")]);

        var docs = await sut.GetDocumentsAsync(ct);
        Assert.Single(docs);
        Assert.Equal("doc-1",           docs[0].DocumentId);
        Assert.Equal("report.pdf",      docs[0].FileName);
        Assert.Equal("application/pdf", docs[0].ContentType);
        Assert.Equal(2,                 docs[0].ChunkCount);
        Assert.True(docs[0].IngestedAt <= DateTimeOffset.UtcNow);
        Assert.True(docs[0].IngestedAt > DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public async Task Add_ThenGetChunks_ReturnsOriginalTextChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        var chunk0 = MakeChunk("doc-1", 0, "hello world", start: 0, end: 11);
        var chunk1 = MakeChunk("doc-1", 1, "foo bar", start: 12, end: 19);

        sut.Add(MakeMetadata("doc-1"), [chunk0, chunk1]);

        var chunks = await sut.GetChunksAsync("doc-1", ct);
        Assert.Equal(2,             chunks.Count);
        Assert.Equal("hello world", chunks[0].Text);
        Assert.Equal(0,             chunks[0].StartPosition);
        Assert.Equal(11,            chunks[0].EndPosition);
        Assert.Equal("foo bar",     chunks[1].Text);
        Assert.Equal(1,             chunks[1].ChunkIndex);
        Assert.Equal(12,            chunks[1].StartPosition);
    }

    [Fact]
    public async Task Add_ThenGetStats_ReturnsCorrectCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "a"), MakeChunk("doc-1", 1, "b")]);
        sut.Add(MakeMetadata("doc-2"), [MakeChunk("doc-2", 0, "c")]);

        var stats = await sut.GetStatsAsync(ct);
        Assert.Equal(2, stats.DocumentCount);
        Assert.Equal(3, stats.TotalChunkCount);
    }

    [Fact]
    public async Task Remove_ThenGetDocuments_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        sut.Remove("doc-1");

        var docs = await sut.GetDocumentsAsync(ct);
        Assert.Empty(docs);
    }

    [Fact]
    public async Task Remove_ThenGetChunks_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        sut.Remove("doc-1");

        var chunks = await sut.GetChunksAsync("doc-1", ct);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllDocumentsAndChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        sut.Add(MakeMetadata("doc-2"), [MakeChunk("doc-2", 0, "world")]);

        await sut.ClearAsync(ct);

        var docs  = await sut.GetDocumentsAsync(ct);
        var stats = await sut.GetStatsAsync(ct);
        Assert.Empty(docs);
        Assert.Equal(0, stats.TotalChunkCount);
    }

    [Fact]
    public async Task Add_ThenRestart_GetDocumentsFindsDocument()
    {
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1", "report.pdf"), [MakeChunk("doc-1", 0, "hello")]);
        await sut.DisposeAsync();

        _sut = new SqliteDocumentStore(_dbPath, "test-coll");
        var docs = await _sut.GetDocumentsAsync(TestContext.Current.CancellationToken);
        Assert.Single(docs);
        Assert.Equal("doc-1", docs[0].DocumentId);
    }

    [Fact]
    public async Task CollectionNameMismatch_WipesExistingData()
    {
        var sut = CreateSut("collection-A");
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        await sut.DisposeAsync();

        _sut = new SqliteDocumentStore(_dbPath, "collection-B");
        var docs = await _sut.GetDocumentsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(docs);
    }

    [Fact]
    public async Task InitializeAsync_CanBeAwaited_ThenAddWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        await sut.InitializeAsync(ct);

        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        var docs = await sut.GetDocumentsAsync(ct);
        Assert.Single(docs);
    }

    [Fact]
    public void Add_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]));
    }

    [Fact]
    public async Task GetDocuments_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.GetDocumentsAsync(TestContext.Current.CancellationToken));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
cd c:/Projects/Prive/Rag.NET
dotnet test tests/Rag.NET.Tests -q --filter "FullyQualifiedName~SqliteDocumentStoreTests" 2>&1 | tail -10
```

Expected: compile error — `SqliteDocumentStore` does not exist yet.

**Step 3: Implement `SqliteDocumentStore`**

Create `src/Rag.NET/Storage/SqliteDocumentStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Storage;

/// <summary>
/// SQLite-backed sidecar tracking document metadata and chunks ingested via <c>DocumentIngestor</c>.
/// Lazy-initialises on first use: creates tables, applies stale guard, matching <see cref="SqliteBm25Index"/> patterns.
/// </summary>
public sealed class SqliteDocumentStore : IRagDataManager
{
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialised;
    private bool _disposed;

    public SqliteDocumentStore(string dbPath, string? collectionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
    }

    public void Add(DocumentMetadata metadata, IReadOnlyList<TextChunk> chunks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var tx = conn.BeginTransaction();

        using var docCmd = conn.CreateCommand();
        docCmd.CommandText = """
            INSERT OR REPLACE INTO rag_documents
                (doc_id, file_name, content_type, tags_json, ingested_at, chunk_count)
            VALUES
                ($docId, $fileName, $contentType, $tagsJson, $ingestedAt, $chunkCount)
            """;
        docCmd.Parameters.AddWithValue("$docId",       metadata.DocumentId);
        docCmd.Parameters.AddWithValue("$fileName",    metadata.FileName);
        docCmd.Parameters.AddWithValue("$contentType", (object?)metadata.ContentType ?? DBNull.Value);
        docCmd.Parameters.AddWithValue("$tagsJson",    JsonSerializer.Serialize(metadata.Tags));
        docCmd.Parameters.AddWithValue("$ingestedAt",  now);
        docCmd.Parameters.AddWithValue("$chunkCount",  chunks.Count);
        docCmd.ExecuteNonQuery();

        using var chunkCmd = conn.CreateCommand();
        chunkCmd.CommandText = """
            INSERT OR REPLACE INTO rag_chunks
                (doc_id, chunk_index, start_pos, end_pos, text, metadata_json)
            VALUES
                ($docId, $chunkIdx, $startPos, $endPos, $text, $meta)
            """;
        foreach (var chunk in chunks)
        {
            chunkCmd.Parameters.Clear();
            chunkCmd.Parameters.AddWithValue("$docId",    chunk.DocumentId);
            chunkCmd.Parameters.AddWithValue("$chunkIdx", chunk.ChunkIndex);
            chunkCmd.Parameters.AddWithValue("$startPos", chunk.StartPosition);
            chunkCmd.Parameters.AddWithValue("$endPos",   chunk.EndPosition);
            chunkCmd.Parameters.AddWithValue("$text",     chunk.Text);
            chunkCmd.Parameters.AddWithValue("$meta",     JsonSerializer.Serialize(chunk.Metadata));
            chunkCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Remove(string documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM rag_documents WHERE doc_id = $docId;
            DELETE FROM rag_chunks     WHERE doc_id = $docId;
            """;
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<DocumentSummary>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText =
                "SELECT doc_id, file_name, content_type, tags_json, ingested_at, chunk_count " +
                "FROM rag_documents";
            using var reader = cmd.ExecuteReader();
            var results = new List<DocumentSummary>();
            while (reader.Read())
            {
                var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3))
                           ?? new Dictionary<string, string>(StringComparer.Ordinal);
                results.Add(new DocumentSummary
                {
                    DocumentId  = reader.GetString(0),
                    FileName    = reader.GetString(1),
                    ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Tags        = tags,
                    IngestedAt  = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    ChunkCount  = reader.GetInt32(5),
                });
            }
            return (IReadOnlyList<DocumentSummary>)results;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TextChunk>> GetChunksAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                SELECT chunk_index, start_pos, end_pos, text, metadata_json
                FROM rag_chunks
                WHERE doc_id = $docId
                ORDER BY chunk_index
                """;
            cmd.Parameters.AddWithValue("$docId", documentId);
            using var reader = cmd.ExecuteReader();
            var results = new List<TextChunk>();
            while (reader.Read())
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4))
                               ?? new Dictionary<string, string>(StringComparer.Ordinal);
                results.Add(new TextChunk
                {
                    DocumentId    = documentId,
                    ChunkIndex    = reader.GetInt32(0),
                    StartPosition = reader.GetInt32(1),
                    EndPosition   = reader.GetInt32(2),
                    Text          = reader.GetString(3),
                    Metadata      = metadata,
                });
            }
            return (IReadOnlyList<TextChunk>)results;
        }, cancellationToken);
    }

    public Task<DataManagerStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(chunk_count), 0) FROM rag_documents";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new DataManagerStats
            {
                DocumentCount   = reader.GetInt32(0),
                TotalChunkCount = reader.GetInt32(1),
            };
        }, cancellationToken);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialised) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _initLock.Wait(cancellationToken);
            try
            {
                if (_initialised) return;
                InitialiseCore();
                _initialised = true;
            }
            finally
            {
                _initLock.Release();
            }
        }, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rag_documents; DELETE FROM rag_chunks;";
            cmd.ExecuteNonQuery();
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureInitialised()
    {
        if (_initialised) return;
        _initLock.Wait();
        try
        {
            if (_initialised) return;
            InitialiseCore();
            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void InitialiseCore()
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        CreateSchema(conn);

        if (_collectionName is not null)
        {
            var storedName = SqliteStoreHelper.ReadMetadata(conn, "doc_store_collection_name");
            if (storedName is not null &&
                !string.Equals(storedName, _collectionName, StringComparison.Ordinal))
            {
                ClearData(conn);
            }
            SqliteStoreHelper.WriteMetadata(conn, "doc_store_collection_name", _collectionName);
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        SqliteStoreHelper.EnsureMetadataTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_documents (
                doc_id       TEXT    NOT NULL PRIMARY KEY,
                file_name    TEXT    NOT NULL,
                content_type TEXT,
                tags_json    TEXT    NOT NULL DEFAULT '{}',
                ingested_at  TEXT    NOT NULL,
                chunk_count  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rag_chunks (
                doc_id        TEXT    NOT NULL,
                chunk_index   INTEGER NOT NULL,
                start_pos     INTEGER NOT NULL DEFAULT 0,
                end_pos       INTEGER NOT NULL DEFAULT 0,
                text          TEXT    NOT NULL,
                metadata_json TEXT    NOT NULL DEFAULT '{}',
                PRIMARY KEY (doc_id, chunk_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ClearData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM rag_documents;
            DELETE FROM rag_chunks;
            DELETE FROM rag_metadata WHERE key = 'doc_store_collection_name';
            """;
        cmd.ExecuteNonQuery();
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
cd c:/Projects/Prive/Rag.NET
dotnet test tests/Rag.NET.Tests -q --filter "FullyQualifiedName~SqliteDocumentStoreTests" 2>&1 | tail -10
```

Expected: all 10 pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Storage/SqliteDocumentStore.cs \
        tests/Rag.NET.Tests/Storage/SqliteDocumentStoreTests.cs
git commit -m "feat: add SqliteDocumentStore — SQLite-backed IRagDataManager implementation"
```

---

### Task 3 — Wire `DocumentIngestor` to `IRagDataManager`

**Files:**
- Modify: `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- Modify: `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs`

**Context — current `DocumentIngestor` constructor (read before editing):**

```csharp
public sealed class DocumentIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ChunkingOptions chunkingOptions,
    IBm25Index bm25Index,
    IParentChunkStore? parentStore = null,
    ParentDocumentOptions? parentOptions = null) : IIngestor
```

`bm25Index` is required (no default). Add `IRagDataManager? dataManager = null` as the last parameter.

**Integration points:**

1. In `IngestAsync`, the BM25 loop is:
   ```csharp
   foreach (ref readonly var ec in CollectionsMarshal.AsSpan(embeddedChunks))
   {
       var id = System.Threading.Interlocked.Increment(ref _nextBm25DocId);
       bm25Index.Add(id, ec.Chunk);
   }
   return new IngestionResult { ... };
   ```
   After the loop, before `return`, add: `dataManager?.Add(metadata, chunks);`

2. In `DeleteAsync`:
   ```csharp
   await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken)...;
   bm25Index.Remove(documentId);
   parentStore?.Remove(documentId);
   ```
   After `parentStore?.Remove`, add: `dataManager?.Remove(documentId);`

**Step 1: Write the failing tests**

Read `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs` first to see what helpers are already present (`ToAsyncEnumerable`, `_parser`, `_chunker`, etc.), then append:

```csharp
[Fact]
public async Task IngestAsync_WithDataManager_RecordsDocumentAndChunks()
{
    var ct = TestContext.Current.CancellationToken;
    var dataManager = Substitute.For<IRagDataManager>();
    var sut = new DocumentIngestor(
        [_parser], _chunker, _vectorStore, _embedder,
        new ChunkingOptions(), _bm25Index,
        dataManager: dataManager);

    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
    var section  = new DocumentSection  { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk    = new TextChunk        { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, ct).Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), ct).Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    using var stream = new MemoryStream("hello"u8.ToArray());
    await sut.IngestAsync(stream, metadata, cancellationToken: ct);

    dataManager.Received(1).Add(
        Arg.Is<DocumentMetadata>(m => m.DocumentId == "doc-1"),
        Arg.Is<IReadOnlyList<TextChunk>>(c => c.Count == 1 && c[0].Text == "Hello"));
}

[Fact]
public async Task DeleteAsync_WithDataManager_RemovesDocument()
{
    var ct = TestContext.Current.CancellationToken;
    var dataManager = Substitute.For<IRagDataManager>();
    var sut = new DocumentIngestor(
        [_parser], _chunker, _vectorStore, _embedder,
        new ChunkingOptions(), _bm25Index,
        dataManager: dataManager);

    await sut.DeleteAsync("doc-1", ct);

    dataManager.Received(1).Remove("doc-1");
}
```

Check the using directives at the top of the test file — `Rag.NET.Abstractions` must be imported for `IRagDataManager`. Add it if absent.

**Step 2: Run tests to verify they fail**

```bash
cd c:/Projects/Prive/Rag.NET
dotnet test tests/Rag.NET.Tests -q --filter "WithDataManager" 2>&1 | tail -10
```

Expected: FAIL — `DocumentIngestor` doesn't accept `dataManager` yet.

**Step 3: Modify `DocumentIngestor`**

Two changes:

**3a. Add the parameter** to the constructor (last, optional):

```csharp
public sealed class DocumentIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ChunkingOptions chunkingOptions,
    IBm25Index bm25Index,
    IParentChunkStore? parentStore = null,
    ParentDocumentOptions? parentOptions = null,
    IRagDataManager? dataManager = null) : IIngestor   // ← add this line
```

**3b. Call `dataManager?.Add` in `IngestAsync`** — after the BM25 loop, before `return`:

```csharp
        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(embeddedChunks))
        {
            var id = System.Threading.Interlocked.Increment(ref _nextBm25DocId);
            bm25Index.Add(id, ec.Chunk);
        }

        dataManager?.Add(metadata, chunks);    // ← add this line

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
```

**3c. Call `dataManager?.Remove` in `DeleteAsync`** — after `parentStore?.Remove`:

```csharp
    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        bm25Index.Remove(documentId);
        parentStore?.Remove(documentId);
        dataManager?.Remove(documentId);    // ← add this line
    }
```

**Step 4: Run the new tests to verify they pass**

```bash
cd c:/Projects/Prive/Rag.NET
dotnet test tests/Rag.NET.Tests -q --filter "WithDataManager" 2>&1 | tail -10
```

Expected: both pass.

**Step 5: Run all DocumentIngestor tests to check for regressions**

```bash
dotnet test tests/Rag.NET.Tests -q --filter "FullyQualifiedName~DocumentIngestorTests" 2>&1 | tail -10
```

Expected: all pass (existing tests unaffected — `dataManager` defaults to null).

**Step 6: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests -q 2>&1 | tail -5
```

Expected: all pass, 0 failures.

**Step 7: Commit**

```bash
git add src/Rag.NET/Ingestion/DocumentIngestor.cs \
        tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs
git commit -m "feat: wire DocumentIngestor to IRagDataManager — records docs/chunks on ingest, removes on delete"
```

---

### Task 4 — Mark feature as done in the backlog

**File:**
- Modify: `docs/reference/features.md`

**Step 1: Update the priority table**

Find the `Data Management API` row in the table at the bottom of [docs/reference/features.md](docs/reference/features.md):

```markdown
| [ ] | Data Management API | Medium | `IVectorStore` extension |
```

Change to:

```markdown
| [x] | Data Management API | Medium | `IVectorStore` extension |
```

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark Data Management API as complete in feature backlog"
```
