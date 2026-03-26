# Design: Hierarchical Merger (Regex-Driven Tree Chunking)

**Date:** 2026-03-26
**Status:** Approved

---

## Overview

A document-level chunking strategy that merges sections into heading-subtree chunks. Each chunk covers one heading and all body text under it down to a configurable depth. Sections deeper than `MaxDepth` are folded into their nearest in-scope ancestor. Works with any document format: uses `DocumentSection.HeadingLevel` when populated by the parser; falls back to user-supplied regex patterns for formats that don't emit heading metadata.

---

## Architecture

### New interface: `IDocumentChunkingStrategy`

The existing `IChunkingStrategy` is per-section and stateless — wrong abstraction for merging. A new interface takes the full section stream:

```csharp
public interface IDocumentChunkingStrategy
{
    IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
```

Located at `src/Rag.NET/Abstractions/IDocumentChunkingStrategy.cs`. This interface is also the right home for future document-level strategies (domain templates, RAPTOR pre-processing).

### `HierarchicalMergerChunkingStrategy`

Implements both `IDocumentChunkingStrategy` and `IChunkingStrategy` (the latter falls back to emitting each section as its own chunk, enabling standalone use outside the pipeline).

**Algorithm — streaming heading stack:**

1. Maintain a current entry: `(headingLevel, headingText, accumulatedBody)`
2. For each incoming `DocumentSection`:
   - Detect heading level: use `HeadingLevel` if set; otherwise match against `HeadingPatterns` in order
   - If heading at level `≤ MaxDepth` and `≤ current entry level` → flush current entry as a chunk, start new entry
   - If heading at level `> MaxDepth` → treat as body text, append to current entry
   - If no heading detected → append text to current entry
3. After stream ends → flush final entry

Memory: O(one chunk worth of text) — no full-document buffering.

**Chunk text format:**

```
[Heading text]

[All body text under this heading]
```

Text appearing before the first heading is emitted as a chunk with no heading prefix.

### `ChunkingBehavior` update

Single check added to the existing behavior:

```csharp
if (_strategy is IDocumentChunkingStrategy doc)
    chunks = doc.ChunkDocumentAsync(ctx.Sections, options, ct);
else
    chunks = ctx.Sections.SelectManyAwait(s => _strategy.ChunkAsync(s, options, ct));
```

No other pipeline changes required.

### Options

```csharp
public sealed class HierarchicalMergerOptions
{
    // Maximum heading depth to treat as chunk boundaries.
    // Sections at HeadingLevel > MaxDepth are merged into their nearest ancestor.
    public int MaxDepth { get; init; } = 2;

    // Per-level regex patterns used when DocumentSection.HeadingLevel is null.
    // HeadingPatterns[0] = level-1 patterns, HeadingPatterns[1] = level-2 patterns, etc.
    // null = rely on parser's HeadingLevel only.
    public string[][]? HeadingPatterns { get; init; }
}
```

### DI Registration

Added to `RagBuilder`:

```csharp
builder.UseHierarchicalMerging(new HierarchicalMergerOptions { MaxDepth = 2 });

// With regex fallback for plain-text/PDF documents:
builder.UseHierarchicalMerging(new HierarchicalMergerOptions
{
    MaxDepth = 2,
    HeadingPatterns = [["^# ", @"^={3,}"], ["^## ", @"^-{3,}"], ["^### "]]
});
```

Registers `HierarchicalMergerChunkingStrategy` as `IChunkingStrategy` (and implicitly as `IDocumentChunkingStrategy` via the interface check in `ChunkingBehavior`).

---

## File Layout

```
src/Rag.NET/
  Abstractions/IDocumentChunkingStrategy.cs              (new)
  Chunking/HierarchicalMergerChunkingStrategy.cs         (new)
  Models/Options/HierarchicalMergerOptions.cs            (new)
  Pipeline/Behaviors/ChunkingBehavior.cs                 (modified)
  DependencyInjection/RagBuilder.cs                      (modified)
tests/Rag.NET.Tests/
  Chunking/HierarchicalMergerChunkingStrategyTests.cs    (new)
```

---

## Error Handling

- Empty section stream → no chunks emitted.
- Section with null/empty text → appended as empty string, not skipped (preserves position metadata).
- Regex pattern compilation errors → throw `ArgumentException` at options validation time, not at chunk time.

---

## Testing

| Scenario | Expected |
|----------|----------|
| 3 H1 sections, MaxDepth=1 | 3 chunks, each = heading + body |
| H1 → H2 → H3, MaxDepth=2 | H1 chunk (body only) + H2 chunk (H2 body + H3 body merged) |
| Body text before first heading | Emitted as chunk with no heading prefix |
| HeadingLevel null, regex supplied | Regex detects headings correctly |
| HeadingLevel null, no regex | All sections merged into one chunk |
| Empty section stream | Zero chunks |
| MaxDepth=0 | All sections merged into one chunk |

---

## Out of Scope

- Nested chunk metadata (parent heading path) — could be added as `TextChunk.Metadata` later
- Auto-splitting oversized merged chunks — callers compose with `TokenAwareChunkingStrategy` for that
- Per-level chunk size limits
