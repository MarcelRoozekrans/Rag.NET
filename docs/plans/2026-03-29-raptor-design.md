# RAPTOR — Recursive Abstractive Processing for Tree-Organized Retrieval

**Date:** 2026-03-29
**Package:** `Rag.NET.Raptor`
**Branch:** `feature/raptor`

## Problem

Long and multi-topic documents lose context when chunked into small pieces. A question about the overall theme of a document may not match any individual leaf chunk well. RAPTOR solves this by building a hierarchical tree of summaries at ingestion time, so retrieval can match at both fine-grained (leaf) and abstract (summary) levels simultaneously.

## Approach

Separate package `Rag.NET.Raptor` referencing `Rag.NET` core (all behavior interfaces are public — no `InternalsVisibleTo` needed). Two behaviors: one for ingestion (tree building), one for retrieval (level boosting/filtering).

**External dependencies:**
- `MathNet.Numerics` — GMM clustering with BIC model selection
- UMAP .NET implementation — dimensionality reduction

## Ingestion: RaptorBehavior

Positioned after `EmbeddingBehavior`, before `StorageBehavior` in the ingestion pipeline.

**Flow:**
1. Receive `ctx.EmbeddedChunks` (leaf chunks with embeddings)
2. Skip if chunk count < `MinChunksForRaptor` (default 5)
3. Extract embedding vectors into a float matrix
4. UMAP: reduce dimensionality (e.g. 1536 → 10 dims)
5. GMM: soft-cluster reduced embeddings, BIC selects optimal cluster count
6. For each cluster: concatenate chunk texts, call `IChatClient` to summarize
7. Embed each summary via `IEmbeddingGenerator`
8. Create `EmbeddedChunk` objects with metadata: `raptor_level`, `raptor_cluster_id`, `raptor_child_ids`
9. Recurse steps 3-8 on level-N summaries until 1 cluster remains (or `MaxTreeDepth` reached)
10. Append all summary chunks to `ctx.EmbeddedChunks`
11. Call `next()` → `StorageBehavior` stores everything

## Retrieval: RaptorRetrievalBehavior

Positioned before `RerankingBehavior` in the retrieval pipeline.

**Three modes:**
- **Blend (default):** Pass-through — all levels participate via vector similarity naturally.
- **Boost:** Multiply scores of summary chunks (`raptor_level > 0`) by `SummaryBoostFactor`. Query classification via heuristic (short queries with "overview"/"summary"/"explain" trigger boost).
- **Filter:** Restrict to specific levels via `MinRaptorLevel` / `MaxRaptorLevel`.

**Implementation:** Call `next()` to get results, then post-process scores based on mode.

## Configuration

### RaptorOptions (ingestion)

| Property | Type | Default | Purpose |
|---|---|---|---|
| `Enabled` | bool | true | Toggle RAPTOR on/off |
| `MinChunksForRaptor` | int | 5 | Skip if fewer chunks |
| `ReducedDimensionality` | int | 10 | UMAP target dims |
| `MaxClusters` | int? | null | Cap for GMM (null = BIC auto) |
| `MaxTreeDepth` | int? | null | Cap recursion (null = until 1 cluster) |
| `StoreLeafChunks` | bool | true | Keep originals alongside summaries |
| `SummaryPrompt` | string | sensible default | LLM prompt for cluster summarization |
| `SummaryChatClient` | IChatClient? | null | Optional separate model for summaries |
| `SummaryEmbedder` | IEmbeddingGenerator? | null | Optional separate embedder for summaries |

### RaptorRetrievalOptions (retrieval)

| Property | Type | Default | Purpose |
|---|---|---|---|
| `Mode` | RaptorRetrievalMode | Blend | Blend / Boost / Filter |
| `SummaryBoostFactor` | double | 1.2 | Score multiplier in Boost mode |
| `MinRaptorLevel` | int? | null | Level filter lower bound |
| `MaxRaptorLevel` | int? | null | Level filter upper bound |

## DI Registration

```csharp
services.AddRagNet(rag => rag.UseRaptor(options => {
    options.ReducedDimensionality = 10;
    options.SummaryChatClient = cheaperModel;  // optional: cheaper model for summaries
    options.SummaryEmbedder = fastEmbedder;    // optional: separate embedder
}));
```

## Documentation

Three deliverables:
1. **`docs/guide/raptor.md`** — comprehensive guide: concept, when to use, how it works, configuration walkthrough, cost/performance trade-offs, retrieval modes, troubleshooting
2. **XML doc comments** — all public types, properties, methods, enums
3. **`docs/reference/features.md`** — mark RAPTOR as Done

## Task Breakdown

| # | Task | Parallel | Dependencies |
|---|---|---|---|
| 1 | UMAP + GMM math layer | Yes | — |
| 2 | RaptorBehavior (ingestion) | No | After 1 |
| 3 | RaptorRetrievalBehavior | Yes | — |
| 4 | DI registration (UseRaptor) | No | After 2+3 |
| 5 | Tests (~15) | No | After 4 |
| 6 | Documentation | Yes | — |
| 7 | Build + test | No | After 5+6 |
