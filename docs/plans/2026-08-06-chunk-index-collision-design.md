# Chunk Index Collision — Design

**Date:** 2026-08-06
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. How this was found

Not by a test. It was found while **documenting `IChunkingStrategy.ChunkAsync`** during the XML
documentation phase: a draft summary claimed callers renumber `ChunkIndex` across sections, and
reading `ParseBehavior.ChunkPerSectionAsync` to verify that claim showed they do not.

The contract was already written down — `TextChunk.ChunkIndex`'s own documentation says it "must be
unique within a document." Nothing enforced it, and no test covered it.

## 1. The defect

`ParseBehavior.ChunkPerSectionAsync` calls `ChunkingStrategy.ChunkAsync(section, …)` **once per
section**. Every strategy assigns `ChunkIndex = chunkIndex++` from a counter local to that call —
`RecursiveChunkingStrategy.cs:57`, `FixedSizeChunkingStrategy.cs:48` — so indices **restart at 0
for each section**, and `ParseBehavior` appends them unchanged.

**Nothing anywhere else in `src/` assigns `ChunkIndex`.** Verified by grep.

**This is the default path.** `RecursiveChunkingStrategy` implements `IChunkingStrategy`, not
`IDocumentChunkingStrategy`, so `ParseBehavior` takes the `else` branch into
`ChunkPerSectionAsync`. Any multi-section document — any Markdown or PDF with headings — is
affected.

## 2. Blast radius — wider than the IDs

`(DocumentId, ChunkIndex)` is an identity key in seven places:

| Site | Consequence of a collision |
|---|---|
| `DeterministicChunkId.Derive(documentId, chunkIndex)` | Qdrant (`:147`) and Weaviate (`:223`) derive point IDs — **one chunk overwrites another at write time** |
| `MultiQueryBehavior.cs:44` | `GroupBy` dedup — **unrelated chunks merged at read time** |
| `RrfMerger.cs:57` | reciprocal-rank-fusion dedup — same |
| `DeepResearchRetriever.cs:79` | dedup key — same |
| `FederatedVectorStore.cs:181` | cross-store dedup — same |
| `ParentChunkKeyHelper` (`{documentId}:{parentChunkIndex}`) | **wrong parent returned** for parent-document retrieval |
| `RagPipelineReindexExtensions.cs:192` | its own comment: *"StoreAsync replaces by (DocumentId, ChunkIndex)"* |

So this is not only silent data loss on write. It also **merges unrelated chunks on read**, in
every deduplication path the library has.

## 3. The fix

A running counter in `ChunkPerSectionAsync`, applied as each chunk is added:

```csharp
ctx.Chunks.Add(chunk with { ChunkIndex = documentChunkIndex++ });
```

`TextChunk` is a `sealed record`, so `with` is available. **Nothing sorts by `ChunkIndex`**
(verified), so renumbering has no ordering consequences.

**The fix belongs in `ParseBehavior`, not in the strategies.** A strategy receives one section and
cannot know its offset within the document. There are many strategies, including user-written ones
implementing the public `IChunkingStrategy` — fixing it per-strategy would be both wrong and
unenforceable.

## 4. What must be verified rather than assumed

**The `IDocumentChunkingStrategy` path (`ChunkDocumentAsync`) is probably already correct** — it
chunks a whole document in one call, so its indices are likely global. **Confirm it; do not assume
it**, and pin it with a test either way. If it turns out to share the defect, the fix is the same
shape but the impact statement changes.

## 5. Testing — the gap is the story

**No test anywhere covers multi-section chunking.** That is exactly why a written contract went
unenforced, and it is the most important thing to fix alongside the code.

- **A failing test first**: a two-section document through the default chunker, asserting
  `ChunkIndex` values are distinct across the whole document. It must fail before the fix.
- **A test per corrupted identity consumer** — at minimum `DeterministicChunkId` producing distinct
  GUIDs, and one dedup path (`RrfMerger` or `MultiQueryBehavior`) not merging unrelated chunks.
- **The `IDocumentChunkingStrategy` branch** covered too, so both paths are pinned and a future
  change cannot silently reintroduce this on either.

## 6. Existing data

Chunk IDs change after this fix, so previously-ingested documents need re-ingestion.

**That data was already corrupt.** Colliding IDs meant chunks were overwriting each other at write
time and being merged at read time. This recovers from data loss rather than causing it — worth
stating plainly in the close, so the ID change is not read as a breaking change with no upside.

## 7. Out of scope

Whether `ChunkIndex` should be a stronger identity type, and whether `DeterministicChunkId` should
incorporate section identity rather than relying on a caller-maintained counter. The fix restores
the documented contract; redesigning chunk identity is separate work with its own measurements.
