# Design: Semantic Chunking (Embedding-Based Boundary Detection)

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

New `SemanticChunkingStrategy` implementing `IChunkingStrategy` that splits text at meaning boundaries rather than fixed sizes. Sentences are embedded, cosine similarity is computed between consecutive pairs, and breaks occur where similarity drops below a percentile threshold. Produces chunks that are coherent units of meaning — no more splitting mid-thought.

---

## Architecture

### `SemanticChunkingOptions`

```csharp
public sealed class SemanticChunkingOptions
{
    public float BreakpointPercentile { get; init; } = 0.25f;
    public int MinChunkSize { get; init; } = 100;
    public int MaxChunkSize { get; init; } = 1500;
    public IEmbeddingGenerator<string, Embedding<float>>? ChunkingEmbedder { get; init; }
}
```

- `BreakpointPercentile`: similarities in the bottom N percentile trigger a break. Lower = fewer breaks (larger chunks), higher = more breaks (smaller chunks). Clamped to `(0, 1)` at runtime.
- `MinChunkSize` / `MaxChunkSize`: character-based constraints. Enforced after breakpoint grouping.
- `ChunkingEmbedder`: optional override. When null (default), uses the same `IEmbeddingGenerator` registered for retrieval. Set this when you want a smaller/faster model for chunking (e.g., MiniLM) while keeping a larger model for retrieval quality.
- Validation: `MinChunkSize < MaxChunkSize` enforced.

### `SemanticChunkingStrategy`

Implements `IChunkingStrategy`. Injected with `IEmbeddingGenerator` from DI and `SemanticChunkingOptions`.

**Algorithm:**

1. **Sentence splitting** — split on `(?<=[.!?])\s+` with negative lookbehind for common abbreviations (`Mr.`, `Dr.`, `Mrs.`, `Ms.`, `Jr.`, `Sr.`, `vs.`, `etc.`, `e.g.`, `i.e.`).
2. **Single-sentence shortcut** — if only 1 sentence, return it as a single chunk. No embedding call needed.
3. **Embed all sentences** — single `GenerateAsync` call using `ChunkingEmbedder ?? injectedEmbedder`.
4. **Compute similarities** — cosine similarity between each consecutive pair → array of N-1 values.
5. **Find breakpoints** — sort a copy of similarities, find the value at `BreakpointPercentile * length` → threshold. Any pair below the threshold = chunk boundary.
6. **Group sentences** — sentences between breakpoints form a chunk. Concatenate with spaces.
7. **Enforce min/max constraints:**
   - Merge any chunk below `MinChunkSize` into its smaller neighbor (left or right, whichever is shorter).
   - After merging: if any chunk exceeds `MaxChunkSize`, split at the nearest sentence boundary within it.

### Registration

```csharp
rag.UseSemanticChunking();
// or with options:
rag.UseSemanticChunking(o =>
{
    o.BreakpointPercentile = 0.3f;
    o.MinChunkSize = 200;
    o.MaxChunkSize = 2000;
});
```

Adds `UseSemanticChunking` extension method to `RagBuilder`. Registers `SemanticChunkingStrategy` as `IChunkingStrategy` (replacing the default `RecursiveChunkingStrategy`) and `SemanticChunkingOptions` as a singleton.

### File Layout

```
src/Rag.NET/Chunking/SemanticChunkingStrategy.cs
src/Rag.NET/Models/Options/SemanticChunkingOptions.cs
src/Rag.NET/DependencyInjection/RagBuilder.cs  (add UseSemanticChunking method)
tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs
```

---

## Error Handling

- **Empty or whitespace text:** yield zero chunks.
- **Single sentence:** yield as one chunk, no embedding call.
- **`OperationCanceledException`:** re-thrown immediately.
- **Embedding call fails:** exception propagates to caller (same pattern as other strategies).

---

## Testing

| Scenario | Expected |
|---|---|
| Two clearly different topics | Break between them |
| Uniform similarity text | Few or no breaks (all above percentile) |
| Single sentence | One chunk, no embedding call |
| Empty text | Zero chunks |
| Chunk below `MinChunkSize` | Merged with neighbor |
| Chunk above `MaxChunkSize` | Split at sentence boundary |
| Custom `BreakpointPercentile = 0.5` | More breaks than default |
| Custom `ChunkingEmbedder` override | Uses provided embedder, not DI default |
| Abbreviations (`Dr. Smith`) | Not split mid-sentence |
| Cancellation token | `OperationCanceledException` propagates |

---

## Out of Scope

- Sliding window / overlap between semantic chunks (breakpoints are natural boundaries — overlap adds noise)
- Language-specific sentence splitters (the regex approach works for Latin-script languages)
- Caching sentence embeddings across re-ingestions
