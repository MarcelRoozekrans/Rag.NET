# Cross-Encoder Reranking Design

**Date:** 2026-03-14

## Goal

After initial vector search retrieves candidate documents, a cross-encoder model rescores each (query, passage) pair for significantly higher precision. Cross-encoders jointly attend to query and passage tokens — unlike bi-encoders used for embeddings — producing much more accurate relevance scores at the cost of per-pair inference.

## Architecture

Public `IReranker` abstraction in core, with a separate `Rag.NET.Reranking.Onnx` package providing an ONNX Runtime implementation. Users can plug in remote APIs (Cohere, Jina) via the interface.

### New types

| Type | Location | Package | Visibility |
|------|----------|---------|------------|
| `IReranker` | `src/Rag.NET/Abstractions/` | `Rag.NET` | `public` |
| `RerankResult` | `src/Rag.NET/Models/` | `Rag.NET` | `public` |
| `RerankingOptions` | `src/Rag.NET/Models/Options/` | `Rag.NET` | `public` |
| `OnnxReranker` | `src/Rag.NET.Reranking.Onnx/` | `Rag.NET.Reranking.Onnx` | `public` |
| `OnnxRerankerOptions` | `src/Rag.NET.Reranking.Onnx/` | `Rag.NET.Reranking.Onnx` | `public` |

### Modified types

| Type | Change |
|------|--------|
| `RagPipeline` | New optional constructor param `IReranker?`; reranking applied after redundancy filter, before lost-in-the-middle reorderer |
| `RetrievalOptions` | New `bool UseReranking { get; set; } = true` and `int? CandidateCount { get; set; }` |
| `RagBuilder` | New `UseReranking<T>()` generic method for custom implementations |
| `ServiceCollectionExtensions` | Resolve `IReranker?` via `GetService<>()` and pass to `RagPipeline` |
| `RagPipelineLog` | New `RerankingCompleted` (Debug) and `RerankingFailed` (Warning) log entries |

## Interfaces & Options

```csharp
public interface IReranker
{
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default);
}

public sealed class RerankResult
{
    public required SearchResult SearchResult { get; init; }
    public required double RelevanceScore { get; init; }
}

public sealed class RerankingOptions
{
    // No properties needed yet — TopK and CandidateCount live on RetrievalOptions
}
```

`RetrievalOptions` additions:

```csharp
public sealed class RetrievalOptions
{
    // ... existing properties ...

    /// <summary>
    /// Number of candidates to fetch from vector search before reranking.
    /// When a reranker is registered and this is null, defaults to TopK * 3.
    /// Ignored when no reranker is registered.
    /// </summary>
    public int? CandidateCount { get; set; }

    /// <summary>
    /// Whether to apply cross-encoder reranking. Requires an IReranker to be registered.
    /// </summary>
    public bool UseReranking { get; set; } = true;
}
```

`OnnxRerankerOptions`:

```csharp
public sealed class OnnxRerankerOptions
{
    /// <summary>
    /// Path to the ONNX cross-encoder model file.
    /// </summary>
    public required string ModelPath { get; set; }

    /// <summary>
    /// Maximum token sequence length for the cross-encoder input.
    /// Query + passage pairs exceeding this are truncated.
    /// </summary>
    public int MaxLength { get; set; } = 512;
}
```

## Data Flow

Reranking runs after redundancy filter and before lost-in-the-middle reorderer:

```
RetrieveAsync(query, options)
  │
  ├─ [if multi-query] expand → [query, v1, v2, ...]
  │
  └─ per query string → SearchSingleQueryAsync(q, ..., useHyde)
        │
        ├─ [if HyDE active] generate hypothetical doc
        ├─ Embed(textToEmbed)
        ├─ VectorStore.SearchAsync(embedding, CandidateCount or TopK)
        └─ [if hybrid] BM25 merge
  │
  ├─ deduplicate across queries
  ├─ RedundancyFilter (cheap, cosine similarity on existing embeddings)
  │
  ├─ [if reranker registered && UseReranking]
  │     IReranker.RerankAsync(query, filteredResults)        ← NEW
  │       → score each (query, chunk.Text) pair
  │       → sort by relevance score descending
  │       → return top TopK results
  │     (on failure: log warning, return results in original order)
  │
  └─ LostInTheMiddleReorderer (presentation ordering)
  │
  └─ final results
```

Key behaviors:
- When no `IReranker` is registered, `CandidateCount` is ignored and `TopK` controls vector search directly — zero behavior change for existing users.
- When reranker is registered, vector search uses `CandidateCount` (defaults to `TopK * 3` if null), reranker trims to `TopK`.
- `UseReranking = false` skips the reranker for that call, vector search falls back to `TopK`.

## Pipeline Integration — `RetrieveAsync` changes

