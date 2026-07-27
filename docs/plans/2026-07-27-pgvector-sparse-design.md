# PgVector Sparse Storage — Design (Phase 2.3)

**Date:** 2026-07-27
**Milestone:** 2 — Deferred Items & Technical Debt, Phase 2.3
**Covers:** the "PgVector sparse storage (SPLADE)" deferral from Phase 1.2

## Why the original deferral no longer applies

Phase 1.2 scoped SPLADE to Qdrant and the in-memory store because PgVector had no native
sparse type; `docs/reference/features.md` still prescribes "PgVector via separate column + RRF
merge", i.e. a client-side fusion that would have been strictly weaker than Qdrant's.

That is obsolete. Measured against `pgvector/pgvector:pg17` on 2026-07-27:

| Capability | Result |
|---|---|
| pgvector version in the image the tests already use | **0.8.2** |
| `sparsevec(30522)` (SPLADE/BERT vocabulary size) | works |
| `CREATE INDEX … USING hnsw (col sparsevec_ip_ops)` | builds |
| `sparsevec_cosine_ops` | also builds |
| **HNSW limit** | **1000 non-zero elements** |
| Unindexed limit | 16000 non-zero elements |
| `<#>` operator | returns the **negative** inner product |
| Repo's SPLADE `TopTerms` default (`OnnxSpladeOptions.cs:36`) | **256** — well inside the HNSW cap |

So PgVector can serve SPLADE natively, with a real index and a server-side dot product. This
design **overrides** the features.md "separate column + RRF merge" prescription rather than
honouring it, and says so where that text lives.

## Scope decisions (agreed)

1. **Fix the duplicate-row defect**: add a unique key on `(document_id, chunk_index)` and make
   `StoreAsync` an upsert. Migration fails fast on pre-existing duplicates; it does not delete
   data.
2. **Sparse lives as a nullable column on `rag_chunks`**, not a side table.
3. **Also add the dense ANN index**, making the guide's long-standing claim true — with its
   approximation consequences documented prominently.

---

## 1. The pre-existing defect this phase must fix first

`PgVectorStore.StoreAsync` is a plain `INSERT` with no `ON CONFLICT`, and the table's primary
key is a synthetic identity — there is **no unique constraint on `(document_id, chunk_index)`**.
Re-storing a chunk therefore *duplicates the row*.

That already contradicts two documented contracts:

- `RagPipelineReindexExtensions.cs:26-27` states chunks are "re-stored via `vectorStore` (which
  replaces by `(DocumentId, ChunkIndex)`)" — so `ReindexStaleAsync` against PgVector silently
  duplicates every chunk it touches today.
- `ISparseSearchable` requires implementations to "key by `(DocumentId, ChunkIndex)`:
  re-storing the same chunk replaces its sparse vector rather than duplicating it."

Sparse storage cannot be correct on top of a table that cannot identify a chunk, so this is
in scope rather than adjacent to it.

**Design:**

