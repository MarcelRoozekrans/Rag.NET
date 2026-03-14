# Search Result Caching Design

## Goal

Add two-level retrieval caching (embedding cache + full result cache) to reduce embedding API and vector store costs on repeated queries.

## Architecture

Two new decorators in the retrieval chain, backed by `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`):

```
ResultCacheRetriever               (outermost — caches final results)
  → LostInTheMiddleRetriever
    → RedundancyFilterRetriever
      → RerankingRetriever
        → MultiQueryRetriever
          → HydeRetriever
            → EmbeddingCacheRetriever  (caches query→embedding)
              → VectorStoreRetriever
```

- **`EmbeddingCacheRetriever`** — wraps `VectorStoreRetriever`, caches the query→embedding vector. On cache hit, skips the `IEmbeddingGenerator` call. The actual text being embedded (`EmbeddingTextOverride ?? query`) is the cache key input.
- **`ResultCacheRetriever`** — wraps the outermost retriever, caches the complete `IReadOnlyList<SearchResult>` after all post-processing (reranking, redundancy filter, reordering). On cache hit, skips the entire retrieval pipeline.

Both use `HybridCache` — in-process `IMemoryCache` as L1, optional `IDistributedCache` (Redis, SQL, etc.) as L2. Users who already have a distributed cache registered get L1+L2 automatically.

## Cache Key Strategy

**`ResultCacheRetriever`** — SHA256 hash of all inputs that affect the final result:
- `query` + `TopK` + `MinScore` + `MetadataFilter` + `UseHybridSearch` + `UseRedundancyFilter` + `RedundancyThreshold` + `UseMultiQuery` + `UseReranking` + `CandidateCount` + `UseHyde`
- Prefixed with `"rag:result:"`

**`EmbeddingCacheRetriever`** — SHA256 hash of:
- `EmbeddingTextOverride ?? query` (the actual text being embedded)
- Prefixed with `"rag:embed:"`

Fixed-length keys are safe for any backing store.

## Cache Invalidation

No automatic invalidation on ingest/delete — tracking which documents contributed to which cached results adds significant complexity. Instead:

- **TTL-based expiry** — configurable per cache level
- **Per-call opt-out** — set `UseCacheResult = false` or `UseCacheEmbedding = false` on `RetrievalOptions` to bypass cache after bulk ingestion

## Configuration & DI Wiring

### CachingOptions

```csharp
public class CachingOptions
{
    public TimeSpan EmbeddingTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ResultTtl { get; set; } = TimeSpan.FromMinutes(5);
}
```

Embeddings get a longer default TTL (same query always produces the same vector). Results get a shorter TTL (underlying index changes with ingestion).

### RetrievalOptions additions

```csharp
public bool UseCacheEmbedding { get; init; } = true;
public bool UseCacheResult { get; init; } = true;
```

Follows the established opt-out pattern: on by default when registered, opt out per-call.

### Builder registration

```csharp
services.AddRagNet(b => b
    .UseCaching(o =>
    {
        o.EmbeddingTtl = TimeSpan.FromHours(1);
        o.ResultTtl = TimeSpan.FromMinutes(10);
    }));
```

`UseCaching()` calls `services.AddHybridCache()` and registers both decorators in the chain.

### NuGet dependency

`Microsoft.Extensions.Caching.Hybrid` added to `Rag.NET.csproj`.

## Error Handling & Logging

Follows the established decorator pattern:
- Cache misses and failures are silent — the decorator falls back to calling the inner retriever
- Cache serialization/deserialization errors log a `Warning` and pass through
- New `[LoggerMessage]` entries in `RagPipelineLog.cs`:
  - `EmbeddingCacheHit` (Debug) — `"Embedding cache hit for query '{Query}'"`
  - `ResultCacheHit` (Debug) — `"Result cache hit for query '{Query}'"`

## Testing

- Unit tests for each decorator: cache hit, cache miss, opt-out flag, error fallback
- DI integration test: verify both decorators are in the chain
- Benchmark: `CachingBenchmarks.cs` — measure cache hit vs. miss overhead

## Docs

- Update `docs/architecture.md` — add both decorators to chain diagram
- Update `docs/retrieval.md` — new "Search Result Caching" section
- Update `docs/observability.md` — add cache log messages
- Update `docs/benchmarks.md` — add caching benchmark section
- Mark feature done in `docs/features.md`