```csharp
// After redundancy filter, before lost-in-the-middle:
if (useReranking && _reranker is not null)
{
    try
    {
        var reranked = await _reranker.RerankAsync(query, results, cancellationToken)
            .ConfigureAwait(false);

        results = reranked
            .OrderByDescending(r => r.RelevanceScore)
            .Take(topK)
            .Select(r => r.SearchResult)
            .ToList();

        RagPipelineLog.RerankingCompleted(_logger, query, results.Count, stopwatch.Elapsed);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        RagPipelineLog.RerankingFailed(_logger, query, ex);
        // results remain in original order (fallback)
    }
}
```

## Vector Search CandidateCount Logic

In `SearchSingleQueryAsync`, the number of results to fetch from vector search:

```csharp
var searchCount = (_reranker is not null && useReranking)
    ? (options.CandidateCount ?? options.TopK * 3)
    : options.TopK;
```

## DI Registration

```csharp
// ONNX reranker (separate package)
services.AddRagNet(b => b
    .UseOnnxReranking(o => {
        o.ModelPath = "models/ms-marco-MiniLM-L-6-v2.onnx";
        o.MaxLength = 512;
    })
);

// Custom reranker implementation
services.AddRagNet(b => b
    .UseReranking<MyCohereReranker>()
);

// Per-call opt-out
var results = await pipeline.RetrieveAsync("query", new RetrievalOptions
{
    UseReranking = false
});

// Over-fetch 30 candidates, return best 5
var results = await pipeline.RetrieveAsync("query", new RetrievalOptions
{
    CandidateCount = 30,
    TopK = 5
});
```

## ONNX Implementation Detail

`OnnxReranker` uses `Microsoft.ML.OnnxRuntime` to run inference on a cross-encoder model:

1. Tokenize `(query, passage)` pair using the model's tokenizer
2. Truncate to `MaxLength` tokens
3. Run ONNX inference → raw logit score
4. Apply sigmoid to get `[0, 1]` relevance score
5. Return `RerankResult` for each pair

The implementation is model-agnostic — any ONNX cross-encoder that accepts `input_ids`, `attention_mask`, `token_type_ids` and outputs a single logit will work.

### Recommended Models

| Model | Languages | Size | Use Case |
|-------|-----------|------|----------|
| `ms-marco-MiniLM-L-6-v2` | English | ~80MB | Fast, good accuracy for English-only |
| `bge-reranker-v2-m3` | 100+ languages | ~568MB | Multilingual, strong accuracy |

## Error Handling

- `OperationCanceledException`: re-thrown immediately (cooperative cancellation).
- All other exceptions: caught, `RerankingFailed` logged at `Warning` level, falls back to results in original vector-search order. No exception propagates to the caller.
- Invalid `ModelPath` in `OnnxRerankerOptions`: throws `FileNotFoundException` at DI registration time (fail fast).

## Testing

### `RagPipelineTests` reranking additions (6 tests)

1. `RetrieveAsync_WhenRerankerRegistered_UsesRerankerResults` — results are reordered by reranker scores.
2. `RetrieveAsync_WhenUseRerankingFalse_SkipsReranker` — `_reranker.DidNotReceive().RerankAsync(...)`.
3. `RetrieveAsync_WhenRerankerThrows_FallsBackToOriginalOrder` — reranker throws → results returned in vector search order.
4. `RetrieveAsync_WhenRerankerRegistered_UsesCandidateCount` — vector search receives `CandidateCount`, final results trimmed to `TopK`.
5. `RetrieveAsync_WhenNoReranker_CandidateCountIgnored` — vector search receives `TopK` directly.
6. `RetrieveAsync_WhenRerankerAndMultiQueryAndHyde_AllCompose` — all three features work together.

### `OnnxRerankerTests` (4 tests)

1. `RerankAsync_ScoresAndSortsResults` — verifies scoring with a test model.
2. `RerankAsync_RespectsMaxLengthTruncation` — long passages truncated without error.
3. `RerankAsync_WhenQueryIsNull_ThrowsArgumentNullException`.
4. `Constructor_WhenModelPathInvalid_ThrowsFileNotFoundException`.

## Files Touched

**New package — `Rag.NET.Reranking.Onnx`:**
- `src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj`
- `src/Rag.NET.Reranking.Onnx/OnnxReranker.cs`
- `src/Rag.NET.Reranking.Onnx/OnnxRerankerOptions.cs`
- `src/Rag.NET.Reranking.Onnx/RagBuilderExtensions.cs`
- `tests/Rag.NET.Reranking.Onnx.Tests/Rag.NET.Reranking.Onnx.Tests.csproj`
- `tests/Rag.NET.Reranking.Onnx.Tests/OnnxRerankerTests.cs`

**Create in core:**
- `src/Rag.NET/Abstractions/IReranker.cs`
- `src/Rag.NET/Models/RerankResult.cs`
- `src/Rag.NET/Models/Options/RerankingOptions.cs`

**Modify:**
- `src/Rag.NET/Pipeline/RagPipeline.cs`
- `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/Rag.NET/Logging/RagPipelineLog.cs`
- `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`
- `docs/features.md`
- `Rag.NET.sln`
