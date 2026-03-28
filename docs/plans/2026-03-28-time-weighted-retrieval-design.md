# Time-Weighted Retrieval — Design

**Date:** 2026-03-28
**Status:** Approved

---

## Goal

Add an `IRetriever` decorator that re-scores each returned `SearchResult` by multiplying the semantic similarity score by an exponential time-decay factor derived from the document's creation timestamp. Fresher documents retain their original score; older documents decay toward zero.

---

## Models

### `DocumentMetadata.CreatedAt`

`DocumentMetadata` gains a non-nullable `DateTime` property with a `DateTime.UtcNow` default:

```csharp
public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
```

This is a non-breaking change — existing callers that don't set it get the ingest timestamp automatically, which is the correct default for documents that have no explicit publication date.

`MetadataBehavior` is extended to serialize this field into every chunk's `Metadata` dictionary under the reserved key `"created_at"` (ISO 8601 round-trip format, `"O"` specifier):

```csharp
chunk.Metadata.TryAdd("created_at", ctx.Metadata.CreatedAt.ToString("O"));
```

`TryAdd` is intentional — callers who already store a `"created_at"` tag in `DocumentMetadata.Tags` retain their value.

### `TimeWeightedOptions`

```csharp
public sealed class TimeWeightedOptions
{
    /// <summary>
    /// Decay constant λ in score × e^(-λ × age_hours).
    /// Default 0.01 halves relevance after ~69 hours (≈ 3 days).
    /// </summary>
    public double DecayRate { get; init; } = 0.01;

    /// <summary>
    /// Ordered list of <see cref="TextChunk.Metadata"/> keys to try when
    /// <c>"created_at"</c> is absent. First key with a parseable ISO 8601 value wins.
    /// Useful when documents originate from external systems with their own timestamp fields.
    /// </summary>
    public IReadOnlyList<string> FallbackMetadataKeys { get; init; } = [];
}
```

---

## Scoring

```
age_hours  = (DateTime.UtcNow − timestamp).TotalHours
decay      = Math.Exp(−DecayRate × age_hours)
finalScore = chunk.Score × decay
```

Results are re-sorted by `finalScore` descending after scoring. `Score` on the returned `SearchResult` records reflects the combined value.

### Timestamp resolution order

1. `chunk.Metadata["created_at"]` — written by `MetadataBehavior` from `DocumentMetadata.CreatedAt`.
2. `FallbackMetadataKeys` — tried in order; first key present with a parseable ISO 8601 value wins.
3. No timestamp found (or parse failure) — treat age as 0, decay = 1.0 (no penalty).

---

## `TimeWeightedRetriever`

```csharp
public sealed class TimeWeightedRetriever : IRetriever
{
    public TimeWeightedRetriever(
        IRetriever inner,
        TimeWeightedOptions options,
        ILogger<TimeWeightedRetriever>? logger = null)
}
```

No `IEmbeddingGenerator` required — all timestamps are read from chunk metadata.

Algorithm:

```
TimeWeightedRetriever.RetrieveAsync(query, options):
  if !options.UseTimeWeighting: return inner.RetrieveAsync(query, options)
  results = await inner.RetrieveAsync(query, options)
  now = DateTime.UtcNow
  rescored = results
    .Select(r => r with { Score = r.Score × Decay(r.Chunk, now) })
    .OrderByDescending(r => r.Score)
    .ToList()
  return rescored
```

---

## DI Registration

```csharp
// Defaults (DecayRate = 0.01, no fallback keys)
services.AddRagNet(rag => rag.UseTimeWeighting());

// Custom decay rate
services.AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions
{
    DecayRate = 0.005,
    FallbackMetadataKeys = ["published_at", "event_date"],
}));
```

`UseTimeWeighting` registers `TimeWeightedOptions` as a sentinel.
`WireTimeWeighting` (called from `AddRagNet`) registers `TimeWeightedRetriever` and replaces `IRetriever`.

Per-call opt-out: `new RetrievalOptions { UseTimeWeighting = false }`.

---

## Decorator stacking

When multiple decorators are registered, the call order (outermost first) is:

