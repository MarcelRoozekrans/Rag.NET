# Decorator Pipeline Refactoring — Design

## Goal

Decompose the monolithic `RagPipeline` (11 constructor parameters, conditional branching on nullable dependencies) into small, single-responsibility decorator classes composable via DI. Preserve the existing `IRagPipeline` public API — zero breaking changes for consumers.

## Motivation

- `RagPipeline` grows with every new feature (multi-query, reranking, HyDE, caching, …). Each feature adds constructor params and conditional logic.
- Decorators make each feature independently testable, removable, and orderable.
- The decomposition paves the way for ZInject (`github.com/MarcelRoozekrans/ZInject`) compile-time DI registration — each decorator becomes a standalone class with a `[Singleton]` attribute.

---

## Architecture

### Three internal interfaces, one public facade

```csharp
public interface IRetriever
{
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IIngestor
{
    Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}

public interface IAnswerEngine
{
    Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

`IRagPipeline` is unchanged. `RagPipeline` becomes a thin coordinator:

```csharp
public sealed class RagPipeline(
    IRetriever retriever,
    IIngestor ingestor,
    IAnswerEngine? answerEngine = null) : IRagPipeline
{
    // Each method delegates to the appropriate interface
}
```

`IAnswerEngine.AskAsync` receives pre-retrieved `sources` — retrieval and generation are cleanly separated. `RagPipeline` calls `retriever.RetrieveAsync()` first, then passes results to `answerEngine.AskAsync()`.

---

## Base implementations

### `VectorStoreRetriever`

The leaf retriever. Embeds the query via `IEmbeddingGenerator`, calls `IVectorStore.SearchAsync()` (or `IHybridSearchable.HybridSearchAsync()` when `UseHybridSearch = true`). Manages the in-memory BM25 fallback + RRF merge for hybrid search on stores that don't implement `IHybridSearchable`.

### `DocumentIngestor`

Parse → chunk → apply metadata tags → embed → store to vector store. Also feeds the shared `InMemoryBm25Index`. `DeleteAsync` delegates to `IVectorStore.DeleteByDocumentIdAsync()` + BM25 removal.

### `ChatAnswerEngine`

Builds system prompt + context + conversation history. Calls `IChatClient.GetResponseAsync()` / `GetStreamingResponseAsync()`. Contains the current `BuildRagMessages` logic.

### Shared state: `InMemoryBm25Index`

Currently private to `RagPipeline`. Becomes a singleton registered in DI, injected into both `DocumentIngestor` (add/remove) and `VectorStoreRetriever` (search).

---

## Retrieval decorators

Each implements `IRetriever`, wraps an inner `IRetriever`, checks its per-call flag, and either applies its logic or passes through.

| Decorator | Responsibility | Per-call flag |
|-----------|---------------|---------------|
| `MultiQueryRetriever` | Expands query via `IQueryExpander`, fans out to inner, deduplicates | `UseMultiQuery` |
| `RerankingRetriever` | Over-fetches via `CandidateCount`, rescores via `IReranker`, trims to `TopK` | `UseReranking` |
| `RedundancyFilterRetriever` | Filters near-duplicate results by cosine similarity | `UseRedundancyFilter` |
| `LostInTheMiddleRetriever` | Reorders results for LLM attention pattern (Liu et al. 2023) | `UseLostInTheMiddleReordering` |

### Decorator skeleton

```csharp
public class RerankingRetriever(IRetriever inner, IReranker reranker) : IRetriever
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query, RetrievalOptions? options, CancellationToken ct)
    {
        var opts = options ?? new RetrievalOptions();
        if (!opts.UseReranking)
            return await inner.RetrieveAsync(query, options, ct);

        var expanded = opts with { TopK = opts.CandidateCount ?? opts.TopK * 3 };
        var results = await inner.RetrieveAsync(query, expanded, ct);
        return await RerankAndTrim(query, results, opts.TopK, ct);
    }
}
```

### Chain order (outermost → innermost)

```
LostInTheMiddleRetriever
  → RedundancyFilterRetriever
    → RerankingRetriever
      → MultiQueryRetriever
        → VectorStoreRetriever (base)
