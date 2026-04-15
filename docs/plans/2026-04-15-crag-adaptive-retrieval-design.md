# CRAG + Adaptive Retrieval Design

**Goal:** Add two complementary retrieval behaviors — Adaptive Retrieval (routes query complexity to optimal retrieval settings) and CRAG (post-retrieval relevance check with web search fallback) — as opt-in middleware behaviors in the existing `IRetrievalBehavior` pipeline.

**Architecture:** Both behaviors implement `IRetrievalBehavior` and slot into the existing middleware chain. Adaptive Retrieval runs first to tune `RetrievalOptions` based on query complexity; CRAG runs second, calls `next()` to obtain vector results, then optionally replaces or appends web results when relevance is low.

**Tech Stack:** C# 13, .NET 9, `Rag.NET.Abstractions` (pipeline + `IWebSearch`), `Rag.NET.Core` (behaviors), `Rag.NET.WebSearch.Tavily` (new package), `Microsoft.Extensions.AI` (`IChatClient`), `ZeroAlloc.Rest` (Tavily HTTP client).

---

## Section 1: Adaptive Retrieval

### Behavior placement

```
AdaptiveRetrievalBehavior → CorrectiveRagBehavior → VectorStoreBehavior
```

`AdaptiveRetrievalBehavior` inserts before `VectorStoreBehavior`. It mutates `RetrievalContext.Options` via `ctx with { Options = ctx.Options with { ... } }` then calls `next()` with the updated context.

### Query complexity classification

**Tier 1 — heuristic (always runs, no LLM cost):**

| Signal | Threshold | Result |
|--------|-----------|--------|
| Word count | ≤ 6 words | `simple` |
| Contains keywords: `how`, `why`, `compare`, `difference`, `explain` | any | `complex` |
| Contains `and`, `also`, `additionally`, `furthermore`, `as well as` | ≥ 2 conjunctions | `multi_hop` |

**Tier 2 — LLM fallback (only for ambiguous cases):**
- If `IChatClient` is injected and the heuristic is inconclusive, classify via a compact prompt
- LLM returns `simple` / `complex` / `multi_hop`; any unexpected value defaults to `complex`

### Strategy mapping

| Complexity | TopK | UseMultiQuery | UseHyde |
|------------|------|---------------|---------|
| `simple`   | 3    | false         | false   |
| `complex`  | 8    | true          | false   |
| `multi_hop`| 10   | true          | true    |

### Options

```csharp
// Added to RetrievalOptions
bool UseAdaptiveRetrieval = false;   // opt-in, default off
```

### Observability

`ctx.Extensions["adaptive_complexity"]` set to `"simple"` / `"complex"` / `"multi_hop"` for downstream telemetry.

---

## Section 2: CRAG (Corrective RAG)

### Behavior placement

Runs after `AdaptiveRetrievalBehavior`, before `VectorStoreBehavior`:

```
AdaptiveRetrievalBehavior → CorrectiveRagBehavior → VectorStoreBehavior
```

`CorrectiveRagBehavior` calls `next()`, receives the vector `SearchResult` list, scores relevance, and optionally replaces or appends web results.

### Relevance scoring

**With `IChatClient` (injected, optional):**
- Prompt each chunk: classify as `relevant` / `ambiguous` / `irrelevant`
- Score = fraction of `relevant` results

**Without `IChatClient` (heuristic fallback):**
- Keyword overlap between query tokens and result content
- Score = ratio of matched tokens / total query tokens

**Threshold:** if score < `CragScoreThreshold` (default `0.5`), trigger web fallback.

### Web fallback flow

```
score < threshold
  → IWebSearch.SearchAsync(ctx.Query, topK: ctx.Options.TopK, ct)
  → CragFallbackMode.Replace  →  discard vector results, return web results only
  → CragFallbackMode.Append   →  concatenate web results after vector results
```

### IWebSearch abstraction

Added to `Rag.NET.Abstractions`:

```csharp
public interface IWebSearch
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct);
}
```

Returns the same `SearchResult` shape used throughout the pipeline — no special casing downstream.

### Tavily implementation (`Rag.NET.WebSearch.Tavily`)

- `TavilyWebSearch : IWebSearch`
- Typed `HttpClient` via `ZeroAlloc.Rest` source-generated client calling `https://api.tavily.com/search`
- `AddTavilyWebSearch(string apiKey)` DI extension
- Maps Tavily response fields (`title`, `url`, `content`, `score`) to `SearchResult`

### Options

```csharp
// Added to RetrievalOptions
bool UseCrag = false;                              // opt-in, default off
float CragScoreThreshold = 0.5f;
CragFallbackMode CragFallbackMode = Replace;
```

```csharp
public enum CragFallbackMode { Replace, Append }
```

### Error handling

If `IWebSearch` throws or is not registered:
- Log warning via `ctx.Logger`
- Return original vector results unchanged (graceful degradation, no exception propagation)

### Observability

`ctx.Extensions["crag_triggered"]` set to `"true"` / `"false"` for downstream telemetry.

---

## Testing strategy

- **Unit tests** for `AdaptiveRetrievalBehavior`: heuristic classification table, LLM fallback path, options mutation, `ctx.Extensions` entry
- **Unit tests** for `CorrectiveRagBehavior`: above-threshold (no fallback), below-threshold (Replace + Append modes), `IWebSearch` throws (graceful degradation), no `IChatClient` (keyword heuristic path)
- **Unit tests** for `TavilyWebSearch`: HTTP request shape, response mapping, error handling
- **Integration test** for `TavilyWebSearch`: WireMock cassette for `api.tavily.com/search`
- No end-to-end pipeline integration tests (existing pipeline tests cover middleware chaining)
