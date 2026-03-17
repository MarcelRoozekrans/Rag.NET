# Design: MMR Retrieval + SQLite Persistence

**Date:** 2026-03-17
**Status:** Approved

---

## Feature A — Maximal Marginal Relevance (MMR) Retrieval

### Problem

`RetrieveAsync` returns results ranked purely by relevance score. When a corpus contains many near-similar chunks (e.g., repeated boilerplate, paraphrased paragraphs), the top-K results can be highly redundant — the LLM receives the same information multiple times and misses other relevant but less-repeated content.

The `RedundancyFilter` addresses near-duplicates with a binary drop, but it is not query-aware: it only considers inter-chunk similarity, not relevance to the query. MMR solves this by jointly optimising both relevance and diversity in a single greedy selection pass.

### Algorithm

At each selection step, pick the candidate `d` maximising:

```
score(d) = λ · sim(d, query) – (1–λ) · max_{s∈S} sim(d, s)
```

Where:
- `sim(d, query)` — cosine similarity between chunk d and the query embedding
- `max_{s∈S} sim(d, s)` — maximum cosine similarity between d and any already-selected chunk
- `λ` (lambda) — trade-off weight; `1.0` = pure relevance, `0.0` = pure diversity
- `S` — set of already-selected chunks

Repeat until `TopK` chunks are selected.

### Components

| File | Role |
|------|------|
| `PostRetrieval/MmrSelector.cs` | Static class. `SelectAsync(query, candidates, embedder, topK, lambda)`. Embeds query + chunks, runs greedy selection. |
| `Retrieval/MmrRetriever.cs` | Decorator implementing `IRetriever`. Checks `opts.UseMmr`. Over-fetches via `opts with { TopK = candidateCount, UseMmr = false }`, then calls `MmrSelector`. |

### `RetrievalOptions` additions

```csharp
// Opt-in per call (default false — unlike most features which default to true)
bool UseMmr { get; init; } = false
float MmrLambda { get; init; } = 0.5f      // 0 = max diversity, 1 = max relevance
int? MmrCandidateCount { get; init; }       // defaults to TopK * 3
```

`UseMmr` defaults to `false` (opt-in), consistent with `UseRedundancyFilter`. The decorator is a no-op unless the call explicitly sets `UseMmr = true`.

### `RagBuilder`

```csharp
rag.UseMmr()
```

Registers `MmrRetriever` in the decorator chain. No options object — all config is per-call.

### Decorator chain position

MMR runs after redundancy filtering (if active) and before reranking, in the same position as the over-fetch pattern used by `RerankingRetriever`.

### Error handling

If embedding fails, log a warning and return candidates in their original score order (same pattern as `RedundancyFilterRetriever`).

---

## Feature D — SQLite Persistence for In-Memory Indexes

### Problem

Both `InMemoryBm25Index` and `InMemoryParentChunkStore` are process-scoped singletons. On application restart, all indexed data is lost and must be rebuilt by re-ingesting every document. For large corpora this can take hours of embedding API calls.

### Stale data scenarios and mitigations

| Scenario | Mitigation |
|----------|------------|
| Document deleted via pipeline | `Remove(documentId)` propagates synchronously to SQLite |
| Document re-ingested with `Overwrite = true` | Remove then re-add — same as memory path, now also in SQLite |
| App restarts with the **same** vector store | Load from SQLite — data is consistent |
| App restarts with a **different** vector store / collection | `collection_name` guard: if registered name doesn't match SQLite metadata, wipe all rows and start fresh |
| Manual reset needed | `ClearAsync()` on the SQLite store |
| Vector store modified externally (bypassing Rag.NET) | Not automatically detected — user must call `ClearAsync()` or change `collectionName` |

The `collectionName` guard is the primary safeguard against stale data. It is optional — if omitted, the guard is skipped.

### New interfaces

To allow swapping in SQLite variants, two interfaces are extracted:

```csharp
// Abstractions/IBm25Index.cs
public interface IBm25Index : IDisposable
{
    void Add(int docId, TextChunk chunk);
    void Remove(string documentId);
    IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK);
}

// Abstractions/IParentChunkStore.cs
public interface IParentChunkStore
{
    void Add(string documentId, int parentChunkIndex, string text);
    bool TryGet(string documentId, int parentChunkIndex, out string? text);
    void Remove(string documentId);
}
```

`InMemoryBm25Index` and `InMemoryParentChunkStore` implement these interfaces. `VectorStoreRetriever` and `ParentDocumentRetriever` are updated to inject `IBm25Index` / `IParentChunkStore`.

### SQLite schema

```sql
-- Stale guard
CREATE TABLE IF NOT EXISTS rag_metadata (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- BM25: raw chunk data; posting list is derived state, rebuilt on load
CREATE TABLE IF NOT EXISTS bm25_docs (
    doc_id        INTEGER NOT NULL PRIMARY KEY,
    document_id   TEXT NOT NULL,
    chunk_text    TEXT NOT NULL,
    chunk_source  TEXT,
    collection    TEXT,
    metadata_json TEXT,
    token_length  INTEGER NOT NULL
);

-- Parent chunk store
CREATE TABLE IF NOT EXISTS parent_chunks (
    document_id        TEXT NOT NULL,
    parent_chunk_index INTEGER NOT NULL,
    text               TEXT NOT NULL,
    PRIMARY KEY (document_id, parent_chunk_index)
);
```

### SQLite implementations

| File | Role |
|------|------|
| `Storage/SqliteBm25Index.cs` | Implements `IBm25Index`. Wraps `InMemoryBm25Index` (composition). Write-through on `Add`/`Remove`. Lazy async init. |
| `Storage/SqliteParentChunkStore.cs` | Implements `IParentChunkStore`. Same pattern. |

**Lazy initialisation:** First call to `Add`/`Remove`/`Search` triggers `InitializeAsync()` via `SemaphoreSlim`. This creates tables, runs the collection name guard, and loads all rows into the in-memory store by calling `Add()`. No `IHostedService` required.

**Serialisation:** `TextChunk` is serialised to/from JSON for the `chunk_*` columns. `System.Text.Json` source-generated context.

### `RagBuilder`

```csharp
rag.UseSqlitePersistence("rag-data.db", collectionName: "my-docs");
```

Registers `SqliteBm25Index` as `IBm25Index` and `SqliteParentChunkStore` as `IParentChunkStore`. Works independently of `UseParentDocumentRetrieval()` — if parent-doc retrieval is not registered, `SqliteParentChunkStore` is registered but never written to.

### Package

Both SQLite types and the interfaces live in the core `Rag.NET` package. `Microsoft.Data.Sqlite` is added as a dependency, gated behind the `UseSqlitePersistence` call path (no overhead when not used).

---

## Interaction between A and D

MMR and SQLite persistence are independent. SQLite persistence keeps the BM25 index and parent chunk store alive across restarts, which makes the MMR candidate pool richer from the first request after restart (since hybrid search over BM25 produces more diverse candidates for MMR to select from).
