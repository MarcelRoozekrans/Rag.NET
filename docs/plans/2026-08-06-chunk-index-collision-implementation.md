# Chunk Index Collision Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `ChunkIndex` unique within a document, as its own documentation already requires, so chunks stop overwriting each other on write and being merged on read.

**Architecture:** A running counter in `ParseBehavior.ChunkPerSectionAsync` renumbers each chunk as it is added. The fix is one line; the tests that should have existed are the bulk of the work.

**Tech Stack:** .NET 10, xUnit v3.

**Design:** `docs/plans/2026-08-06-chunk-index-collision-design.md`

---

## The defect in one paragraph

`ParseBehavior.ChunkPerSectionAsync` calls `ChunkingStrategy.ChunkAsync(section, …)` once per section. Every strategy assigns `ChunkIndex = chunkIndex++` from a counter local to that call (`RecursiveChunkingStrategy.cs:57`, `FixedSizeChunkingStrategy.cs:48`), so indices restart at 0 per section. **Nothing else in `src/` assigns `ChunkIndex`.** `RecursiveChunkingStrategy` — the default — implements `IChunkingStrategy`, so this is the default path.

`(DocumentId, ChunkIndex)` is an identity key in seven places: `DeterministicChunkId` (Qdrant `:147`, Weaviate `:223`), `MultiQueryBehavior:44`, `RrfMerger:57`, `DeepResearchRetriever:79`, `FederatedVectorStore:181`, `ParentChunkKeyHelper`, and `RagPipelineReindexExtensions:192`.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`.** MA0051 (≤60-line methods), MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- A file watcher edits `.csproj` concurrently — `git status` before committing.

**Baselines:** `Rag.NET.Tests` **1172**, `RepoConventions` **42 + 1 skip**.

**Test home:** `tests/Rag.NET.Tests/Ingestion/Behaviors/`. Note `PipelineIngestorChunkingValidationTests.cs` already exists — **read it first and report why existing chunking validation did not catch this.** That answer belongs in the close.

---

## Task 1: The failing test — and it must fail for the right reason

**Files:** create `tests/Rag.NET.Tests/Ingestion/Behaviors/ParseBehaviorChunkIndexTests.cs`

**Step 1: Write it.** Drive `ParseBehavior` with a parser yielding **two sections**, each producing multiple chunks, using the default `RecursiveChunkingStrategy`. Assert every `ChunkIndex` in `ctx.Chunks` is distinct.

```csharp
[Fact]
public async Task ChunkPerSection_MultiSectionDocument_ProducesDistinctChunkIndices()
{
    // ChunkIndex must be unique within a document — TextChunk.ChunkIndex says so, and
    // DeterministicChunkId, every dedup path and parent-chunk lookup all key on
    // (DocumentId, ChunkIndex). Before this fix the per-section counter restarted at 0.
    var chunks = await RunParseBehaviourAsync(/* two sections, several chunks each */);

    Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkIndex).Distinct().Count());
}
```

**Step 2: Run it and confirm it FAILS.** Report the **actual** message. It must fail because indices repeat — **not** because the harness is wrong. If sections produce one chunk each, indices won't collide and the test passes vacuously: **make each section produce at least two chunks**, and say how you ensured that.

**Step 3:** Commit the failing test, or hold it and commit with Task 3 — say which.

---

## Task 2: Verify the other branch — do not assume

`ChunkDocumentAsync` (`ParseBehavior.cs:71-80`) passes **all** sections to `docStrategy.ChunkDocumentAsync(...)` in **one** call, so a strategy can number globally. **Probably correct — confirm it.**

Implementers to check: `SemanticChunkingStrategy`, `HierarchicalMergerChunkingStrategy`, `LateChunkingStrategy`, `PropositionChunkingStrategy`, the five `Chunking.Templates` strategies, and `Image`/`VideoChunkingStrategy`.

**Write a test pinning this branch too**, using a real `IDocumentChunkingStrategy`, asserting distinct indices across a multi-section document.

**If any implementer restarts its counter per section, it has the same defect** — report it before fixing, because the impact statement changes and the fix may need to be elsewhere.

---

## Task 3: The fix

**Files:** `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs` (`ChunkPerSectionAsync`, ~82-101)

Maintain a running counter and renumber as each chunk is added:

```csharp
ctx.Chunks.Add(chunk with { ChunkIndex = documentChunkIndex++ });
```

`TextChunk` is a `sealed record`, so `with` works. **Nothing sorts by `ChunkIndex`** (verified), so renumbering has no ordering consequences.

**Do not change any chunking strategy.** A strategy receives one section and cannot know its offset within the document; there are many, including user-written ones implementing the public `IChunkingStrategy`. The fix belongs where the sections are joined.

**Step: Task 1's test now passes.** Run the full `Rag.NET.Tests` suite — **if anything else fails, report it rather than adjusting it.** A test that depended on per-section numbering would be encoding the defect.

---

## Task 4: Pin the consumers the collision corrupted

One test each, minimum:

**Write-time — `DeterministicChunkId`.** Two chunks from different sections of the same document must derive **distinct** GUIDs. This is the one that caused Qdrant and Weaviate to overwrite.

**Read-time — a dedup path.** `RrfMerger` or `MultiQueryBehavior`: two genuinely different chunks from different sections must **not** be merged. Before the fix they shared `(DocumentId, ChunkIndex)` and one was discarded.

These are the tests that make the defect impossible to reintroduce silently. Without them, a future change to `ParseBehavior` restores it and only an integration test against a real store would notice.

---

## Task 5: Close

Update `docs/planning/ROADMAP.md` (and `MILESTONE.md` if the house form requires).

**Record:**

- **How it was found** — while documenting `IChunkingStrategy.ChunkAsync`, by reading call sites to verify a draft summary. Not by a test.
- **That the contract was already written** — `TextChunk.ChunkIndex` says "must be unique within a document" — and nothing enforced it.
- **Why no test caught it**: no test covered multi-section chunking at all. Plus whatever `PipelineIngestorChunkingValidationTests` turns out to validate instead.
- **The blast radius**: seven identity-key sites, write-time overwrite *and* read-time merge.
- **Existing data**: chunk IDs change, so re-ingestion is needed — but that data was **already corrupt**. This recovers from data loss rather than causing it. State it plainly so the ID change is not misread.
- Task 2's verdict on the `IDocumentChunkingStrategy` branch.

**Do not tick a DoD box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
```

Plus any chunking-package suite touched.

**The deliverable is that a two-section Markdown document keeps all its chunks** — which no test in this repository previously checked.
