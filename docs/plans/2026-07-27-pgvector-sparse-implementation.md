# PgVector Sparse Storage Implementation Plan (Phase 2.3)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** SPLADE sparse vectors stored and searched natively in PgVector via `sparsevec`, with the `(document_id, chunk_index)` duplicate-row defect fixed underneath it and the long-claimed dense ANN index finally built.

**Architecture:** Per `docs/plans/2026-07-27-pgvector-sparse-design.md`. Part A fixes the duplicate-row defect and adds the dense HNSW index on the existing store — it is a prerequisite, because sparse storage cannot key a chunk that the schema cannot identify. Part B unseals `PgVectorStore` and adds `PgVectorSparseVectorStore`. Part C is docs. **A must complete before B.**

**Tech Stack:** .NET 10, Npgsql 8.0.9, Pgvector 0.3.2 (which already ships a `SparseVector` type and maps `sparsevec` through `UseVector()`), PostgreSQL with pgvector 0.8.2 via Testcontainers (`pgvector/pgvector:pg17`), xUnit v3.

**Conventions:** MA0051 (≤60-line methods — `SearchAsync` is already 56 lines, so split query building into helpers from the start), MA0015, ZA0601/ZA0501 (no LINQ/boxing in hot loops), EPS05/EPS06, HLQ012/HLQ013 — all warnings-as-errors, build must end 0/0. **HLQ012 is muted in tests but active in `src/`**, and the repo has only two justified pragmas; do not add a third — build payloads in synchronous helpers instead. Conventional commits ending with a blank line then `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Never stage `.lucent/*` or `.claude/worktrees/*`.**

**Read first:** the design doc; `src/Rag.NET.VectorStores.PgVector/PgVectorStore.cs` in full; `src/Rag.NET.VectorStores.Qdrant/QdrantSparseVectorStore.cs` (the type-split template); `src/Rag.NET.VectorStores.Pinecone/PineconeSparseVectorStore.cs:18-24` (the ORDERING CONTRACT this design exists to avoid needing); `src/Rag.NET.Abstractions/Abstractions/ISparseSearchable.cs`; `src/Rag.NET/Storage/InMemoryVectorStore.cs:136-225` (reference sparse semantics).

**Empirically established** (measured 2026-07-27 against `pgvector/pgvector:pg17`; re-verify anything you depend on):
- pgvector 0.8.2; `sparsevec(30522)` works; HNSW builds with `sparsevec_ip_ops` and `sparsevec_cosine_ops`.
- **HNSW caps `sparsevec` at 1000 non-zero elements**; unindexed cap is 16000.
- `<#>` returns the **negative** inner product (`{5:1.5,900:2.0} <#> {5:1.0,900:1.0}` → `-3.5`).
- `OnnxSpladeOptions.TopTerms` defaults to **256**.

---

## Part A — Fix the duplicate-row defect + dense index (prerequisite)

### Task A1: unique key + upsert, with fail-fast migration

**Files:**
- Modify: `src/Rag.NET.VectorStores.PgVector/PgVectorStore.cs` — `InitializeAsync` (~:23-60), `StoreAsync` (~:62-89), `CreateCollectionAsync` (~:165-200)
- Test: `tests/Rag.NET.VectorStores.PgVector.Tests/PgVectorStoreTests.cs`

**The defect:** the PK is a synthetic identity and `StoreAsync` is a plain `INSERT` with no `ON CONFLICT`, so re-storing a chunk duplicates the row. `RagPipelineReindexExtensions.cs:26-27` documents the opposite ("replaces by `(DocumentId, ChunkIndex)`"), and `ISparseSearchable` requires it.

**Write the failing test first:**

```csharp
// 1. StoreAsync_SameChunkTwice_ReplacesInsteadOfDuplicating
//    Store one chunk, store it again with different text, search, assert exactly ONE result
//    with the NEW text. Fails today with two rows.
```

Then implement:
- `CREATE UNIQUE INDEX IF NOT EXISTS idx_rag_chunks_doc_chunk ON rag_chunks (document_id, chunk_index)` in `InitializeAsync`, and the equivalent in `CreateCollectionAsync` for named tables.
- `StoreAsync` → `INSERT … ON CONFLICT (document_id, chunk_index) DO UPDATE SET text = EXCLUDED.text, metadata = EXCLUDED.metadata, embedding = EXCLUDED.embedding`.
- **Do not add `sparse_embedding` to that SET list in Part B** — see the note in B2.

**Fail-fast migration.** An existing table may already hold duplicates. Before creating the unique index, probe:

```sql
SELECT count(*) FROM (
    SELECT document_id, chunk_index FROM rag_chunks
    GROUP BY document_id, chunk_index HAVING count(*) > 1
) d
```

If non-zero, throw naming the duplicate-key count and giving that query so the user can inspect. **Do not delete rows** — this is a path the user did not ask to migrate, and silent deletion during startup is data loss.

```csharp
// 2. InitializeAsync_TableWithPreExistingDuplicates_FailsFast
//    Create the old schema by hand (no unique index), insert two rows with the same
//    (document_id, chunk_index), then run InitializeAsync and assert the throw names the
//    count and does NOT delete either row.
```

**Commit:** `fix(vector-stores): key PgVector chunks by (document_id, chunk_index)`

### Task A2: the dense HNSW index

**Files:** same store file; `InitializeAsync` and `CreateCollectionAsync`.

`docs/guide/vector-stores.md:22` claims PgVector uses "IVFFlat / HNSW (pgvector)". It builds no ANN index at all — dense search is a sequential scan. Add:

```sql
CREATE INDEX IF NOT EXISTS idx_rag_chunks_embedding ON rag_chunks USING hnsw (embedding vector_cosine_ops)
```

`vector_cosine_ops` because `SearchAsync` orders by `<=>`. Use pgvector defaults for `m`/`ef_construction` (tuning is out of scope).

**This changes behaviour and the change must be visible in code, not only docs:** HNSW is approximate, so dense results may differ from today's exact scan, and building the index on a large existing table is slow and memory-hungry inside `InitializeAsync`. Put both facts in the XML doc on `InitializeAsync`.

```csharp
// 3. InitializeAsync_CreatesHnswIndexOnEmbedding — assert via pg_indexes that the index
//    exists and uses hnsw. (Correctness of ANN recall is not this test's job.)
```

**Commit:** `feat(vector-stores): build the HNSW index PgVector already claimed to have`

---

## Part B — Sparse storage

### Task B1: unseal the base, add the sparse subtype skeleton

**Files:**
- Modify: `src/Rag.NET.VectorStores.PgVector/PgVectorStore.cs` — drop `sealed`, change the members the subtype needs to `private protected` (`_dataSource`, `_vectorDimensions`, and whatever DDL/mapping helpers B2 reuses). Follow `QdrantVectorStore.cs:18-22`, which did exactly this.
- Create: `src/Rag.NET.VectorStores.PgVector/PgVectorSparseVectorStore.cs`
- Modify: `src/Rag.NET.VectorStores.PgVector/PgVectorBuilderExtensions.cs`
- Test: `tests/Rag.NET.VectorStores.PgVector.Tests/PgVectorSparseCapabilityTests.cs` (new; mirror `tests/Rag.NET.VectorStores.Qdrant.Tests/QdrantSparseCapabilityTests.cs`)

`public sealed class PgVectorSparseVectorStore : PgVectorStore, ISparseSearchable`, selected by a new `enableSparseVectors` parameter on `UsePgVector` plus a `sparseVocabularySize` parameter defaulting to **30522**. Register one instance as `IVectorStore` + `ICollectionManageable`, base-typed variable, exactly like `QdrantBuilderExtensions.cs:21-36`.

Carry the rationale comment across: the split exists so `store is ISparseSearchable` stays honest — a dense-only store must never advertise the capability, or `SparseEmbeddingBehavior` computes SPLADE vectors nothing can store.

```csharp
// 1. DenseStore_IsNotSparseSearchable    — Assert.False(store is ISparseSearchable)
// 2. SparseStore_IsSparseSearchable      — Assert.True(...)
// 3. SparseStore_IsStillSubstitutableForTheDenseStore — Assert.True(store is PgVectorStore)
// 4. DI: UsePgVector(enableSparseVectors: true) resolves the sparse type; default does not
```

**Commit:** `feat(vector-stores): PgVector sparse store type split`

### Task B2: schema, gates, and `StoreSparseAsync`

**Files:** `PgVectorSparseVectorStore.cs`; tests in `tests/Rag.NET.VectorStores.IntegrationTests/`

**Schema** — override `InitializeAsync` to call `base.InitializeAsync` then:
```sql
ALTER TABLE rag_chunks ADD COLUMN IF NOT EXISTS sparse_embedding sparsevec({vocabSize})
CREATE INDEX IF NOT EXISTS idx_rag_chunks_sparse ON rag_chunks USING hnsw (sparse_embedding sparsevec_ip_ops)
```
`sparsevec_ip_ops` because SPLADE similarity is a dot product.

**Gate 1 — pgvector version.** The store issues `CREATE EXTENSION IF NOT EXISTS vector` but never checks `extversion`; `sparsevec` needs ≥ 0.7.0. Read it:
```sql
SELECT extversion FROM pg_extension WHERE extname = 'vector'
```
and throw naming the installed version, the required version, and the upgrade path. **Put the version comparison in an `internal static` helper taking a string**, so it is unit-testable — the pinned image is 0.8.2 and the failure path cannot be reached against it. Test the helper directly; record in your report that the end-to-end path is verified by construction only.

**Gate 2 — the HNSW 1000 non-zero cap.** Check `sparse.Count` before insert and throw naming `OnnxSpladeOptions.TopTerms`, the 1000 limit, and the option of dropping the index — rather than letting pgvector's context-free error surface at insert time.

**`StoreSparseAsync`** — `UPDATE rag_chunks SET sparse_embedding = $1 WHERE document_id = $2 AND chunk_index = $3`, skipping items where `sparse.Count == 0` (the contract says empty vectors are skipped). Build the parameter payload in a **synchronous helper** and then await, so HLQ012 never fires.

**CRITICAL — do not add `sparse_embedding` to Part A's `DO UPDATE SET` list.** If a dense re-store wrote every column, it would null out the chunk's sparse vector — precisely the hazard `PineconeSparseVectorStore.cs:18-24` has to carry as an ORDERING CONTRACT. PgVector can eliminate it. Pin that with a test:

```csharp
// 5. StoreAsync_AfterStoreSparseAsync_DoesNotClearTheSparseVector
//    dense store → sparse store → dense store again → sparse search still finds it.
//    This is the test that proves the hazard is designed out rather than documented.
```

**The sparsevec literal.** SPLADE term ids are **0-based**; `sparsevec` literals are **1-based** — format as `{i+1:w,...}/dim` with ascending indices. Either use `Pgvector.SparseVector` (0.3.2 ships it; verify `UseVector()` maps it after `ReloadTypesAsync`) or format the literal yourself with `CultureInfo.InvariantCulture`. Whichever you choose, pin the shift:

```csharp
// 6. SparseLiteral_ShiftsFromZeroBasedToOneBased — a SparseVector with index 0 round-trips
//    through the database and comes back as index 0.
```

**Commit:** `feat(vector-stores): store SPLADE vectors in a pgvector sparsevec column`

### Task B3: `SearchSparseAsync`

**Files:** `PgVectorSparseVectorStore.cs`; integration tests.

```sql
SELECT document_id, chunk_index, text, metadata, -(sparse_embedding <#> $1) AS score
FROM rag_chunks
WHERE sparse_embedding IS NOT NULL
  AND -(sparse_embedding <#> $1) > 0
  AND -(sparse_embedding <#> $1) >= $2      -- MinScore
  [AND metadata @> $4::jsonb]               -- when MetadataFilter is set
ORDER BY sparse_embedding <#> $1
LIMIT $3
```

Three things that are easy to get wrong:
- **`<#>` is the negative inner product**, so the score is `-(…)`. `ORDER BY sparse_embedding <#> $1` ascending is correct (most negative = highest dot product) and is what lets the HNSW index be used — ordering by the negated expression would not.
- **`> 0` excludes rows sharing no terms.** `InMemoryVectorStore` only scores chunks sharing ≥1 term, so a disjoint chunk is absent rather than present with score 0. All SPLADE weights are > 0, so `score > 0` ⇔ "shares ≥ 1 term". Without this, `MinScore = 0` would return every row.
- **`MinScore` is on the raw unbounded dot-product scale**, not cosine — the same option name means a different scale than `SearchAsync`. Say so in the XML doc.

Parameters are positional and bound **in Add order** (`$1` sparse vector, `$2` MinScore, `$3` TopK, `$4` filter) — the existing `SearchAsync` does this and a mismatch is silent.

`SearchSparseAsync` with `query.Count == 0` returns `[]` without a round trip.

MA0051: `SearchAsync` is already 56 lines; build the SQL in a helper.

Integration tests (shared fixture, per-test `$"pgv-{Guid.CreateVersion7():N}"` doc ids, `try/finally` cleanup — copy `PgVectorVectorStoreTests.cs`):

```csharp
// 7.  StoreSparse_AndSearchSparse_RanksByDotProduct — PIN EXACT SCORES, as
//     QdrantSparseVectorStoreTests does (Assert.Equal(6.0, results[0].Score, precision: 3))
// 8.  SearchSparse_DisjointVector_ReturnsEmpty — not "returns rows with score 0"
// 9.  SearchSparse_TopKRespected
// 10. SearchSparse_MinScore_AppliesOnDotProductScale
// 11. SearchSparse_MetadataFilter_Filters
// 12. DeleteByDocumentId_RemovesSparseSearchability
// 13. StoreSparse_SameChunkTwice_Replaces (no duplicate rows)
```

**Commit:** `feat(vector-stores): sparse search over pgvector sparsevec`

---

## Part C — Docs

**Files:**
- `docs/guide/vector-stores.md` — feature matrix row `:18` ("No (deferred)" → yes); **correct the index-algorithm row `:22`**, which is false today and only becomes true via Task A2; new PgVector sparse subsection; mermaid diagram `:42-55` gains the sparse subtype. Document the dense-HNSW consequences: approximate results, and a potentially long `InitializeAsync` on a large existing table.
- **Anchor hazard:** a new section titled "Sparse vectors (SPLADE)" renumbers the existing `#sparse-vectors-splade-1` anchor and breaks two inbound links (`retrieval.md:214`, `vector-stores.md:532`). Either place the new section *after* Pinecone's or fix both links in the same commit — verify by grepping for both anchors afterwards.
- `docs/guide/retrieval.md:211,214` — PgVector joins the sparse-capable list; delete "PgVector sparse storage is deferred".
- `docs/reference/features.md:821,823,1080` — package list; **override** the "PgVector via separate column + RRF merge" sentence (this design does it natively, server-side); roadmap row.

Also state plainly that `MinScore` means cosine similarity on the dense path and a raw unbounded dot product on the sparse path.

**Commit:** `docs(vector-stores): PgVector sparse storage and the real index story`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. `tests/Rag.NET.VectorStores.PgVector.Tests`, `tests/Rag.NET.VectorStores.IntegrationTests`, and `tests/Rag.NET.Tests` (~1250) all green. Report exact counts.
3. Grep to confirm no broken doc anchors after the Part C renumbering.
4. `docs/planning/ROADMAP.md` debt entry + `MILESTONE.md` Phase 2.3 — **at close-out, after the whole-phase review, not per part.**
5. Whole-phase review; merge decision.
