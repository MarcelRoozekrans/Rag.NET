# Parent-Document Retrieval Design

## Goal

Index small chunks for precise embedding matching but return their larger parent chunks to the LLM for answer generation. Resolves the fundamental tension between embedding precision (small chunks) and answer quality (large context).

## Architecture

Two new components following established patterns:

### Ingestion side

`DocumentIngestor` gains an optional second chunking pass. When `ParentDocumentOptions` is registered in DI, it chunks each `DocumentSection` twice:

1. **Parent pass** — chunks at `ParentChunkSize` (default 2048), stored in `InMemoryParentChunkStore` singleton (keyed by `documentId:parentChunkIndex`)
2. **Child pass** — chunks at `MaxChunkSize` (default 512), embedded and stored in the vector store + BM25 index as usual

Each child chunk gets a `_parentKey` metadata entry mapping it to its parent. The mapping uses `StartPosition`/`EndPosition` overlap — a child belongs to whichever parent chunk's position range contains the child's start position.

### Retrieval side

`ParentDocumentRetriever` is a new decorator in the retrieval chain. After the inner retriever returns child `SearchResult`s, it:

1. Groups children by `_parentKey`
2. Deduplicates (multiple children may share a parent)
3. Looks up parent text from `InMemoryParentChunkStore`
4. Replaces each `SearchResult.Chunk.Text` with the parent text
5. Assigns score = max child score among children sharing that parent

The result count may shrink (e.g., 5 children from 3 parents → 3 results). To compensate, the decorator over-fetches from the inner retriever.

### Decorator chain position

```
ResultCacheRetriever              (present when UseCaching() called)
  → LostInTheMiddleRetriever      (always present)
    → RedundancyFilterRetriever   (always present)
      → ParentDocumentRetriever   (present when UseParentDocumentRetrieval() called)
        → RerankingRetriever      (present when IReranker registered)
          → MultiQueryRetriever   (present when IQueryExpander registered)
            → HydeRetriever       (present when IHypotheticalDocumentGenerator registered)
              → EmbeddingCacheRetriever  (present when UseCaching() called)
                → VectorStoreRetriever   (base — always present)
```

Position rationale: reranking scores against child chunks (precise match), then parent replacement happens, then redundancy filtering operates on parent text (since two children sharing a parent would produce duplicate results).

### InMemoryParentChunkStore

Process-scoped singleton, same trade-off as `InMemoryBm25Index` — not persisted, rebuilt on re-ingestion. Keyed by `"{documentId}:{parentChunkIndex}"`. Supports add, get, and delete-by-document-id operations.

## Data Flow

### Ingestion

```
DocumentSection ("The quick brown fox... [2000 chars]")
    │
    ├── Chunk pass 1: ParentChunkSize=2048 → Parent chunks [P0, P1, ...]
    │   └── stored in InMemoryParentChunkStore
    │
    └── Chunk pass 2: MaxChunkSize=512 → Child chunks [C0, C1, C2, ...]
        ├── each child gets Metadata["_parentKey"] = "docId:parentChunkIndex"
        ├── embedded via IEmbeddingGenerator
        └── stored in IVectorStore + InMemoryBm25Index
```

### Retrieval

```
Query → ... → inner retriever returns child SearchResults
    │
    ParentDocumentRetriever:
    ├── Group children by _parentKey → deduplicate parents
    ├── Look up parent text from InMemoryParentChunkStore
    ├── Replace each SearchResult.Chunk.Text with parent text
    ├── Score = max child score among children sharing that parent
    └── Return deduplicated parent SearchResults
```

## Configuration & DI Wiring

### ParentDocumentOptions

```csharp
public class ParentDocumentOptions
{
    public int ParentChunkSize { get; set; } = 2048;
    public int ParentOverlap { get; set; } = 100;
}
```

### Builder registration

```csharp
services.AddRagNet(b => b
    .UsePgVector(connectionString)
    .UseParentDocumentRetrieval(o =>
    {
        o.ParentChunkSize = 4096;
        o.ParentOverlap = 200;
    }));
```

`UseParentDocumentRetrieval()` registers `ParentDocumentOptions` and `InMemoryParentChunkStore` as singletons. The decorator and ingestion changes are conditional on `ParentDocumentOptions` being present in DI.

### RetrievalOptions addition

```csharp
public bool UseParentDocument { get; init; } = true;
```

On by default when registered, opt-out per-call. Same pattern as `UseReranking`, `UseHyde`, `UseCacheResult`.

## Error Handling & Logging

Follows the established decorator pattern:
- Parent lookup failures are silent — the decorator logs a warning and returns the original child chunk unmodified
- `OperationCanceledException` is rethrown

New `[LoggerMessage]` entries in `RagPipelineLog.cs`:

| Component | Level | Message |
|-----------|-------|---------|
| `ParentDocumentRetriever` | Debug | `Parent document retrieved for query '{Query}': {ChildCount} children → {ParentCount} parents` |
| `ParentDocumentRetriever` | Warning | `Parent document lookup failed for query '{Query}', returning child chunks` |

## Testing

- `InMemoryParentChunkStore` — store, retrieve, delete, key not found
- `ParentDocumentRetriever` — parent replacement, deduplication (multiple children → one parent), scoring (max child score), opt-out flag, error fallback, over-fetch
- `DocumentIngestor` — dual chunking pass when `ParentDocumentOptions` registered, single pass when not, parent-child position mapping
- `CacheKeyGenerator` — `UseParentDocument` included in result cache key
- DI integration — decorator present when `UseParentDocumentRetrieval()` called, absent when not
- Benchmark — decorator overhead with mocked store

## Docs

- Update `docs/architecture.md` — add decorator to chain diagram
- Update `docs/retrieval.md` — new "Parent-Document Retrieval" section
- Update `docs/observability.md` — add log messages
- Update `docs/features.md` — mark feature done
- Update `docs/benchmarks.md` — add benchmark section