```
TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever
```

`TagRetriever` narrows candidates via filter injection before retrieval. `TimeWeightedRetriever` re-scores the final result set before returning to the caller. `WireTimeWeighting` runs **before** `WireTagRetrieval` in `AddRagNet`; `WireTagRetrieval` is updated to resolve `TimeWeightedRetriever` (if present) as its inner, preferring it over `DeepResearchRetriever` / `PipelineRetriever`.

Call order in `AddRagNet`:
1. `WireRefinementStrategy`
2. `WireDeepResearch`
3. `WireTimeWeighting`
4. `WireTagRetrieval`

`WireTimeWeighting` inner-resolution logic:
- `hasDeepResearch` → wrap `DeepResearchRetriever`
- else → wrap `PipelineRetriever` (registers it as concrete type if not already done, same pattern as `WireDeepResearch` / `WireTagRetrieval`)

`WireTagRetrieval` inner-resolution logic (updated):
- `hasTimeWeighting` → wrap `TimeWeightedRetriever`
- else if `hasDeepResearch` → wrap `DeepResearchRetriever`
- else → wrap `PipelineRetriever`

---

## Error handling

| Condition | Behaviour |
|---|---|
| `"created_at"` missing + no fallback keys configured | Treat as age = 0, decay = 1.0 |
| Fallback key present but value not parseable as ISO 8601 | Skip that key, try next; if none succeed treat as age = 0 |
| `UseTimeWeighting = false` | Decorator skipped entirely, inner called directly |

---

## Files

**New:**
- `src/Rag.NET/Retrieval/TimeWeightedRetriever.cs`
- `src/Rag.NET/Models/Options/TimeWeightedOptions.cs`

**Modified:**
- `src/Rag.NET/Models/DocumentMetadata.cs` — add `CreatedAt` property
- `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs` — serialize `CreatedAt` into `chunk.Metadata["created_at"]`
- `src/Rag.NET/Models/Options/RetrievalOptions.cs` — add `UseTimeWeighting = true`
- `src/Rag.NET/DependencyInjection/RagBuilder.cs` — add `UseTimeWeighting()`
- `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` — add `WireTimeWeighting()`, update `WireTagRetrieval()`, update call order in `AddRagNet`

**New tests:**
- `tests/Rag.NET.Tests/Retrieval/TimeWeightedRetrieverTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UseTimeWeightingTests.cs`
- `tests/Rag.NET.Tests/Ingestion/MetadataBehaviorCreatedAtTests.cs`

**Docs:**
- `docs/reference/features.md` — mark ✅ Done
- `docs/guide/retrieval.md` — add `## Time-Weighted Retrieval` section

---

## Testing Plan

### `TimeWeightedRetriever`
1. Document with `CreatedAt` 10 hours ago → score multiplied by `e^(-0.01 × 10)` ≈ 0.905
2. Two results with different ages → oldest gets lower final score; results re-sorted correctly
3. `UseTimeWeighting = false` → inner called with original options, scores unchanged
4. `FallbackMetadataKeys = ["published_at", "event_date"]` — `published_at` present and parseable → used
5. `FallbackMetadataKeys` — first key absent, second key present → second key used
6. No timestamp anywhere → decay = 1.0, score unchanged
7. Invalid timestamp string → decay = 1.0, score unchanged (parse failure treated as no timestamp)

### `MetadataBehavior` (CreatedAt)
8. `DocumentMetadata.CreatedAt` serialised into `chunk.Metadata["created_at"]` in ISO 8601 format
9. Existing `"created_at"` tag in `DocumentMetadata.Tags` is preserved (TryAdd semantics — tag wins)

### DI
10. `UseTimeWeighting` → `IRetriever` is `TimeWeightedRetriever`
11. `UseTimeWeighting` + `UseTagRetrieval` → `IRetriever` is `TagRetriever` wrapping `TimeWeightedRetriever`
12. `UseTimeWeighting` + `UseTagRetrieval` + `UseDeepResearch` → `TagRetriever → TimeWeightedRetriever → DeepResearchRetriever`
