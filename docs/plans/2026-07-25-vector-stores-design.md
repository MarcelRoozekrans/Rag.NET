# Vector Stores — Design (Phase 1.7)

**Date:** 2026-07-25
**Milestone:** 1 — Feature Backlog, Phase 1.7 (final phase)
**Covers features.md rows:** Weaviate Vector Store; Chroma Vector Store; Pinecone Vector Store

## Scope decisions (agreed)

1. **Clients — mixed:** the official first-party `Pinecone` .NET SDK for Pinecone;
   hand-rolled ZeroAlloc.Rest clients for Weaviate and Chroma (no maintained first-party
   .NET clients; the house Slack/Notion/Linear pattern applies, including the Phase 1.6
   GraphQL conventions for Weaviate's query endpoint).
2. **Native capabilities only** (honest-capability posture from Phase 1.2): Weaviate adds
   `IHybridSearchable` (native BM25+vector fusion); Pinecone adds opt-in
   `ISparseSearchable` (native sparse values, Qdrant type-split precedent); Chroma stays
   dense-only. No client-side fusion emulation anywhere.
3. **Testcontainers for all three** (existing store-test convention): official Weaviate and
   Chroma images plus Pinecone Local (`ghcr.io/pinecone-io/pinecone-local`).

## 1. Weaviate (`src/Rag.NET.VectorStores.Weaviate`, ns `Rag.NET.Weaviate`)

- `WeaviateVectorStore : IVectorStore, IHybridSearchable, ICollectionManageable`.
- **Transport:** ZeroAlloc.Rest `IWeaviateApi` (internal) —
  REST for management/writes: `POST/DELETE /v1/schema[/{class}]`, `GET /v1/schema/{class}`
  (exists), `POST /v1/batch/objects` (store), `POST /v1/batch/delete` (delete by
  `document_id`); **GraphQL `POST /v1/graphql`** for search. Const query documents with
  typed variables, GraphQL `errors[]` → failure naming the messages — the Linear
  conventions verbatim.
- **Search:** dense = `Get { {Class}(nearVector: {vector}, limit) }`; hybrid =
  `Get { {Class}(hybrid: {query, vector, alpha}, limit) }` with `_additional { score }`
  (hybrid) / `_additional { certainty }`or distance (dense) mapped to `SearchResult.Score`
  (implementer verifies which additional field Weaviate returns per mode and pins it).
  `SearchOptions.MetadataFilter` → server-side `where` operand (`Equal` per key, `And`
  composed). MinScore applied to the mapped score; TopK → `limit`.
- **Schema/data model:** one Weaviate class per store instance. Fixed properties
  `document_id` (filterable), `chunk_index` (int), `text` (searchable — feeds BM25);
  chunk metadata written as additional object properties relying on Weaviate auto-schema
  so arbitrary keys become filterable properties (implementer verifies auto-schema default
  and pins the class config). Cosine distance; `vectorizer: none` (we bring vectors).
- **Replace semantics:** deterministic object UUID (v5-style hash) from
  `(DocumentId, ChunkIndex)` — re-storing a chunk replaces it.
- **Options** (`WeaviateOptions`, validated in `UseWeaviate`): endpoint, class name
  (required, Weaviate-valid capitalized name), vector dimensions, optional `ApiKey`
  (Bearer; anonymous default for local), optional `Tenant` — when set, the class is
  created with multi-tenancy enabled and all reads/writes carry the tenant.
- DI: `.UseWeaviate(endpoint, className, vectorDimensions = 1536, configure?)` —
  singleton `IVectorStore` + `IHybridSearchable` + `ICollectionManageable`.

## 2. Chroma (`src/Rag.NET.VectorStores.Chroma`, ns `Rag.NET.Chroma`)

- `ChromaVectorStore : IVectorStore, ICollectionManageable` — deliberately the lightweight
  dense-only adapter (per features.md intent).
- **Transport:** ZeroAlloc.Rest `IChromaApi` against the REST **v2** API:
  `/api/v2/tenants/{tenant}/databases/{database}/collections` (+ `/{id}/upsert`,
  `/{id}/query`, `/{id}/delete`, get-by-name, delete-by-name). Defaults `default_tenant` /
  `default_database` (options-overridable). Collections are addressed by UUID: resolve the
  configured collection name → id once, cache it, invalidate on 404.
- **Data model:** record id `{documentId}:{chunkIndex}` (upsert = replace); `documents` =
  chunk text; `metadatas` = chunk metadata + `document_id` + `chunk_index` keys;
  `where: {document_id: {"$eq": ...}}` for delete-by-document and metadata equality
  filters for search. Cosine space (`hnsw:space = cosine` collection metadata);
  query returns distances → `Score = 1 - distance`, MinScore applied on the converted
  score, TopK → `n_results`.
- DI: `.UseChroma(endpoint, collectionName, configure?)` — singleton `IVectorStore` +
  `ICollectionManageable`. `CreateCollectionAsync(name, dims)` creates with cosine config
  (Chroma infers dimensions on first upsert; the dims argument is validated but not sent
  unless the v2 API accepts it — implementer verifies and documents).

## 3. Pinecone (`src/Rag.NET.VectorStores.Pinecone`, ns `Rag.NET.Pinecone`)

- **Client:** official first-party `Pinecone` NuGet SDK (the one published by Pinecone;
  implementer pins the current stable and verifies serverless + sparse surface).
- `PineconeVectorStore : IVectorStore, ICollectionManageable`; opt-in
  `PineconeSparseVectorStore : PineconeVectorStore, ISparseSearchable` registered by an
  `enableSparseVectors` flag (the `UseQdrant` type-split precedent). Sparse values ride on
  the same records; `InitializeAsync`/first use **fails fast** if the index metric/type
  cannot host sparse values (honest capability — no silent degradation).
- **Index/namespace mapping:** `ICollectionManageable` manages serverless **indexes**
  (create with dimensions + metric — cosine default, dotproduct when sparse enabled;
  exists; delete; create waits for index readiness). Optional `Namespace` option scopes
  all upserts/queries/deletes for the features.md "namespace-based collection isolation".
- **Data model:** vector id `{documentId}:{chunkIndex}`; metadata = chunk metadata +
  `document_id`, `chunk_index`, `text` (Pinecone stores no document body — text lives in
  metadata, consistent with what Qdrant does with payload). Delete-by-document via
  metadata filter (implementer verifies serverless delete-by-filter support; fallback:
  list-by-prefix `{documentId}:` then delete ids — pin whichever Pinecone Local also
  supports). Search: `query` with vector + metadata filter, scores returned natively,
  MinScore/TopK direct.
- DI: `.UsePinecone(apiKey, indexName, vectorDimensions = 1536, configure?)` with
  `enableSparseVectors` on the options; singleton registrations as above. Local/emulator
  use supported via an options `Endpoint`/host override (needed for Pinecone Local).

## Error handling summary

House store posture (Qdrant/AzureAISearch/PgVector precedent): backend and transport
errors **throw** — the pipeline layer owns degradation. ZeroAlloc.Rest `Result` failures
and GraphQL/REST error bodies are unwrapped into exceptions naming the backend message.
Cancellation propagates. Registration validates options eagerly (ArgumentException with
paramName, MA0015).

## Testing

- Per-store test projects mirroring `Rag.NET.VectorStores.AzureAISearch.Tests`
  (Testcontainers directly in the test class/fixture, collection-scoped):
  - **Weaviate** (`cr.weaviate.io/semitechnologies/weaviate`, anonymous access, ready
    endpoint wait): store/search round-trip, replace-on-restore (deterministic UUID),
    metadata `where` filtering, TopK/MinScore, delete-by-document, hybrid search returns
    fused results (text-only match found by BM25 side), collection lifecycle, tenant path.
  - **Chroma** (`chromadb/chroma`): round-trip, distance→score conversion (identical
    vector ⇒ score ≈ 1), metadata filter, upsert-replace, delete-by-document, collection
    lifecycle + name→UUID caching behavior.
  - **Pinecone** (`ghcr.io/pinecone-io/pinecone-local`): index lifecycle, round-trip,
    namespace isolation, metadata filter, delete-by-document, sparse store/search via the
    sparse variant. Implementer verifies Pinecone Local's documented limitations first and
    scopes tests honestly (anything unsupported by the emulator gets a documented gap, not
    a fake pass).
- DI registration tests in `tests/Rag.NET.Tests/DependencyInjection` (Use* resolves the
  right concrete + capability interfaces; validation throws).
- features.md: three rows ticked; ROADMAP/MILESTONE Phase 1.7 complete → Milestone 1
  backlog done.

## Out of scope

- Weaviate gRPC transport and modules (vectorizers, generative); Weaviate replication
  config.
- Pinecone pod-based indexes; Pinecone inference/hosted-embedding APIs.
- Chroma auth modes beyond a static token header; Chroma full-text/regex search.
- Client-side hybrid fusion emulation for stores lacking native support.
