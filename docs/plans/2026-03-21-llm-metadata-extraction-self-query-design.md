# Design: LLM Metadata Extraction + Self-Query Filtering

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

Two complementary behaviors that form a complete metadata-aware RAG capability:

- **Ingest side:** `LlmMetadataExtractionBehavior` — calls an LLM to extract structured key-value tags from each chunk and stores them in `chunk.Metadata`.
- **Query side:** `SelfQueryBehavior` — calls an LLM to parse the user's question into a refined semantic query plus a structured metadata filter, then applies both to the retrieval context.

Together they allow the pipeline to automatically enrich documents with queryable metadata at index time, and to automatically narrow retrieval by that metadata at query time — without any manual tagging or filter construction by the caller.

---

## Architecture

Two new behaviors following the existing `IIngestionBehavior` / `IRetrievalBehavior` pattern.

### `LlmMetadataExtractionBehavior` (ingestion)

- Slots into the ingestion pipeline **after `ChunkingBehavior`, before `MetadataBehavior`**.
- For each chunk, calls `IChatClient` with a prompt that asks for a flat JSON object of string key-value pairs.
- Merges the extracted pairs into `chunk.Metadata`.
- Returns `Result<IReadOnlyDictionary<string, string>>` internally; on failure logs a warning and skips — ingestion never fails due to metadata extraction.
- Is always present in the pipeline; no-ops when `LlmMetadataExtractionOptions` is not registered.

### `SelfQueryBehavior` (retrieval)

- Slots **first in the retrieval pipeline**, before `VectorStoreBehavior`.
- Calls `IChatClient` with the user's question and asks for JSON: `{ "query": "...", "filters": [{"key": "...", "value": "..."}] }`.
- Sets `ctx.Options.EmbeddingTextOverride` to the refined query (reuses the existing HyDE mechanism).
- Builds a `HasTagSpec` chain from the returned filters and sets `ctx.Options.Filter`.
- Returns `Result<SelfQueryOutput>` internally; on failure logs a warning and proceeds with the original query and no filter.
- Is always present in the pipeline; no-ops when `SelfQueryOptions` is not registered.
- Per-call opt-out: `new RetrievalOptions { UseSelfQuery = false }`.

### Shared model

```csharp
public sealed record AttributeInfo(string Name, string Description);
```

Passed as an optional `IReadOnlyList<AttributeInfo>?` schema to either or both behaviors. When `null`, the LLM extracts/filters freely. When provided, the prompt constrains the LLM to only the listed fields.

---

## Data Flow

### Ingestion

```
Parse → Chunk → [LlmMetadataExtraction] → Metadata → Embed → Store
```

**Prompt (schema-free):**
> Extract metadata from this text as a flat JSON object. Keys must be lowercase snake_case strings. Values must be strings. Return `{}` if nothing useful can be extracted.

**Prompt (schema-guided):**
> Extract metadata from this text. Return a JSON object using only these fields: `[{name, description}, ...]`. Omit fields not present in the text. Return `{}` if nothing applies.

### Retrieval

```
[SelfQuery] → HyDE → MultiQuery → VectorStore → Filter → Rerank → ...
```

**Prompt (schema-free):**
> Parse this question into a search query and metadata filters. Return JSON: `{ "query": "...", "filters": [{"key": "...", "value": "..."}] }`. Filters may be an empty array.

**Prompt (schema-guided):**
> Parse this question. Available metadata fields: `[{name, description}, ...]`. Return JSON: `{ "query": "...", "filters": [{"key": "...", "value": "..."}] }`. Only include filters for the listed fields. Filters may be an empty array.

---

## Error Handling

Both behaviors use `Result<T>` (from `ZeroAlloc.Results`) for their internal LLM call + JSON parse step:

```csharp
// extraction
Result<IReadOnlyDictionary<string, string>> ExtractAsync(string chunkText, CancellationToken ct)

// self-query
Result<SelfQueryOutput> ParseAsync(string question, CancellationToken ct)
```

`.Match()` is used at the call site:
- **Success:** apply tags / set filter + query override
- **Failure:** log warning at `Warning` level, continue without modification

Ingestion and retrieval are never aborted due to LLM or parse errors in these behaviors.

---

## DI Registration

```csharp
// Schema-free
rag.UseLlmMetadataExtraction();
rag.UseSelfQuery();

// Schema-guided (recommended for production)
var schema = new[]
{
    new AttributeInfo("topic",    "Main subject area of the document"),
    new AttributeInfo("year",     "Publication or reference year, e.g. 2024"),
    new AttributeInfo("language", "Programming language discussed, if applicable"),
};

rag.UseLlmMetadataExtraction(schema: schema);
rag.UseSelfQuery(schema: schema);
```

Both methods register their options singleton. The behaviors are always in the pipeline and self-disable when their options are absent — consistent with `ParentDocumentIngestionBehavior` and `HydeBehavior`.

`RetrievalOptions` gains:
```csharp
public bool UseSelfQuery { get; init; } = true;
```

---

## Testing

### `LlmMetadataExtractionBehavior`

| Scenario | Expected |
|----------|----------|
| LLM returns valid JSON | Tags merged into `chunk.Metadata` |
| Schema-guided: unknown keys in response | Unknown keys ignored |
| LLM returns invalid JSON | `Result` failure → warning logged, chunk metadata unchanged |
| LLM returns `{}` | No tags added, ingestion continues |
| `LlmMetadataExtractionOptions` not registered | Behavior is a no-op |

### `SelfQueryBehavior`

| Scenario | Expected |
|----------|----------|
| LLM returns valid query + filters | `EmbeddingTextOverride` set, `HasTagSpec` filter composed |
| LLM returns empty filters | No filter set, refined query applied |
| LLM returns invalid JSON | `Result` failure → warning logged, original query used, no filter |
| `UseSelfQuery = false` on `RetrievalOptions` | Behavior skipped |
| `SelfQueryOptions` not registered | Behavior is a no-op |

---

## Out of Scope

- Q&A pair generation (different storage implications — separate feature)
- Numeric / boolean attribute types in `AttributeInfo` (YAGNI — string covers the majority of filter use cases)
- Push-down of filters into vector store queries (post-retrieval filtering via `FilterBehavior` is sufficient for now)
