# Vector Stores Implementation Plan (Phase 1.7)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the last three backlog rows: Weaviate (`IVectorStore` + `IHybridSearchable` + `ICollectionManageable`), Chroma (dense-only `IVectorStore` + `ICollectionManageable`), and Pinecone (`IVectorStore` + `ICollectionManageable` + opt-in `ISparseSearchable`) — completing the Milestone 1 backlog.

**Architecture:** Per `docs/plans/2026-07-25-vector-stores-design.md`. Weaviate and Chroma are hand-rolled ZeroAlloc.Rest clients (Weaviate search goes through its GraphQL endpoint — reuse the Linear Phase 1.6 GraphQL conventions); Pinecone uses the official first-party `Pinecone` SDK with a `UseQdrant`-style `enableSparseVectors` type-split. All three tested against real backends via Testcontainers (AzureAISearch test-project model).

**Tech Stack:** .NET 10, xUnit v3, ZeroAlloc.Rest 1.1.3 + SystemTextJson (Weaviate, Chroma), official `Pinecone` NuGet (verify current stable), Testcontainers (`cr.weaviate.io/semitechnologies/weaviate`, `chromadb/chroma`, `ghcr.io/pinecone-io/pinecone-local`).

**Conventions:** as previous phases — MA0051/MA0015/ZA0601/ZA0501/EPS05/HLQ warnings-as-errors, LoggerMessage where logging exists, commit trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`, filtered tests during work, one `dotnet build Rag.NET.slnx` per part, TDD throughout. NEVER stage `.lucent/*`.

**Read before any part:** `src/Rag.NET.Abstractions/Abstractions/IVectorStore.cs`, `ICollectionManageable.cs`, `IHybridSearchable.cs`, `ISparseSearchable.cs`, `src/Rag.NET.Abstractions/Models/Options/SearchOptions.cs`, `EmbeddedChunk`/`SearchResult` models, and one existing store end-to-end: `src/Rag.NET.VectorStores.Qdrant/QdrantVectorStore.cs` + `QdrantSparseVectorStore.cs` + `QdrantBuilderExtensions.cs`. Store posture: backend errors THROW (no Result), pipeline owns degradation.

---

## Part A — Weaviate

### Task A1: project + REST/GraphQL client + store

**Files:**
- Create: `src/Rag.NET.VectorStores.Weaviate/Rag.NET.VectorStores.Weaviate.csproj` — copy `src/Rag.NET.DataProviders.Linear/Rag.NET.DataProviders.Linear.csproj` shape (ZeroAlloc.Rest + SystemTextJson, Http.Resilience) but reference conventions of the VectorStores csprojs (assembly name `Rag.NET.Weaviate` — check how `Rag.NET.VectorStores.Qdrant.csproj` sets AssemblyName/RootNamespace and mirror). Add to `Rag.NET.slnx` (+ test project from A2).
- Create: `src/Rag.NET.VectorStores.Weaviate/IWeaviateApi.cs` — internal `[ZeroAllocRestClient]`, read `src/Rag.NET.DataProviders.Linear/ILinearApi.cs` FIRST for the GraphQL POST pattern:
  - `[Post("/v1/schema")]` create class; `[Delete("/v1/schema/{className}")]`; `[Get("/v1/schema/{className}")]` exists-probe (404 → false);
  - `[Post("/v1/batch/objects")]` store; `[Post("/v1/batch/delete")]` delete-by-filter;
  - `[Post("/v1/graphql")]` search — `WeaviateGraphQlRequest { Query, Variables }` with typed variables, response envelope `data.Get.{class}` is dynamic-keyed: model `data` as `JsonElement` and extract the class array by name (document why — GraphQL response shape is class-keyed).
  - Top-level GraphQL `errors[]` → throw naming messages (design: stores throw).
- Create: `WeaviateOptions.cs` (endpoint, ClassName required + capitalized-valid, VectorDimensions, ApiKey?, Tenant?), `WeaviateVectorStore.cs` implementing `IVectorStore, IHybridSearchable, ICollectionManageable`:
  - Deterministic UUID from `(DocumentId, ChunkIndex)`: SHA-1/MD5 of `"{docId}:{index}"` formatted as UUID (v5-style; document determinism contract — mirror `QdrantSparseVectorStore`'s point-id derivation, read it first).
  - Class schema: `vectorizer: "none"`, `vectorIndexConfig: {distance: cosine}`, properties `document_id` (text), `chunk_index` (int), `text` (text); metadata keys ride as extra object properties via auto-schema (VERIFY auto-schema is enabled by default in the image; if not, enable via env in the fixture and document the requirement).
  - Dense search GraphQL: `Get { {Class}(nearVector: {vector: $v}, limit: $n, where: $w) { text document_id chunk_index _additional { distance } } }` — score = `1 - distance/2`? NO: VERIFY Weaviate cosine `distance` semantics (0 = identical, 2 = opposite) and map `Score = 1 - distance / 2` OR use `certainty` (`= (2 - distance) / 2`) — pin whichever the live container returns for both nearVector and hybrid, with a test asserting identical-vector ⇒ score ≈ 1.
  - Hybrid GraphQL: `hybrid: {query: $q, vector: $v}` + `_additional { score }` (hybrid score is 0..1 fusion score). MinScore applied on mapped scores; TopK → limit.
  - `MetadataFilter` → `where` operand: single key `{path: [key], operator: Equal, valueText: v}`, multiple keys wrapped in `{operator: And, operands: [...]}`.
  - `DeleteByDocumentIdAsync` → `/v1/batch/delete` with `match: {class, where: {path: ["document_id"], operator: Equal, valueText: id}}`.
  - Tenant set → all object/batch/GraphQL calls carry the tenant (objects: `tenant` field; GraphQL: `tenant:` arg; class created with `multiTenancyConfig: {enabled: true}` + tenant created via `/v1/schema/{class}/tenants`). Keep tenant plumbing in helper methods to respect MA0051.
- Create: `WeaviateBuilderExtensions.cs` — `.UseWeaviate<TBuilder>(endpoint, className, vectorDimensions = 1536, Action<WeaviateOptions>? configure = null)` registering singleton `IVectorStore` + `IHybridSearchable` + `ICollectionManageable` (read `QdrantBuilderExtensions.cs`); validate eagerly (ArgumentException + paramName).

### Task A2: container tests + DI tests

- Create: `tests/Rag.NET.VectorStores.Weaviate.Tests/` — copy `tests/Rag.NET.VectorStores.AzureAISearch.Tests` csproj shape (Testcontainers, xUnit v3, collection-scoped fixture). Container: `cr.weaviate.io/semitechnologies/weaviate:latest` with env `AUTHENTICATION_ANONYMOUS_ACCESS_ENABLED=true`, `PERSISTENCE_DATA_PATH=/var/lib/weaviate`, `DEFAULT_VECTORIZER_MODULE=none` (+ auto-schema env if needed per A1), port 8080, wait on `/v1/.well-known/ready`.

```csharp
// 1. StoreAndSearch_RoundTrip (3-dim vectors; nearest returned first; text/documentId/chunkIndex mapped).
// 2. Search_IdenticalVector_ScoreNearOne (pins the distance→score mapping).
// 3. Store_SameChunkTwice_Replaces (deterministic UUID; count stays 1, text updated).
// 4. Search_MetadataFilter_FiltersServerSide (two chunks, filter matches one; And-composition with 2 keys).
// 5. Search_TopKAndMinScore_Honored.
// 6. HybridSearch_FindsKeywordOnlyMatch (chunk whose vector is orthogonal but text matches the query string — proves BM25 side; fused _additional.score mapped).
// 7. DeleteByDocumentId_RemovesAllChunksOfDoc (other doc untouched).
// 8. Collection_CreateExistsDelete_Lifecycle.
// 9. Tenant_Isolation (tenant option set: store+search in tenant A; class is multi-tenancy enabled).
// 10. GraphQlError_Throws (search against a deleted class → exception naming the Weaviate message).
// DI (tests/Rag.NET.Tests/DependencyInjection/UseWeaviateTests.cs — read UseQdrantTests.cs):
//   resolves IVectorStore/IHybridSearchable/ICollectionManageable as same singleton; empty className throws.
```

**Commits:** `feat(vector-stores): Weaviate store via REST + GraphQL hybrid search` then `test(vector-stores): Weaviate container + DI tests` (or provider+tests per TDD interleave; implementer's judgment within convention).

### Task A3: docs

- `docs/guide/` — find the vector-stores guide page (grep for "UseQdrant" in docs/guide) and add a Weaviate section: local Docker quickstart, `UseWeaviate` snippet, hybrid search note (`IHybridSearchable` — used automatically by hybrid retrieval, cross-link the retrieval guide the way the AzureAISearch section does), tenant option, auto-schema/metadata-filter note. Update any store capability matrix (grep "IHybridSearchable" in docs).
- `docs/reference/features.md`: tick the Weaviate row (checkbox table ~line 1051) + flesh the section status like sibling completed rows.

**Commit:** `docs(vector-stores): Weaviate guide section; tick feature`

---

## Part B — Chroma

### Task B1: project + client + store

**Files:**
- Create: `src/Rag.NET.VectorStores.Chroma/Rag.NET.VectorStores.Chroma.csproj` (assembly `Rag.NET.Chroma`; ZeroAlloc.Rest shape as Part A) + slnx entries (+ test project).
- Create: `IChromaApi.cs` — internal client against REST v2 (VERIFY paths against the container's `/docs` OpenAPI at first test run; pin what the live image serves):
  - `POST /api/v2/tenants/{tenant}/databases/{database}/collections` (create, body `{name, metadata: {"hnsw:space": "cosine"}}`, returns `{id}`), `GET .../collections/{name}` (by-name lookup → id; 404 → not exists), `DELETE .../collections/{name}`;
  - `POST .../collections/{collectionId}/upsert` (`{ids, embeddings, documents, metadatas}`), `POST .../collections/{collectionId}/query` (`{query_embeddings, n_results, where?}` → `{ids, documents, metadatas, distances}` nested arrays), `POST .../collections/{collectionId}/delete` (`{where: {document_id: {"$eq": id}}}`).
- Create: `ChromaOptions.cs` (endpoint, CollectionName required, Tenant = "default_tenant", Database = "default_database", ApiKey? → `Authorization: Bearer` when set), `ChromaVectorStore.cs` (`IVectorStore, ICollectionManageable`):
  - Name→UUID resolution cached in a field; on 404 during an operation, invalidate + re-resolve once (collection recreated case), then rethrow if still failing.
  - Ids `{documentId}:{chunkIndex}`; metadatas = chunk metadata + `document_id` + `chunk_index` (int); documents = text.
  - Query: score = `1 - distance` (cosine distance in [0,2] but Chroma cosine distance = 1 - cosine similarity, range [0,2]; identical ⇒ 0 ⇒ score 1). MinScore on converted score; `MetadataFilter` → `where` `$eq` per key (`$and` composition for 2+).
  - `CreateCollectionAsync(name, dims)`: validate dims > 0 but Chroma infers dimensions on first upsert — document on the method.
- Create: `ChromaBuilderExtensions.cs` — `.UseChroma<TBuilder>(endpoint, collectionName, configure?)`; singleton `IVectorStore` + `ICollectionManageable`; eager validation.

### Task B2: container tests + DI tests

- Create: `tests/Rag.NET.VectorStores.Chroma.Tests/` (AzureAISearch model). Container `chromadb/chroma:latest`, port 8000, wait on v2 heartbeat (`/api/v2/heartbeat`).

```csharp
// 1. StoreAndSearch_RoundTrip (nearest-first ordering, text + metadata mapped back).
// 2. Search_IdenticalVector_ScoreNearOne (distance→score conversion pinned).
// 3. Store_SameChunkTwice_Replaces (upsert semantics).
// 4. Search_MetadataFilter_Filters ($eq; two keys → $and).
// 5. Search_TopKAndMinScore_Honored.
// 6. DeleteByDocumentId_RemovesOnlyThatDocument.
// 7. Collection_CreateExistsDelete_Lifecycle.
// 8. CollectionRecreated_IdCacheRecovers (delete + recreate behind the store's back; next Store succeeds via re-resolve).
// DI: UseChromaTests.cs (same singleton across interfaces; empty collectionName throws).
```

**Commits:** `feat(vector-stores): Chroma store via REST v2` + `test(vector-stores): Chroma container + DI tests`

### Task B3: docs

- Guide section (local `docker run chromadb/chroma` quickstart, `UseChroma` snippet, dense-only note pointing hybrid seekers at Qdrant/Weaviate, metadata `$eq` filter note); capability matrix row; features.md tick.

**Commit:** `docs(vector-stores): Chroma guide section; tick feature`

---

## Part C — Pinecone

### Task C1: project + store + sparse variant

**Files:**
- Create: `src/Rag.NET.VectorStores.Pinecone/Rag.NET.VectorStores.Pinecone.csproj` (assembly `Rag.NET.Pinecone`) — package ref official `Pinecone` SDK: VERIFY the current stable version and that it exposes serverless index create/delete/describe, upsert/query with metadata filter + sparse values, and a configurable base URL/host for Pinecone Local. If the official SDK cannot target Pinecone Local, STOP and surface the trade-off (fallback: ZeroAlloc.Rest client per design's spirit) before proceeding. Slnx entries (+ test project).
- Create: `PineconeOptions.cs` (ApiKey required, IndexName required, VectorDimensions, Namespace?, EnableSparseVectors = false, Endpoint?/ControllerHost? override for local — shape to what the SDK accepts), `PineconeVectorStore.cs` (`IVectorStore, ICollectionManageable`):
  - `ICollectionManageable`: create serverless index (dimension, metric cosine — dotproduct when sparse enabled; cloud/region defaults documented, options-overridable), poll describe until ready (bounded wait honoring the CancellationToken), exists via list/describe, delete.
  - Vector id `{documentId}:{chunkIndex}`; metadata = chunk metadata + `document_id` + `chunk_index` + `text` (Pinecone stores no body — text lives in metadata; SearchResult.Text read back from it).
  - Search: `query(vector, topK, filter, includeMetadata: true)`, native scores, MinScore direct; `MetadataFilter` → `{key: {"$eq": v}}` composed with `$and`.
  - `DeleteByDocumentIdAsync`: delete-by-metadata-filter; VERIFY serverless + Pinecone Local support — if unsupported on serverless, use list-vector-ids-by-prefix (`{documentId}:`) + delete-by-ids and pin that path with a comment.
- Create: `PineconeSparseVectorStore.cs` (`: PineconeVectorStore, ISparseSearchable` — read `QdrantSparseVectorStore.cs` FIRST for the type-split + fail-fast conventions): sparse values on the same records (`sparseValues: {indices, values}`); `SearchSparseAsync` queries with sparse-only input; fail fast at collection create/first use when the index metric can't host sparse (cosine rejects sparse values — hence dotproduct when enabled; surface a clear InvalidOperationException naming the fix).
- Create: `PineconeBuilderExtensions.cs` — `.UsePinecone<TBuilder>(apiKey, indexName, vectorDimensions = 1536, Action<PineconeOptions>? configure = null)`; `EnableSparseVectors` on options picks the sparse type (UseQdrant pattern verbatim: one instance registered as `IVectorStore` + `ICollectionManageable` + conditionally `ISparseSearchable`); eager validation.

### Task C2: container tests + DI tests

- Create: `tests/Rag.NET.VectorStores.Pinecone.Tests/` — container `ghcr.io/pinecone-io/pinecone-local:latest` (env `PORT=5080`, `PINECONE_HOST=localhost` — read Pinecone Local docs via WebFetch/WebSearch first; it emulates the control + data planes on one port range). FIRST ACTION of this task: verify which features Pinecone Local supports (sparse indexes? metadata delete-by-filter? dotproduct?) and scope tests honestly — every emulator gap becomes a documented limitation in the guide, never a fake pass.

```csharp
// 1. Index_CreateExistsDelete_Lifecycle (create waits until ready).
// 2. StoreAndSearch_RoundTrip (text read back from metadata; native score; identical vector ⇒ score ≈ 1 for cosine).
// 3. Store_SameChunkTwice_Replaces (same id upsert).
// 4. Search_MetadataFilter_Filters ($eq; $and for two keys).
// 5. Search_TopKAndMinScore_Honored.
// 6. Namespace_Isolation (two stores, same index, different Namespace — no cross-talk).
// 7. DeleteByDocumentId_RemovesOnlyThatDocument (whichever deletion path C1 pinned).
// 8. Sparse_StoreAndSearch_RoundTrip (sparse variant on a dotproduct index; skip-with-documented-gap if Pinecone Local lacks sparse).
// 9. SparseVariant_OnCosineIndex_FailsFast (clear error naming dotproduct requirement).
// DI: UsePineconeTests.cs — default resolves PineconeVectorStore, no ISparseSearchable registration;
//     EnableSparseVectors=true resolves PineconeSparseVectorStore serving all three interfaces; empty apiKey/indexName throws.
```

**Commits:** `feat(vector-stores): Pinecone store via official SDK (+ sparse variant)` + `test(vector-stores): Pinecone Local container + DI tests`

### Task C3: docs + close-out

- Guide section (API key + serverless quickstart, Pinecone Local dev loop, `UsePinecone` snippet, namespace isolation, sparse/dotproduct requirement + pairing with `UseSpladeEncoder`, text-in-metadata note incl. Pinecone's metadata size limit); capability matrix row; features.md tick.
- `docs/planning/ROADMAP.md` + `MILESTONE.md`: Phase 1.7 complete (2026-07-25) — Milestone 1 backlog fully ticked.

**Commit:** `docs(vector-stores): Pinecone guide section; tick feature; complete phase 1.7`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Three new test projects green + `tests/Rag.NET.Tests` (incl. new DI tests) green + existing `tests/Rag.NET.VectorStores.*` suites untouched-and-green.
3. features.md: all three rows ticked → Milestone 1 backlog complete. Final whole-phase review; merge decision.