- `InitializeAsync` creates `CREATE UNIQUE INDEX IF NOT EXISTS … ON rag_chunks (document_id, chunk_index)`.
- `StoreAsync` becomes `INSERT … ON CONFLICT (document_id, chunk_index) DO UPDATE SET …`.
- **The `DO UPDATE SET` list deliberately excludes `sparse_embedding`.** Writing every column
  would null out a chunk's sparse vector whenever its dense vector is re-stored — exactly the
  hazard `PineconeSparseVectorStore` has to carry as an ORDERING CONTRACT ("calling `StoreAsync`
  AFTER `StoreSparseAsync` for the same chunk silently drops its sparse vector"). PgVector can
  *eliminate* that hazard instead of documenting it, and should.

**Migration is fail-fast, never destructive.** An existing deployment may already hold duplicate
keys — that is the bug, so the rows are already there. Creating the unique index would fail with
a raw PostgreSQL error. Instead `InitializeAsync` probes for duplicates first and throws with the
duplicate-key count and the query to inspect them, leaving the database untouched. Deleting
"extra" rows during startup would be silent data loss on a path the user did not ask to migrate.

## 2. Schema

`sparse_embedding sparsevec(N) NULL` on `rag_chunks`, added by the sparse subtype's
`InitializeAsync` (`ALTER TABLE … ADD COLUMN IF NOT EXISTS`), where `N` is the sparse vocabulary
size — a new parameter defaulting to **30522** (BERT/SPLADE vocabulary), plumbed separately from
the existing dense `_vectorDimensions`.

A nullable column on the existing table rather than a side table because:
- `DeleteByDocumentIdAsync` needs no change — one `DELETE` still removes everything.
- Dense and sparse stay transactionally consistent and cannot drift.
- No foreign key is needed (which would itself have required the unique key above).

## 3. Type split

`PgVectorStore` is `sealed` today. It gets unsealed with `private protected` members, and:

```
PgVectorSparseVectorStore : PgVectorStore, ISparseSearchable
```

selected by `UsePgVector(connectionString, vectorDimensions, enableSparseVectors: true)`. This
mirrors `QdrantSparseVectorStore` and `PineconeSparseVectorStore`, and exists for the same
stated reason: the pipelines probe `store is ISparseSearchable`, and a dense-only store must
never advertise a capability it lacks — otherwise `SparseEmbeddingBehavior` computes SPLADE
vectors that nothing can store.

Registration follows `UseQdrant`: one instance registered as `IVectorStore` and
`ICollectionManageable`, with the declared variable typed as the base.

## 4. Two gates that do not exist today

**pgvector version.** The store issues `CREATE EXTENSION IF NOT EXISTS vector` but never checks
`extversion`. `sparsevec` requires ≥ 0.7.0; on an older extension the failure today would be a
raw "type sparsevec does not exist". The sparse subtype reads the version at initialize and
throws naming the installed version, the required version, and the upgrade path.

**HNSW non-zero cap.** HNSW rejects a `sparsevec` with more than 1000 non-zero elements. The
default `TopTerms` of 256 is comfortably inside it, but nothing stops a user raising it. The
store checks vector length before insert and throws naming `OnnxSpladeOptions.TopTerms`, the
1000 limit, and the option to drop the index — rather than surfacing pgvector's context-free
error at insert time.

## 5. Scoring

`<#>` returns the **negative** inner product, so the store selects `-(sparse_embedding <#> $1)`
to satisfy `ISparseSearchable`'s documented "dot product of matching term weights".

`MinScore` applies on that **raw, unbounded dot-product scale** — not cosine, and not comparable
to the dense path's `1 - (embedding <=> $1)`. This matches `InMemoryVectorStore` and
`PineconeSparseVectorStore` and must be said plainly in the docs, because the same option name
means two different scales depending on which method is called.

Rows sharing no terms score exactly 0 and are **excluded**, mirroring the in-memory reference
where only chunks sharing at least one term ever enter the score map. Since every SPLADE weight
is > 0, "score > 0" is exactly equivalent to "shares ≥ 1 term".

**Index base differs**: SPLADE term ids are 0-based vocabulary indices; `sparsevec` literals are
1-based. The formatter shifts, and a round-trip test pins it.

## 6. Dense ANN index

`docs/guide/vector-stores.md:22` has long claimed PgVector uses "IVFFlat / HNSW (pgvector)". The
code builds no ANN index at all — only a btree on `document_id` — so dense search is a
sequential scan. This phase adds `USING hnsw (embedding vector_cosine_ops)`, matching the `<=>`
operator `SearchAsync` already uses.

**This changes behaviour for existing users and must be documented, not buried:**

- HNSW is **approximate**. Dense results after upgrading may differ from the exact
  sequential-scan results returned today. Recall is tunable via `hnsw.ef_search` but is not 100%
  by default.
- Building the index on a large existing table is **slow and memory-hungry**; it happens inside
  `InitializeAsync`, which callers may not expect to be long-running.

Both belong in the guide and in the merge notes. The alternative — leaving the docs lying — is
worse, but the honest fix has a cost and hiding that cost would repeat the pattern this
milestone keeps correcting.

## 7. Error handling

House store posture: backend errors **throw**; the pipeline owns degradation. The three new
throws (duplicate keys at migration, unsupported pgvector version, oversized sparse vector) are
all startup- or authoring-time programming/configuration errors, deliberately loud, each naming
the fix. `StorageBehavior` already catches and logs `StoreSparseAsync` failures so ingestion
survives a sparse-side problem; nothing changes there.

## 8. Testing

Two suites, following the existing split:

- **`tests/Rag.NET.VectorStores.PgVector.Tests`** (own container per class): capability probe —
  `PgVectorStore` is *not* `ISparseSearchable`, the sparse subtype *is* and is still substitutable
  for the base; sparsevec literal formatting incl. the 0→1 index shift; the >1000 non-zero guard.
- **`tests/Rag.NET.VectorStores.IntegrationTests`** (shared fixture, per-test GUID doc ids and
  `try/finally` cleanup — the established isolation pattern): store/search round trip with
  **pinned exact dot products** (the Qdrant sparse tests are the template), `TopK`, `MinScore` on
  the dot-product scale, metadata filtering, delete-removes-sparse-searchability, idempotent
  re-store proving no duplicate rows, dense upsert not clearing sparse, and fail-fast on a table
  containing pre-existing duplicates.

The pgvector-version gate cannot be tested against the pinned image (which is 0.8.2); it is
verified by construction with the check isolated in a testable helper, and the limitation is
recorded rather than claimed as covered.

## 9. Documentation

- `docs/guide/vector-stores.md`: feature matrix "No (deferred)" → yes; **correct the index-algorithm
  row**, which is wrong today and only becomes true with §6; new PgVector sparse subsection; the
  mermaid diagram gains the sparse subtype. **Anchor hazard:** adding a section titled "Sparse
  vectors (SPLADE)" renumbers the existing `#sparse-vectors-splade-1` anchor and breaks two
  inbound links (`retrieval.md:214`, `vector-stores.md:532`) unless it is placed after Pinecone's
  or the links are updated in the same commit.
- `docs/guide/retrieval.md:211,214`: PgVector joins the sparse-capable list; delete "PgVector
  sparse storage is deferred".
- `docs/reference/features.md:821,823,1080`: package list, the now-overridden "separate column +
  RRF merge" sentence, and the roadmap row.

## Out of scope

- IVFFlat as an alternative to HNSW, and exposing HNSW build parameters (`m`, `ef_construction`).
  The index is created with pgvector's defaults; tuning is a separate concern with its own
  benchmarking needs.
- Server-side hybrid fusion of the dense and sparse columns in one query. The ensemble path
  already fuses client-side via RRF, consistent with every other store.
- Backfilling sparse vectors for chunks ingested before sparse was enabled — `RegenerateSparseAsync`
  already exists for that.
