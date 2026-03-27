# Semantic Chunking — Document-Level + Refinement Strategy Design

**Date:** 2026-03-27
**Status:** Approved

---

## Goal

Extend `SemanticChunkingStrategy` with two new capabilities:

1. **Document-level chunking** (`IDocumentChunkingStrategy`) — merge adjacent sections that are semantically similar, break where similarity drops. Complements the existing within-section sentence-level path.
2. **Chunk refinement** (`IChunkRefinementStrategy`) — a new pipeline extension point that post-processes the output of any chunking strategy. `SemanticChunkingStrategy` implements it to sub-split oversized chunks at sentence boundaries.

---

## New Interface

```csharp
/// <summary>
/// Post-processes chunks produced by any chunking strategy.
/// Applied by ParseBehavior after the chunking step (both per-section and document-level paths).
/// </summary>
public interface IChunkRefinementStrategy
{
    IAsyncEnumerable<TextChunk> RefineAsync(
        IAsyncEnumerable<TextChunk> chunks,
        CancellationToken cancellationToken = default);
}
```

Registered in DI as a singleton. `ParseBehavior` resolves it via a nullable `[Inject]` property and skips the refinement pass when absent.

---

## `SemanticChunkingStrategy` — Three Interfaces

| Interface | Mode | Input | Algorithm |
|---|---|---|---|
| `IChunkingStrategy` | Per-section (existing) | `DocumentSection` | Split into sentences → embed → breakpoint detect → merge/split groups |
| `IDocumentChunkingStrategy` | Document-level (new) | `IAsyncEnumerable<DocumentSection>` | Batch-embed section texts → breakpoint detect → merge sections into chunks |
| `IChunkRefinementStrategy` | Refinement (new) | `IAsyncEnumerable<TextChunk>` | Re-split oversized chunks via sentence-level path; pass through the rest |

All three modes share `CosineSimilarity`, `SplitSentences`, `MergeUndersizedGroups`, `SplitOversizedGroups`, and `SemanticChunkingOptions`. No new options are needed.

---

## Document-Level Algorithm (`ChunkDocumentAsync`)

1. Buffer all sections from the stream
2. Batch-embed section texts in one `GenerateAsync` call (`activeEmbedder`)
3. Compute consecutive cosine similarities between adjacent section embeddings
4. Percentile breakpoint detection (same `BreakpointPercentile` as sentence path)
5. Group sections at breakpoints — adjacent sections merge into one group
6. `MergeUndersizedGroups` / `SplitOversizedGroups` using existing helpers
7. Join section texts within each group → emit `TextChunk`

---

## Refinement Algorithm (`RefineAsync`)

For each incoming `TextChunk`:
- If `Text.Length > MinChunkSize`: treat text as a `DocumentSection`, run sentence-level `ChunkAsync`, re-emit sub-chunks (preserving `DocumentId`, incrementing `ChunkIndex`)
- Otherwise: pass through unchanged

---

## `ParseBehavior` Integration

```
parser.ParseAsync()
  → IAsyncEnumerable<DocumentSection>
  → chunking:
      if IDocumentChunkingStrategy  → ChunkDocumentAsync(sections)
      else per-section              → ChunkAsync(section) × N
  → if IChunkRefinementStrategy registered
      → RefineAsync(chunks)
  → ctx.Chunks
```

`IChunkRefinementStrategy` is resolved as an optional `[Inject]` property (nullable). When null, the refinement pass is skipped. No breaking change to existing behaviour.

---

## `RagBuilder` Changes

| Method | Registers |
|---|---|
| `UseSemanticChunking()` (updated) | `IChunkingStrategy`, `IDocumentChunkingStrategy`, `IChunkRefinementStrategy` — same instance |
| `UseSemanticRefinement()` (new) | Only `IChunkRefinementStrategy` |

Usage patterns:

```csharp
// Standalone: semantic splitting from raw sections
services.AddRagNet(rag => rag.UseSemanticChunking());

// Composed: hierarchical structure first, semantic refinement after
services.AddRagNet(rag => rag
    .UseHierarchicalMerging()
    .UseSemanticRefinement());

// Full semantic: document-level split + per-chunk refinement
services.AddRagNet(rag => rag.UseSemanticChunking());
// (IChunkRefinementStrategy is also registered, so refinement runs automatically)
```

---

## Testing Plan

### `SemanticChunkingStrategy` — `ChunkDocumentAsync`
1. Sections with similar embeddings merge into one chunk
2. Sections with low similarity produce separate chunks
3. `MinChunkSize` constraint merges undersized groups
4. `MaxChunkSize` constraint splits oversized groups
5. Empty section stream produces no chunks

### `SemanticChunkingStrategy` — `RefineAsync`
6. Chunk longer than `MinChunkSize` is sub-split at sentence boundaries
7. Chunk shorter than `MinChunkSize` passes through unchanged
8. Empty input produces no output

### `ParseBehavior` integration
9. `RefineAsync` is called after chunking when `IChunkRefinementStrategy` is registered
10. Refinement is skipped when no `IChunkRefinementStrategy` is registered

### `RagBuilder` DI
11. `UseSemanticChunking` — all three interfaces resolve to the same instance
12. `UseSemanticRefinement` — only `IChunkRefinementStrategy` is registered; `IChunkingStrategy` and `IDocumentChunkingStrategy` are not

---

## Files

**New:**
- `src/Rag.NET/Abstractions/IChunkRefinementStrategy.cs`

**Modified:**
- `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs` — add `IDocumentChunkingStrategy` + `IChunkRefinementStrategy`
- `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs` — add refinement pass
- `src/Rag.NET/DependencyInjection/RagBuilder.cs` — update `UseSemanticChunking`, add `UseSemanticRefinement`

**New tests:**
- `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyDocumentTests.cs`
- `tests/Rag.NET.Tests/Chunking/SemanticRefinementStrategyTests.cs`
- `tests/Rag.NET.Tests/Ingestion/Behaviors/ParseBehaviorRefinementTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UseSemanticChunkingTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UseSemanticRefinementTests.cs`
