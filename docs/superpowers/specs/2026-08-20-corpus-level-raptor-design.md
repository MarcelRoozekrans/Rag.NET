# Corpus-Level RAPTOR — design

**Date:** 2026-08-20
**Phase:** 6.2.3 — Corpus-Level RAPTOR (added 2026-08-20, gates v1.0)
**Issue:** #331. Also fixes #332.
**Status:** design approved in brainstorming; not yet planned

## Goal

Make `Rag.NET.Raptor` cluster across the corpus rather than within each document — the mechanism the
RAPTOR paper is about — while keeping the per-document scope selectable so the shipped behaviour
stays measurable rather than deleted.

## The defect

`RaptorIngestionBehavior` is an `IIngestionBehavior` that clusters `ctx.EmbeddedChunks`, and
`IngestionContext` carries exactly one `Stream` and one `DocumentMetadata`. It is one document's
chunks. The behaviour's own telemetry says so: `activity?.SetTag("document.id",
ctx.Metadata.DocumentId.Value)`.

The RAPTOR paper clusters across the collection. That is the technique's point — a top-level node
summarises themes spanning many documents. A per-document tree cannot produce a node spanning two
documents, so on any cross-document question its summaries can only displace.

**This is #300's shape**: a whole-corpus operation running once per ingested document.

`docs/guide/raptor.md` is not wrong about it — it describes the per-document behaviour accurately.
The gap is between the package's name and its mechanism.

### Why the fix is phase-sized

#302's debounce-plus-rebuilder pattern transfers in shape but not in substance.
`CommunityDetectionBehavior` can do whole-graph work because GraphRAG owns an `IGraphStore` that
enumerates itself. RAPTOR has no equivalent, and the vector store cannot stand in:

| Obstacle | Detail |
|---|---|
| No enumeration on `IVectorStore` | `StoreAsync`, `SearchAsync`, `DeleteByDocumentIdAsync`. Nothing returns the corpus. |
| `IChunkLookup` is by key | You would already need every chunk identity to ask for them. |
| `IChunkLookup` returns `TextChunk` | RAPTOR clusters on **vectors**. The lookup cannot return embeddings at all. |
| Not universal | `InMemoryVectorStore` and `FederatedVectorStore` only, forwarded by `ResilientVectorStore`. #318 exists because the remote stores lack it. |

So RAPTOR must own persistent state of its own. Roughly #302 plus #312 combined.

### Ordering, and the reversal behind it

`docs/superpowers/specs/2026-08-20-raptor-real-protocol-design.md` originally deferred this fix on a
measured trigger, per #247's measure-then-fix order. **Reversed by the operator on 2026-08-20** —
*"fix it first before spending again"* — on the grounds that this defect is **structural**: it was
established by reading `IngestionContext`, not inferred from a figure, so a paid sweep would buy
evidence for something already known.

What the reversal gives up is a pinned figure for the shipped state. §1's decision to keep
`PerDocument` fully working is what gives it back.

## §1 — What changes

`RaptorOptions` gains `TreeScope`:

| Value | Meaning |
|---|---|
| `Corpus` | **New default.** Cluster across every leaf chunk the store holds. |
| `PerDocument` | Today's behaviour: cluster within one document's chunks. |

A `feat!` change. The cost is low now and will not be after the tag — all 71 packages sit at 0.1.0,
and #312 took this exact call, recorded in its commit message as the operator's: *"we are not in
production yet."*

**`PerDocument` is kept fully working, not deprecated and not deleted.** It is the control arm for
6.2.1's RAPTOR measurement. #323's precedent is explicit: `GraphLocalSearchBehavior` and
`PageRankWeight` were kept unregistered but present because deleting them would have made three
pinned figures unreproducible. The same reasoning applies before the fact here — deleting the
per-document path before its replacement figure exists would make the comparison impossible to run.

## §2 — The store: `Rag.NET.Raptor.Store`

A new package holding the abstraction and its SQLite implementation together, mirroring
`Rag.NET.Graph` — which pairs `IGraphStore` and `SqliteGraphStore` in one package, separate from
the `Rag.NET.GraphRag` behaviours.

**Why a separate package rather than inside `Rag.NET.Raptor`.** That package depends only on
`Rag.NET`, MathNet and ZeroAlloc today. Putting a SQLite dependency into it charges every RAPTOR
user for corpus clustering, including those who select `PerDocument` and never build a corpus tree.
Splitting later would itself be breaking.

**Why not `Rag.NET.Abstractions` + `Rag.NET.Storage.Sqlite`**, which is the `IParentChunkStore` /
`SqliteParentChunkStore` shape: that would put a RAPTOR-shaped concept into core Abstractions for a
single consumer. The apparent second consumer — #318, `IChunkLookup` on the remote stores — is not
one. #318 wants **lookup by key** on remote backends; RAPTOR wants **enumerate everything** locally.
Those sound alike and are different operations.

**What it holds:** leaf chunks with their embedding vectors, written during ingestion, read once per
rebuild. This is exactly what `IVectorStore` cannot give back.

**Written only under `Corpus` scope.** Under `PerDocument` the behaviour has every chunk it needs in
`ctx.EmbeddedChunks` and the leaf store is neither written nor required — so selecting
`PerDocument` costs nothing, no package reference, no second copy of every embedding. Stated
explicitly because "written during ingestion" could otherwise be read as unconditional, and an
unconditional write would double the storage cost for users who never build a corpus tree.

**Packaging tax, named rather than discovered later.** This is the 72nd package: release-please
configuration, package metadata, README, a `VerifiedBy` level, and a slot in the CI matrices.

## §3 — When clustering runs

#302's shape, for #302's reason.

- **Debounce on corpus growth.** Ingestion does not rebuild the tree per document — that is the
  defect being fixed. **The threshold's shape and value are a plan decision, not fixed here**;
  #302's equivalent debounces on entity count, and RAPTOR's natural analogue is leaf-chunk count,
  but whether it is absolute, proportional, or both wants the plan's attention rather than a number
  invented in a design doc.
- **A `RaptorTreeRebuilder` for on-demand rebuilds**, which bypasses the growth threshold and resets
  its baseline, so ingestion continuing afterwards debounces from the rebuilt state rather than a
  stale count. This mirrors `CommunityDetectionBehavior.DetectNowAsync` and
  `GraphProjectionRebuilder`.
- **The rebuild goes through the same code path as ingestion.** `DetectNowAsync`'s own remarks give
  the reason and it holds here verbatim: *"A rebuild that recomputed communities its own way would
  be a second implementation of the thing under measurement, free to drift from the one that runs
  during ingest."*

**A behaviour change users will notice, stated plainly rather than buried:** ingesting a single
document no longer produces a tree immediately. Summaries appear once the corpus crosses the
threshold or the rebuilder is called. That is the direct cost of not recomputing per document, and
it should be in the guide, not only in release notes.

## §4 — Summary identity, and #332

Corpus-level summaries carry a **reserved `DocumentId`** and a **single monotonic `ChunkIndex`
counter spanning all levels**.

### Why a reserved id

A corpus summary spans many documents; there is no document whose id it could honestly carry, and
`ctx.Metadata.DocumentId` would be a lie. Per-source attribution — inheriting the dominant
contributor's id — was rejected: it is arbitrary for a genuinely cross-document summary, and
deleting that one document would orphan summaries that also summarise others.

The reserved id also makes rebuild trivial: `DeleteByDocumentIdAsync(reserved)` then write.
**No interface change, and it works on every store today.**

### Why the monotonic counter is not optional — #332

`SummarizeClusterAsync` currently assigns `ChunkIndex = ctx.EmbeddedChunks.Count + summaryIndex`.
Both operands are wrong for the purpose: `ctx.EmbeddedChunks` is appended to only *after* the tree
loop (`AddRange(allSummaries)` in `HandleAsync`), so `.Count` is the leaf count at every level; and
`summaryIndex` is `summaryChunks.Count`, a list local to `BuildLevelAsync`, which resets per level.

So level 1's first summary and level 2's first summary both receive `leafCount + 0`. `ChunkKey` is
`(string DocumentId, int ChunkIndex)` and `TextChunk.ChunkIndex` is documented as unique within a
document, so these are two chunks with one identity. `MaxTreeDepth` defaults to `null` — recurse
until one cluster remains — so depth ≥ 2 is the ordinary case.

**The counter fix must be applied to both scopes.** The collision lives in the shared
summary-construction path and fires on per-document trees today. Fixing it only in the new path
would leave the control arm corrupt, which would then corrupt 6.2.1's comparison — the measurement
this whole phase exists to make possible.

Same defect class as `docs/plans/2026-08-06-chunk-index-collision-design.md`, which fixed indices
restarting per section in the chunking strategies. That guard did not generalise to other producers
of `TextChunk`; whether it should is a question for the plan, not a commitment here.

## §5 — Migration

Existing stored per-document summaries become stale after the default flips — they would compete for
rank against the new corpus tree.

**The release notes state a clear-and-reingest step explicitly. There is no automatic cleanup.** Old
summaries carry `raptor_level` and a real `DocumentId`, which a legitimate summary could resemble;
a heuristic that guesses wrong deletes user data. An explicit step the user takes is preferable to
a silent one the library gets wrong.

## §6 — Testing

Fast tier, no model:

- **#332 regression:** a two-level tree yields unique `(DocumentId, ChunkIndex)` across all levels.
  Fails against today's code — that is the point of writing it first. Asserted under **both**
  scopes.
- The debounce fires at the threshold and not before.
- The rebuilder bypasses the threshold and resets the baseline, so a subsequent ingest debounces
  from the rebuilt state.
- **A corpus-scope build over documents that each fall under `MinChunksForRaptor` still produces a
  tree.** This is the case per-document scope structurally cannot serve, so it is the test that
  would fail if the fix were only nominal.

## §7 — Out of scope

- **Whether summaries should compete for rank at all.** That is 6.2.1's `raptor − raptorfiltered`
  arm. Settling it here would pre-empt the measurement this phase exists to make possible.
- **The `Boost` and `Filter` over-fetch defects** — `Boost` cannot promote a summary into the result
  set, `Filter` under-fills. Both stay in
  `docs/superpowers/specs/2026-08-20-raptor-real-protocol-design.md`.
- **Generalising the chunk-index uniqueness guard** beyond RAPTOR. Noted in §4; a decision for the
  plan or a later phase.

## Decisions taken during brainstorming

| Question | Chosen | Rejected |
|---|---|---|
| Store location | New `Rag.NET.Raptor.Store`, mirroring `Rag.NET.Graph` | Inside `Rag.NET.Raptor` — forces SQLite on every user; core Abstractions — a feature concept in core for one consumer |
| Summary identity | Reserved `DocumentId` + monotonic `ChunkIndex` | Dominant-contributor attribution — arbitrary, and orphans on delete; own store (#312's answer) — disables collapsed-tree retrieval and pre-empts 6.2.1 |
| Rebuild trigger | Debounce on growth + on-demand rebuilder (#302) | Eager per-ingest rebuild — that is #300, the defect being fixed |
| Default scope | `Corpus`, breaking, clear-and-reingest documented | Automatic cleanup — heuristic, deletes user data when wrong; `PerDocument` default — package still would not do what its name says |
