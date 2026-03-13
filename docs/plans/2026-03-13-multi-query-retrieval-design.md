# Multi-Query Retrieval Design

**Goal:** Improve retrieval recall by generating multiple semantic phrasings of the user's query, running each against the vector store in parallel, and merging deduplicated results.

**Architecture:** New `IQueryExpander` abstraction registered in DI; `RagPipeline.RetrieveAsync` resolves it optionally and fans out to the vector store per variant.

**Tech Stack:** `IChatClient` (already in pipeline), `Microsoft.Extensions.AI`, no new NuGet dependencies.

---

## Components

### `IQueryExpander`

```csharp
public interface IQueryExpander
{
    Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}
```

### `LlmQueryExpander`

Default implementation. Resolves `IChatClient` from DI. Sends a prompt requesting `count` alternative phrasings, splits the response by newline, trims, and discards empty lines.

### `MultiQueryOptions`

```csharp
public sealed class MultiQueryOptions
{
    public int VariantCount { get; set; } = 3;
    public string PromptTemplate { get; set; } =
        "Generate {count} different phrasings of the following question.\n" +
        "Return only the rephrased questions, one per line, with no numbering or extra text.\n\n" +
        "Question: {query}";
}
```

---

## Data Flow

1. `RetrieveAsync` is called with a query and `RetrievalOptions`
2. If `IQueryExpander` is registered and `RetrievalOptions.UseMultiQuery != false`:
   - Call `ExpandAsync(query, VariantCount)` → N variant strings
   - Build query list: `[original] + variants` (original always included)
   - Fan out with `Task.WhenAll` — each variant runs a full vector store search
3. Merge all result lists: deduplicate by `ChunkId`, keep highest `Score`
4. Trim to `TopK` and return
5. If `ExpandAsync` throws: log warning, fall back to single-query on original

---

## Configuration

```csharp
// Default: 3 variants, built-in prompt
rag.UseMultiQueryRetrieval();

// Custom
rag.UseMultiQueryRetrieval(o =>
{
    o.VariantCount = 5;
    o.PromptTemplate = "Generate {count} alternative questions for: {query}";
});
```

`IChatClient` must be registered before calling `UseMultiQueryRetrieval()`. If missing, `InvalidOperationException` is thrown at startup.

Per-call opt-out:

```csharp
new RetrievalOptions { UseMultiQuery = false }
```

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| `IChatClient` not registered | `InvalidOperationException` at startup |
| `ExpandAsync` throws at runtime | Log warning, fall back to single-query |
| LLM returns fewer variants than requested | Proceed with whatever was returned |
| `UseMultiQuery = false` | Skip expansion entirely |

---

## Testing

### Unit Tests (`tests/Rag.NET.Tests`)

- `LlmQueryExpander` parses LLM response into correct number of variants
- `LlmQueryExpander` handles fewer lines than requested gracefully
- `RagPipeline.RetrieveAsync` deduplicates — same chunk from two variants keeps highest score
- `RagPipeline.RetrieveAsync` respects `UseMultiQuery = false`
- `RagPipeline.RetrieveAsync` falls back to single-query when `ExpandAsync` throws
- `RagPipeline.RetrieveAsync` always includes the original query in the fan-out

All mocked via NSubstitute (`IChatClient`, `IVectorStore`).

### Integration Tests (`tests/Rag.NET.Integration.Tests`)

- Ingest 3 documents, retrieve with multi-query enabled, assert broader recall than single-query on an ambiguous question
