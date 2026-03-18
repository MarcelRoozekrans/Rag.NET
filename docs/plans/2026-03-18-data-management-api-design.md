# Data Management API — Design

**Date:** 2026-03-18
**Status:** Approved

---

## Goal

Add a read surface for browsing and managing ingested data via `IRagDataManager`. Allows users to list documents, inspect chunks, get stats, and coordinate cleanup — without going directly to the vector store.

---

## Architecture

A SQLite-backed sidecar store (`SqliteDocumentStore`) sits alongside the existing `SqliteBm25Index` and `SqliteParentChunkStore` in `src/Rag.NET/Storage/`. All three share the same `.db` file and `SqliteStoreHelper` infrastructure.

`DocumentIngestor` gains one optional `IRagDataManager?` constructor parameter (same pattern as `IBm25Index?` and `IParentChunkStore?`). After writing chunks to the vector store, the ingestor calls `dataManager?.Add(metadata, chunks)`. On delete, it calls `dataManager?.Remove(documentId)`.

**Zero changes to `IVectorStore`, `IRagPipeline`, or any vector store implementation.**

Each pipeline instance = one collection. The collection is implicit in the `SqliteDocumentStore` constructor (same stale-guard pattern as `SqliteBm25Index`).

---

## Interface

```csharp
/// <summary>
/// Sidecar metadata store tracking ingested documents and their chunks.
/// Write methods are called internally by <see cref="DocumentIngestor"/>;
/// read methods are the public management surface.
/// </summary>
public interface IRagDataManager : IDisposable, IAsyncDisposable
{
    // Write — called by DocumentIngestor
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

---

## Models

```csharp
public sealed record DocumentSummary
{
    public required string DocumentId   { get; init; }
    public required string FileName     { get; init; }
    public string?         ContentType  { get; init; }
    public required int    ChunkCount   { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public IDictionary<string, string> Tags { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record DataManagerStats
{
    public required int DocumentCount   { get; init; }
    public required int TotalChunkCount { get; init; }
}
```

---

## SQLite Schema

Two new tables in the shared `.db` file:

```sql
CREATE TABLE IF NOT EXISTS rag_documents (
    doc_id       TEXT NOT NULL PRIMARY KEY,
    file_name    TEXT NOT NULL,
    content_type TEXT,
    tags_json    TEXT NOT NULL DEFAULT '{}',
    ingested_at  TEXT NOT NULL,   -- ISO-8601 UTC
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
```

Stale-guard uses the existing `rag_metadata` table with key `"doc_store_collection_name"` — same pattern as `SqliteBm25Index` (`"bm25_collection_name"`). Mismatch wipes both `rag_documents` and `rag_chunks`.

---

## Integration

### `DocumentIngestor`

Add one optional constructor parameter:

```csharp
public sealed class DocumentIngestor(
    IReadOnlyList<IDocumentParser> parsers,
    IChunkingStrategy chunker,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ChunkingOptions chunkingOptions,
    IBm25Index? bm25Index = null,
    IParentChunkStore? parentStore = null,
    ParentDocumentOptions? parentOptions = null,
    IRagDataManager? dataManager = null)
```

After `vectorStore.StoreAsync(embeddedChunks)` in `IngestAsync`:
```csharp
dataManager?.Add(metadata, chunks);
```

After `vectorStore.DeleteByDocumentIdAsync(documentId)` in `DeleteAsync`:
```csharp
dataManager?.Remove(documentId);
```

### DI Registration

No new extension method — users wire it up directly:

```csharp
builder.Services.AddSingleton<IRagDataManager>(
    new SqliteDocumentStore("ragnet.db", collectionName: "my-collection"));
```

---

## `SqliteDocumentStore` Behaviour

- **Lazy init + stale guard** — identical pattern to `SqliteBm25Index`
- **`Add`** — `INSERT OR REPLACE` into both `rag_documents` and `rag_chunks`; sets `ingested_at` to `DateTimeOffset.UtcNow`
- **`Remove`** — deletes from both tables by `doc_id`
- **`GetDocumentsAsync`** — `SELECT` all from `rag_documents`, deserialise `tags_json`
- **`GetChunksAsync`** — `SELECT` from `rag_chunks WHERE doc_id = $docId ORDER BY chunk_index`
- **`GetStatsAsync`** — two scalar queries: `COUNT(*)` on `rag_documents`, `SUM(chunk_count)` on `rag_documents`
- **`ClearAsync`** — `DELETE FROM rag_documents; DELETE FROM rag_chunks`
- **`InitializeAsync`** — same async lazy-init pattern as `SqliteBm25Index.InitializeAsync`
- **`ObjectDisposedException.ThrowIf`** guard on every public method

---

## Error Handling

- `Add` / `Remove` are synchronous. If the SQLite write fails after the vector store write already succeeded, the sidecar is out of sync. This is the same accepted risk carried by `SqliteBm25Index`. No rollback.
- Read methods propagate SQLite exceptions naturally.
- Stale guard fires on first use after collection name change — wipes both tables and re-initialises.

---

## Testing

### `SqliteDocumentStoreTests` (new file, real SQLite, temp db)

| Test | Verifies |
|------|----------|
| `Add_ThenGetDocuments_ReturnsSummaryWithCorrectFields` | All DocumentSummary fields populated correctly |
| `Add_ThenGetChunks_ReturnsOriginalTextChunks` | Chunk text, index, positions, metadata preserved |
| `Add_ThenGetStats_ReturnsCorrectCounts` | DocumentCount and TotalChunkCount accurate |
| `Remove_ThenGetDocuments_ReturnsEmpty` | Document removed from listing |
| `Remove_ThenGetChunks_ReturnsEmpty` | Chunks removed when document deleted |
| `ClearAsync_RemovesAllDocumentsAndChunks` | Both tables wiped |
| `CollectionNameMismatch_WipesExistingData` | Stale guard fires on restart |
| `Add_AfterDispose_ThrowsObjectDisposedException` | Dispose guard |
| `GetDocuments_AfterDispose_ThrowsObjectDisposedException` | Dispose guard |
| `InitializeAsync_CanBeAwaited_ThenAddWorks` | Async init path |

### `DocumentIngestorTests` (existing file — append 2 tests)

| Test | Verifies |
|------|----------|
| `IngestAsync_WithDataManager_RecordsDocumentAndChunks` | dataManager.Add called with correct metadata + chunks |
| `DeleteAsync_WithDataManager_RemovesDocument` | dataManager.Remove called with correct documentId |

---

## Files

| Action | Path |
|--------|------|
| Create | `src/Rag.NET/Abstractions/IRagDataManager.cs` |
| Create | `src/Rag.NET/Models/DocumentSummary.cs` |
| Create | `src/Rag.NET/Models/DataManagerStats.cs` |
| Create | `src/Rag.NET/Storage/SqliteDocumentStore.cs` |
| Modify | `src/Rag.NET/Ingestion/DocumentIngestor.cs` |
| Create | `tests/Rag.NET.Tests/Storage/SqliteDocumentStoreTests.cs` |
| Modify | `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs` |
