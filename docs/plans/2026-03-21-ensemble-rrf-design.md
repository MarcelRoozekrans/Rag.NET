# Design: Ensemble / Reciprocal Rank Fusion (RRF)

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

Extracts the RRF hybrid-search logic currently buried inside `VectorStoreBehavior` into a first-class `EnsembleBehavior` with configurable per-retriever weights and k. Makes weighted hybrid search available across all vector store backends with a clean, visible API matching the LangChain/LlamaIndex industry standard.

---

## Architecture

### `EnsembleOptions`

```csharp
public sealed class EnsembleOptions
{
    public float DenseWeight { get; init; } = 0.5f;
    public float Bm25Weight  { get; init; } = 0.5f;
    public int   K           { get; init; } = 60;
}
```

Added to `src/Rag.NET/Models/Options/EnsembleOptions.cs`.

`DenseWeight` and `Bm25Weight` are rank multipliers, not probabilities — they do not need to sum to 1.0. `K` is the RRF constant (60 is the canonical value from Cormack et al., 2009).

### `RetrievalOptions`

```csharp
public EnsembleOptions? EnsembleOptions { get; init; }
```

`UseHybridSearch` remains the activation flag — no breaking change. `EnsembleOptions = null` applies defaults.

### `EnsembleBehavior`

New `IRetrievalBehavior` inserted into the retrieval pipeline before `VectorStoreBehavior`.

**When `UseHybridSearch = false`:** calls `next` immediately — zero overhead, pure pass-through.

**When `UseHybridSearch = true`:**
1. Embeds the query via `IEmbeddingGenerator`
2. Runs dense search (`IVectorStore.SearchAsync`) and BM25 search (`IBm25Index.Search`) in parallel
3. Merges results using `RrfMerger.Merge` with configured weights and k
4. Returns merged results — short-circuits `next` (VectorStoreBehavior is not called)

`VectorStoreBehavior` loses its existing RRF fallback path. It becomes pure dense-only.

### `RrfMerger`

Existing `RrfMerger.Merge` gains a weighted overload:

```csharp
public static IReadOnlyList<SearchResult> Merge(
    IReadOnlyList<SearchResult> dense,
    IReadOnlyList<(TextChunk chunk, double score)> bm25,
    int topK,
    EnsembleOptions options);
```

Weighted RRF formula: `score(d) = denseWeight / (k + rank_dense) + bm25Weight / (k + rank_bm25)`

### File Layout

```
src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs   (new)
src/Rag.NET/Models/Options/EnsembleOptions.cs          (new)
src/Rag.NET/Models/Options/RetrievalOptions.cs         (modified — EnsembleOptions property)
src/Rag.NET/Search/RrfMerger.cs                        (modified — weighted overload)
src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs (modified — remove RRF fallback path)
src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs (modified — register EnsembleBehavior)
```

---

## Error Handling

- **BM25 throws (non-cancellation):** log Warning, fall back to dense-only results. Same resilience pattern as the existing BM25 retrieval path.
- **Dense search throws:** exception propagates to caller — dense is the primary retriever.
- **`K < 1`:** clamped to 1 at the start of `Merge`.
- **`OperationCanceledException`:** always re-thrown immediately.

---

## DI Registration

`EnsembleBehavior` is registered in `RetrievalPipelineBuilder` alongside the other behaviors. It is passive (pass-through) when `UseHybridSearch = false`, so it adds no overhead to pipelines that do not use hybrid search.

---

## Testing

| Scenario | Expected |
|---|---|
| `UseHybridSearch=false` | Calls `next`; VectorStoreBehavior handles search |
| `UseHybridSearch=true`, both return results | Weighted RRF merge; correct rank ordering |
| Custom weights `(0.3, 0.7)` | BM25-heavy results ranked higher |
| BM25 throws | Warning logged; dense results returned |
| One side returns empty | Merge still produces results from other side |
| `EnsembleOptions=null` | Defaults (0.5 / 0.5 / 60) applied |
| `K=1` | Valid; smallest legal value |

---

## Out of Scope

- Combining more than two retrievers (dense + BM25 is the standard pairing)
- Per-query weight adaptation
- Exposing RRF scores individually in `SearchResult`
