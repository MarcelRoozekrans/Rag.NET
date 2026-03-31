# Cohere Rerank — Design

**Date:** 2026-03-31
**Status:** Approved

## Overview

Add `Rag.NET.Reranking.Cohere` — a new package that implements `IReranker` by calling the Cohere `/rerank` API via the official `Cohere.Net` NuGet package. Mirrors the `OnnxReranker` pattern: thin wrapper, options-in-constructor, registered via `UseCohereReranking()` on `RagBuilder`.

## Package Structure

**`Rag.NET.Reranking.Cohere`**

| File | Purpose |
|------|---------|
| `CohereRerankerOptions.cs` | Configuration — API key, model, TopN, batching, endpoint override |
| `CohereReranker.cs` | `IReranker` + `IDisposable` implementation |
| `RagBuilderExtensions.cs` | `UseCohereReranking(Action<CohereRerankerOptions>)` extension |

**`tests/Rag.NET.Reranking.Cohere.Tests`**

Unit tests using a stub HTTP server (via `Endpoint` override in options).

## Options

```csharp
public sealed class CohereRerankerOptions
{
    /// <summary>Cohere API key. Required.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Reranking model. Default: "rerank-english-v3.0".
    /// Switch to "rerank-v3.5" for multilingual workloads.
    /// </summary>
    public string Model { get; init; } = "rerank-english-v3.0";

    /// <summary>
    /// Number of top results to return. Default: 5.
    /// Cohere returns the top-N results sorted by relevance score.
    /// </summary>
    public int TopN { get; init; } = 5;

    /// <summary>
    /// Whether to ask Cohere to echo back document text in the response. Default: false.
    /// </summary>
    public bool ReturnDocuments { get; init; } = false;

    /// <summary>
    /// Maximum documents per API call. Cohere's limit is 1,000. Default: 1000.
    /// When results exceed this, calls are batched sequentially and merged.
    /// </summary>
    public int MaxDocumentsPerBatch { get; init; } = 1000;

    /// <summary>
    /// Optional API endpoint override. Useful for testing with a local stub server.
    /// When null, the Cohere SDK uses its default endpoint.
    /// </summary>
    public string? Endpoint { get; init; }
}
```

## Data Flow

1. Validate `query` and `results` (null checks, empty-list early return)
2. Partition `results` into batches of `MaxDocumentsPerBatch`
3. For each batch:
   - Map `SearchResult.Chunk.Text` → Cohere document strings (index-aligned)
   - Call `cohereClient.V2.RerankAsync(...)` with `Query`, `Documents`, `Model`, `TopN`, `ReturnDocuments`
   - Map each result: `RerankResult { SearchResult = results[result.Index], RelevanceScore = result.RelevanceScore }`
4. Merge all batches, sort descending by `RelevanceScore`, return

> **Note:** Cohere caps individual document text at ~10,000 tokens. If a passage exceeds this, the SDK throws. We do not truncate — callers should chunk aggressively before reranking. This is documented on `RerankAsync`.

## Registration

```csharp
rag.UseCohereReranking(o =>
{
    o.ApiKey = configuration["Cohere:ApiKey"]!;
    // o.Model = "rerank-v3.5"; // multilingual
    o.TopN  = 10;
});
```

Internally delegates to the existing `UseReranking<CohereReranker>()` pattern on `RagBuilder`.

## Error Handling

| Scenario | Behaviour |
|----------|-----------|
| `ApiKey` null/empty | `ArgumentException` in constructor |
| Empty `results` | Returns empty list immediately (no API call) |
| Cohere 4xx/5xx | SDK exception propagates as-is |
| Cancellation | `OperationCanceledException` from SDK |
| Document too long | SDK exception propagates; not wrapped |

## Testing

- **Empty input** — no API call, returns `[]`
- **Single result** — correct score mapping and sort
- **Multi-batch** — >1000 docs split, merged, and re-sorted correctly
- **Index mapping** — result `Index` maps back to correct `SearchResult`
- **`TopN` respected** — Cohere request carries the configured value
- **Cancellation** — token propagated and honoured
- **Bad API key** — constructor throws before any call

Tests point `Endpoint` at a local stub server — no real API key required.

## Dependencies

| Package | Version |
|---------|---------|
| `Cohere` | latest stable |
| `Rag.NET` | project reference |