```

Matches the current execution order. If a decorator isn't registered, it's simply absent from the chain.

---

## Future features as decorators

### Retrieval

- **HyDE** — transforms query before search
- **MMR** — post-search diversity selection
- **Search Result Caching** — wraps search, returns cached if available
- **Self-Query Filtering** — pre-search LLM metadata filter extraction
- **Parent-Document Retrieval** — post-search child→parent swap
- **Deep Research Loop** — sufficiency-gated recursive retrieval
- **Time-Weighted Retrieval** — post-search recency score adjustment

### Ingestion

- **Content-Hash Record Manager** — skip unchanged documents
- **LLM Metadata Extraction** — enrich chunks before storing

### Answer generation

- **Map-Reduce Synthesis** — alternative to single-prompt
- **Refine (Iterative Synthesis)** — sequential chunk processing

---

## DI wiring

Public builder API unchanged:

```csharp
services.AddRagNet(rag =>
{
    rag.UseMultiQueryRetrieval();
    rag.UseReranking<OnnxReranker>();
});
```

Internally, the builder composes the decorator chain:

```csharp
services.AddSingleton<InMemoryBm25Index>();
services.AddSingleton<VectorStoreRetriever>();
services.AddSingleton<DocumentIngestor>();
services.AddSingleton<ChatAnswerEngine>();

services.AddSingleton<IRetriever>(sp =>
{
    IRetriever chain = sp.GetRequiredService<VectorStoreRetriever>();
    if (sp.GetService<IQueryExpander>() is not null)
        chain = new MultiQueryRetriever(chain, ...);
    if (sp.GetService<IReranker>() is not null)
        chain = new RerankingRetriever(chain, ...);
    chain = new RedundancyFilterRetriever(chain, ...);
    chain = new LostInTheMiddleRetriever(chain, ...);
    return chain;
});

services.AddSingleton<IRagPipeline, RagPipeline>();
```

### ZInject path

Once decorators are standalone classes, migration to ZInject is straightforward — each decorator gets a `[Singleton]` attribute for compile-time registration. Chain ordering is expressed via builder helpers or explicit wrapping in the generated method.

### RetrievalOptions

`RetrievalOptions` becomes a record (or gains `with` support) so decorators can modify options (e.g. `TopK`) before passing to the inner retriever without mutating the caller's instance.

---

## Error handling

Same graceful fallback pattern, now per-decorator:

- Each decorator catches non-cancellation exceptions and falls through to `inner.RetrieveAsync()`
- `OperationCanceledException` always re-thrown
- Each decorator logs its own warnings via `ILogger`
- No change in observable behaviour for consumers

---

## Testing

- Each decorator tested in isolation by mocking `IRetriever inner` (2-3 constructor params, not 11)
- Existing `RagPipelineTests` become thin facade tests verifying delegation
- Feature-specific tests move to per-decorator test classes (e.g. `RerankingRetrieverTests`)
- Integration tests compose a real chain with mocked dependencies

---

## Migration strategy

Incremental, not big-bang:

1. **Phase 1** — Introduce `IRetriever`, `IIngestor`, `IAnswerEngine` and base implementations. Extract logic from `RagPipeline`. All existing tests pass.
2. **Phase 2** — Extract retrieval decorators one by one (multi-query, reranking, redundancy, lost-in-middle). Each extraction is a single commit. Tests migrate alongside.
3. **Phase 3** — Update `RagBuilder` / `ServiceCollectionExtensions` to compose the decorator chain. Public API unchanged.
4. **Phase 4** (future) — Make `RetrievalOptions` a record, ZInject migration, new features as decorators.

### Breaking changes

None for consumers of `IRagPipeline`. The new interfaces (`IRetriever`, `IIngestor`, `IAnswerEngine`) are additive — power users can inject them directly for finer control.
