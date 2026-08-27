---
id: features
title: Feature Backlog
sidebar_position: 2
---

# Rag.NET Feature Backlog

Candidate features for future design and implementation. Completed features are documented in their own pages.

## What a ✅ Done row has to carry, since Milestone 6.0

A status line reading "✅ Done" says the code exists and `FeatureClaimTests` checks it does. Milestone 5
showed that is not enough — `Rag.NET.GraphRag` was Done, green and published with eight defects
that running it once found — so every Done section also carries **`**Exercised by:**`**, one line
saying what runs the real thing, checked by `FeatureExerciseTests`: the kind, a dash, and text
naming the test or benchmark class in backticks. The kinds, and what each is allowed to mean:

| Kind | Means | The bar |
|---|---|---|
| `benchmark` | a measured run on a real corpus with a real model | a figure pinned in a reproduction table at ±0.005, with a control it is differenced against — the GraphRAG method |
| `container` | a Docker-tier suite against the real dependency | the suite is `RequiresDocker` and runs in CI's Docker tier |
| `test` | a fast-tier test that drives the real path | a real file, a real pipeline, a real store — not a fake of it |
| `recorded` | a scrubbed, dated real-service exchange replayed | the recording is committed and its date and version are in it |
| `declared` | cannot be exercised here | the text names what would be needed and what stays unverified |

A Done section without the line is on `FeatureExerciseTests.SectionsAwaitingExercise` under the
Milestone 6 phase that owes it one, and that list must reach empty before v1.0. A section marked
"Delivered" rather than "✅ Done" is outside both guards today — a gap 6.0 recorded and 6.2 closes by
normalising the status lines.

---

## Chunking

### Semantic Chunking (Embedding-Based Boundary Detection)
**Package:** `Rag.NET.Chunking`

Split text by meaning boundaries rather than fixed sizes. Embed each sentence, compute cosine similarity between consecutive sentence embeddings, and break where similarity drops below a configurable percentile threshold (breakpoint detection). Produces chunks that are coherent units of meaning — no more splitting mid-thought.

`SemanticChunkingStrategy` implements three interfaces:
- `IChunkingStrategy` — per-section sentence-level splitting (existing path)
- `IDocumentChunkingStrategy` — document-level section merging: batch-embeds all sections, groups adjacent similar sections, then applies min/max size constraints
- `IChunkRefinementStrategy` — post-processing decorator: passes short chunks through unchanged; re-splits oversized chunks at sentence boundaries

`RagBuilder` registration:

```csharp
// All three interfaces → same SemanticChunkingStrategy instance
services.AddRagNet(rag => rag.UseSemanticChunking());

// Semantic refinement only — pairs with any base chunking strategy
services.AddRagNet(rag => rag
    .UseHierarchicalMerging()
    .UseSemanticRefinement());
```

**Why:** The single biggest quality lever for retrieval. Fixed-size and recursive splitting regularly break mid-paragraph or mid-argument. Semantic chunking ensures each chunk is a self-contained unit of meaning, directly improving retrieval precision.

**Status:** ✅ Done

---

### Hierarchical Merger (Regex-Driven Tree Chunking)
**Package:** `Rag.NET.Chunking`

Configurable chunking stage driven by user-supplied regex patterns for each heading level and an integer hierarchy depth. Builds a heading tree and extracts subtrees up to the specified depth as chunks — each chunk starts with its section heading and contains all body text within that section. Applicable to any document format.

**Why:** Many enterprise document types (legal codes, technical specs, internal wikis) use non-standard heading structures. Regex-driven depth chunking lets operators tune chunking without writing a custom `IChunker`.

**Status:** ✅ Done

---

### Typed Chunk Metadata (Filterable Everywhere, No Per-Key Schema)
**Packages:** `Rag.NET.Abstractions`, `Rag.NET`, all seven vector stores, all data-provider connectors

Metadata values carry their type end to end: `MetadataValue` (string, number, boolean, date) replaces `string` in `FileEntry.Metadata`, `DocumentMetadata.Tags`, `TextChunk.Metadata` and `SearchOptions.MetadataFilter`, and every vector store persists the kind — PgVector as native JSONB types, Qdrant as typed payload values (numbers filter via a closed range), Weaviate as typed auto-schema `meta_*` properties, Chroma and Pinecone as native record values (dates as a `$date:` sentinel), and Azure AI Search as a `metadata_entries` `Collection(Edm.ComplexType)` of `{key, stringValue, numberValue, boolValue, dateValue}` rows — every key filterable with its type, no per-key index schema. A custom data provider can submit a number and filter on it numerically; nothing stringifies it along the way. Backward compatibility: metadata stored before the change reads back losslessly as string-kind values; on Azure AI Search an existing index gains `metadata_entries` additively via `CreateOrUpdateIndexAsync` (no rebuild), but pre-existing documents have no typed rows and stop matching `MetadataFilter` until re-ingested — reads fall back to the legacy JSON blob.

**Why:** `page eq 3` and `page gt 3` need a real number in the index; `IDictionary<string, string>` guaranteed everything arrived as text (#91). Typing only one link of the chain would just move the stringification, so the whole chain changed at once, before anything ships on nuget.org.

**Status:** ✅ Done

---

### Page-Attributed Chunks (Source-Page Citation)
**Packages:** `Rag.NET.Abstractions`, `Rag.NET`, `Rag.NET.Chunking`, `Rag.NET.Chunking.CSharp`, `Rag.NET.Chunking.Templates`, `Rag.NET.Parsers.Vision`

The reserved `page`/`page_end` metadata pair (always written together, as numbers) carries the source-page range from `DocumentSection.PageNumber` through every chunking strategy: per-section strategies stamp both with the section's page, merging strategies (hierarchical merger and its templates, semantic document-level grouping, proposition passages) report min/max across the contributing sections and keep the pages present when a run mixes paginated and unpaginated sections. Absent — not null — for unpaginated formats and where the origin page is unknowable (LLM-rewritten resume fields; video timestamps stay `timestamp_seconds`). Riding on typed metadata (above), the pair is numerically filterable in all six stores with no per-store schema; chunks stored before the keys existed read back without them until re-ingested.

**Why:** For reference checking, "which page did this answer come from" is the difference between a citation a human can verify and a chunk index they cannot (issue #82). PdfPig already supplied the page number; it previously died at the chunking boundary.

**Status:** ✅ Done

**Exercised by:** test — `PdfPageAttributionTests` builds a real two-page PDF, parses it with `PdfDocumentParser` and chunks it through the pipeline's own `ParseBehavior`, asserting each chunk's `page`/`page_end` metadata points at the page its text came from and arrives typed as a number rather than a literal. `DocumentParserTests` covers the read-a-file-this-library-did-not-write case, on committed `sample.pdf` and `sample-table.pdf` fixtures.

---

### Multi-Language Code Splitting (Heuristic)
**Package:** `Rag.NET.Chunking`

Language-specific separator hierarchies for Python, JS/TS, Java, Go, Ruby, Rust, C#, and more — splitting at class/function/method boundaries before falling back to line/block. Works via regex-based heuristics; no compiler infrastructure required. Complements the Roslyn-based C# chunker for other languages.

**Why:** Generic character splitting ignores code structure. Heuristic splitters work for all languages without per-language compiler dependencies.

**Status:** ✅ Done

---

### C# Semantic Chunking (Roslyn)
**Package:** `Rag.NET.Chunking.CSharp`

Split C# source files into semantically meaningful chunks using Roslyn. Each chunk maps to a single code construct — class, method, interface, enum, delegate, constructor, etc. — and carries: kind, namespace, parent type, identifier name, XML doc summary, source text, and a dependency list (parameter types, base types, property types) for graph-aware retrieval. `CSharpChunkingOptions` controls whether member bodies, private members, and internal members are included.

**Why:** Generic text chunking splits code mid-method or mid-class, destroying semantic meaning.

**Status:** ✅ Done

**Exercised by:** test — `RealSourceFileChunkingTests` chunks this library's own `CSharpChunkingStrategy.cs` through Roslyn: production C# with file-scoped namespaces, primary constructors and raw string literals, none of which the inline-source cases in `CSharpChunkingStrategyTests` contain. It asserts the file splits into members and that no chunk is the whole file — the shape the strategy returns when a parse fails, which is the failure an inline-source test can never trip.

---

### Domain-Specific Chunking Templates
**Status:** ✅ Done
**Package:** `Rag.NET.Chunking.Templates`

Pre-built chunking templates for common vertical document types:

- **Academic Papers** — two-column layout detection, index from abstract, filter front matter
- **Legal Documents** — detect numbered clause hierarchy, merge sub-clauses under parent article
- **Q&A Pairs** — ingest CSV/Excel rows as question (chunk) + answer (payload) pairs
- **Books** — hierarchical merge with table-of-contents removal
- **Email** — chunk header/body/attachment sections; `EmailChunkingOptions.IncludeHeaders` and `.IncludeAttachments` (both default `true`) skip the `headers` and `attachment:*` sections entirely when disabled
- **Resumes** — parallel LLM extraction of basic info, work history, and education sections

**Why:** Domain templates dramatically reduce noise. Legal documents chunked generically lose clause hierarchy; academic papers mix references into the body. Pre-built templates serve vertical markets out of the box.

> **Breaking change (Phase 4.2) — `UseEmailChunking()` no longer registers a parser.** It used to
> bundle its own `EmailTemplateDocumentParser`, which duplicated `Rag.NET.Parsers.Email`'s
> `EmailDocumentParser` (see [Email File Parser](#email-file-parser-eml--msg) below) — both claimed
> `message/rfc822`, which was a startup error the moment both packages were registered together.
> The duplicate is retired outright rather than resolved: `.eml` ingestion alongside this chunking
> strategy now needs `Rag.NET.Parsers.Email` added separately (`AddEmailParser()`). The chunking
> strategy itself is unaffected either way — it consumes `DocumentSection`s and does not care which
> parser produced them.
>
> **Enabling QA-pairs chunking makes plain CSVs (and Excel workbooks, if `Rag.NET.Parsers.Office`
> is installed) parse as QA pairs.** `UseQAPairsChunking()` registers `QAPairsDocumentParser` via
> `AddParser<QAPairsDocumentParser>(replacesTypeNames: [...])`, declaring a deliberate override
> against core's `CsvDocumentParser` (`text/csv`) and, by type name so this package takes no
> compile-time dependency on the optional one, `Rag.NET.Parsers.Office`'s `ExcelDocumentParser`
> (`…spreadsheetml.sheet`). That is the override doing its job, not a bug: a caller who asked for
> QA-pairs chunking wants that parser to win, and `AddParser<TParser>(replaces:)` removes the
> replaced parser's registration rather than merely silencing the conflict, so the override
> actually wins parser selection instead of losing to whichever built-in registered first. It is
> also the *opposite* of the previous default, where the collision was undetected and core's
> `CsvDocumentParser` silently won every time — see [Parsers — content-type ownership and the
> claim model](../guide/ingestion.md#content-type-ownership-and-the-claim-model). Pass
> `registerParser: false` to `UseQAPairsChunking()` to take the chunking strategy without the
> parser or its override.

---

## Retrieval

### Self-Query / Metadata Filter Generation
**Package:** `Rag.NET` (core)

Use an LLM to translate a natural-language question into a vector search query plus a structured metadata filter expression (e.g., "2023 finance reports" → semantic query + `year=2023 AND category='finance'`). Requires an `AttributeInfo` schema describing which metadata fields and types are available.

**Why:** Rag.NET has no mechanism to automatically derive metadata filters from user questions.

**Status:** ✅ Done

---

### Tag-Based Retrieval Filtering
**Package:** `Rag.NET` (core)

Maintain a "tag knowledge base" of content-tag pairs. At query time, match the user's question against all known tags via hybrid search and inject top-k matching tags as keyword filters for the primary retrieval. Two-stage funnel: tag filtering narrows candidates before full semantic search.

**Why:** Lightweight scoping alternative to self-query — useful when documents carry human-assigned categories (product names, departments, issue types) without requiring an LLM call.

**Status:** ✅ Done

---

### Time-Weighted Retrieval
**Package:** `Rag.NET` (core)

Combine semantic similarity score with a recency decay factor. Fresher documents keep their original score, older ones decay toward zero. Configurable decay rate (`DecayRate`, zero or positive). Valuable for knowledge bases where recency matters (support docs, regulatory updates, news).

**Why:** Pure semantic similarity ignores document age entirely.

**Status:** ✅ Done

---

### BM25 Synonym Expansion
**Package:** `Rag.NET` (core)

Augment BM25 retrieval with runtime-updatable domain-specific synonym dictionaries (e.g., "MI" → "myocardial infarction", "k8s" → "kubernetes"). Synonyms are bidirectional: any term in a group expands to all other terms. Dictionary updatable at runtime without restart via `SynonymMap.AddGroup` / `RemoveGroup`.

**Why:** Domain terminology mismatches silently reduce BM25 recall in specialised corpora (medical, legal, engineering).

**Status:** ✅ Done

**Performance** (BenchmarkDotNet, .NET 10, i9-12900HK, Release):

*Index time (`Add`)*

| Scenario | No synonyms | +10 single-word groups | +100 single-word groups | +10 phrase groups |
|---|---|---|---|---|
| Short text (~40 tokens) | 2.9 µs | 3.5 µs | 3.5 µs | — |
| Medium text (~200 tokens) | 7.6 µs | 11.9 µs | 12.1 µs | 52 µs |
| Long text (~800 tokens) | 29 µs | 48 µs | 55 µs | — |

*Query time (`Search`, 50-doc index)*

| Scenario | No synonyms | +10 groups | +100 groups |
|---|---|---|---|
| Query expansion | 3.6 µs | 4.6 µs | 5.3 µs |

Synonym expansion overhead is sub-linear for single-word groups (phrase-scan window bounded by `SynonymMap.MaxKeyTokenCount`). Multi-word groups (e.g. `"heart attack"`) engage the phrase-scan and add cost proportional to the longest phrase length × token count.

---

### Ensemble / Reciprocal Rank Fusion (RRF)
**Package:** `Rag.NET` (core)

Combine results from multiple retrievers (e.g., BM25 + dense vector) using Reciprocal Rank Fusion with configurable per-retriever weights. Unlike Rag.NET's current hybrid search (tied to Azure AI Search), RRF works across all vector stores and allows mixing any two retrieval strategies.

**Why:** RRF consistently outperforms individual retrievers by combining rank signals.

**Status:** ✅ Done. `EnsembleBehavior` fuses dense/BM25/(sparse) arms client-side with weighted RRF. Since native-hybrid dispatch landed, a store implementing `IHybridSearchable` (Azure AI Search, Weaviate) serves the hybrid call server-side instead **when nothing native fusion cannot express is configured** — supplying `EnsembleOptions`, a non-zero `MinScore`, or an active sparse arm keeps the client-side ensemble. The chosen path is observable via the `retrieval.hybrid.path` activity tag.

---

### RAPTOR — Recursive Abstractive Tree Summarization
**Status:** ✅ Done
**Package:** `Rag.NET.Raptor`

Embed chunks, dimensionality-reduce with UMAP, soft-cluster with a Gaussian Mixture Model (BIC selects optimal cluster count), then LLM-summarize each cluster into a new higher-level chunk. Recurse until a level can no longer be usefully split, building a full summary tree — the top level always retains at least two nodes; a level whose cluster count would not shrink the level below it is rejected rather than collapsed to a single cluster. Store all intermediate summary chunks alongside originals; all levels participate in retrieval simultaneously. Default `TreeScope` is `Corpus` — the tree is built over every ingested document, not one at a time (#331) — debounced on growth via `CorpusGrowthThreshold` and rebuildable on demand via `RaptorTreeRebuilder`; `PerDocument` remains available and is the control arm Phase 6.2.1 differences the corpus scope against.

**Why:** Enables retrieval at multiple granularities — high-level theme queries match cluster summaries, fine-grained questions match leaf chunks. Essential for long documents (books, reports, legal corpora) where a flat chunk pool is insufficient.

**Exercised by:** benchmark — `MultiHopRagAnswerReproduction` pins four arms measured 2026-08-25 over the full 609-article MultiHop-RAG corpus with `openai/gpt-4o-mini` at temperature 0, top-6 context, over the 2,255 judged queries: `raptor` 0.3734 (per-document control), `raptorcorpus` 0.3588 (the shipped default), `raptorfiltered` 0.3499 (the validation gate — it reproduces the dense arm's pinned 0.3499/0.2603/0.3242 to four decimals), `raptorboost` 0.3450. **The measured finding is that corpus scope is 0.0146 worse than the per-document tree it replaced** (McNemar p=0.0247 paper, p=0.0006 raw), the whole gap being inference queries (0.7831 against 0.8309) — the opposite of the rationale for making it the default. The default stays `Corpus` as a hold pending a second corpus, because MultiHop-RAG rewards per-document locality by construction; see `docs/guide/raptor.md`'s Measured section. Also exercised at the integration level by `RaptorTreeScopeTests` and `RaptorCorpusBuildTests` over a real `SqliteRaptorLeafStore`, with `SqliteRaptorLeafStoreTests` proving it survives a reopen.

---

### Deep Research Loop (Sufficiency-Gated Sub-Query Decomposition)
**Package:** `Rag.NET` (core)

After initial retrieval, use an LLM to judge whether the retrieved information is sufficient. If not, generate follow-up sub-queries (`SubQueryCount`, greater than 0) and explore them recursively to a configurable depth (`MaxDepth`, greater than 0). Merge and deduplicate results across all branches.

**Why:** Answers complex questions that require discovering what is missing and forming follow-up questions — moves Rag.NET from single-pass retrieval toward autonomous research capability.

**Status:** ✅ Done

---

## Post-Retrieval

### Cohere Rerank
**Status:** ✅ Done

**Package:** `Rag.NET.Reranking.Cohere`

Call Cohere's hosted reranking API as a post-retrieval step. `CohereReranker` batches candidate chunks against the user query, scores each with Cohere's cross-encoder model, and returns the top-N results by relevance score. When the candidate list exceeds `MaxDocumentsPerBatch`, calls are issued sequentially and results are merged before final ranking. No local model hosting or GPU required.

**Why:** Highest-quality managed reranking with a simple API key — no GPU required.

**Options**

| Option | Default | Description |
|---|---|---|
| `ApiKey` | *(required)* | Cohere API key. |
| `Model` | `rerank-english-v3.0` | Reranking model. Use `rerank-v3.5` for multilingual workloads. |
| `TopN` | `5` | Number of top results to return after reranking. |
| `ReturnDocuments` | `false` | Whether Cohere echoes document text back in the response. |
| `MaxDocumentsPerBatch` | `1000` | Maximum documents per API call (Cohere hard limit). Larger lists are batched sequentially. |
| `Endpoint` | `null` | Optional API endpoint override. Useful for testing with a local stub server. |

**Usage**

```csharp
rag.UseCohereReranking(o =>
{
    o.ApiKey = configuration["Cohere:ApiKey"]!;
    // o.Model = "rerank-v3.5"; // multilingual
    o.TopN  = 5;
});
```

---

### ONNX Cross-Encoder Reranking (Local)
**Status:** ✅ Done
**Exercised by:** benchmark — the +reranker ablation cell on SciFact, FiQA and ArguAna in `BeirAblationTests`, pinned in `BeirReproduction` at ±0.005; the run that found `OnnxReranker` mapping 26% of every document to `[UNK]` (Phase 3.15) and now guards the fix. Recall@10 is frozen by construction there — the cell permutes only the top-10 it is evaluated on — which the Milestone 6.2.1 re-measure under the Real protocol will lift.

**Package:** `Rag.NET.Reranking.Onnx`

Run a BERT-based cross-encoder reranker fully locally via `Microsoft.ML.OnnxRuntime`. `OnnxReranker` tokenises each query-passage pair using a BERT whitespace tokeniser, runs inference through the ONNX model, and ranks results by the sigmoid-transformed logit score. No API key or network access required — suitable for air-gapped environments or cost-sensitive deployments.

**Why:** Highest-quality reranking without API cost or data-egress concerns; works offline with any ONNX-compatible cross-encoder model (e.g., `ms-marco-MiniLM-L-6-v2` exported to ONNX).

**Options**

| Option | Default | Description |
|---|---|---|
| `ModelPath` | *(required)* | Path to the `.onnx` cross-encoder model file. |
| `VocabPath` | *(required)* | Path to the BERT `vocab.txt` vocabulary file. |
| `MaxLength` | `512` | Maximum token sequence length; query + passage pairs are truncated to this limit. |

**Usage**

```csharp
services.AddRagNet(rag => rag
    .UseOnnxReranking(o =>
    {
        o.ModelPath = "models/cross-encoder.onnx";
        o.VocabPath = "models/vocab.txt";
        o.MaxLength = 512;
    }));
```

---

## Answer Generation

### Map-Reduce Synthesis
**Package:** `Rag.NET.AnswerEngines`

Answer questions over large document sets by first mapping an LLM call over each retrieved chunk (partial answers), then reducing with a second LLM call into a final answer. Handles cases where retrieved text collectively exceeds the model's context window. Rag.NET's `AskAsync` currently stuffs all chunks into a single context.

**Why:** Essential for long-document and large-corpus RAG workloads.

**Status:** ✅ Done

---

### Refine (Iterative Synthesis)
**Package:** `Rag.NET.AnswerEngines`

Process chunks sequentially: generate an initial answer from the first chunk, then iteratively refine by feeding each subsequent chunk plus the running answer to the LLM. More token-efficient than map-reduce for sequential coherence tasks.

**Why:** Handles context-window overflow gracefully with a different trade-off profile than map-reduce.

**Status:** ✅ Done

---

## Document Enrichment

### LLM Metadata Extraction at Ingest
**Package:** `Rag.NET` (core)

Run an LLM over each ingested document to generate representative Q&A pairs or structured metadata tags (topics, entities, document type) and attach them to chunks. At retrieval time, user questions match against these pre-generated questions, yielding much better recall than embedding raw chunk text alone.

**Why:** One of the highest-impact RAG accuracy improvements — applied at index time, zero retrieval overhead.

**Status:** ✅ Done

---

## Indexing Infrastructure

### Content-Hash Record Manager
**Status:** ✅ Done
**Package:** `Rag.NET` (core)

Track which document content hashes have been written to which vector store namespace, persisted to a SQL/file store. On re-ingestion: skip truly unchanged documents, re-index modified ones, optionally delete documents whose sources have disappeared (`CleanupMode.Full`). Goes beyond `IngestionOptions.Overwrite` — that flag re-ingests unconditionally; this skips unchanged content.

**Why:** Critical for efficient incremental indexing of large corpora.

---

## Vector Stores

### Weaviate Vector Store
**Package:** `Rag.NET.VectorStores.Weaviate`

Implement `IVectorStore` and `ICollectionManageable` backed by Weaviate via a hand-rolled REST + GraphQL client (`ZeroAlloc.Rest`; no maintained first-party .NET client exists). Supports hybrid search (BM25 + vector), metadata filtering via Weaviate's `where` filter, and multi-tenancy. Registration: `.UseWeaviate(endpoint, className, vectorDimensions)`.

**Why:** Weaviate is a popular managed vector store with native hybrid search and a generous free tier. Adds a third open-source option alongside PgVector and Qdrant.

**Status:** Delivered. `WeaviateVectorStore` (register with `UseWeaviate(endpoint, className, vectorDimensions, configure?)`) serves `IVectorStore`, `IHybridSearchable` (native BM25+vector relative-score fusion — the second store with native hybrid after Azure AI Search), and `ICollectionManageable` from one singleton. REST handles schema/batch writes, a single GraphQL POST handles search (arguments inlined — Weaviate's GraphQL rejects variables for its custom scalar types). Deterministic object ids per `(DocumentId, ChunkIndex)` make re-ingestion replace chunks; metadata keys become filterable `meta_*` properties via auto-schema with `Equal`/`And` `where` composition; dense scores map `1 - distance/2` (identical vector ⇒ 1), hybrid scores are Weaviate's 0..1 fusion scores. Optional `Tenant` creates the class multi-tenancy-enabled and scopes every read/write. Tested against the official image via Testcontainers.

---

### Chroma Vector Store
**Package:** `Rag.NET.VectorStores.Chroma`

Implement `IVectorStore` backed by ChromaDB via its REST API. Chroma is the most widely used embedded/local vector store in Python RAG tutorials — a .NET adapter lowers the barrier for teams already running Chroma.

**Why:** Chroma is commonly used in prototyping and local development. A lightweight adapter makes Rag.NET accessible to teams already invested in Chroma.

**Status:** Delivered. `ChromaVectorStore` (register with `UseChroma(endpoint, collectionName, configure?)`) serves `IVectorStore` and `ICollectionManageable` from one singleton — deliberately the lightweight dense-only adapter (no hybrid/sparse; the pipeline's BM25 fallback applies). Hand-rolled `ZeroAlloc.Rest` client against the REST v2 API (`/api/v2/tenants/{tenant}/databases/{database}/...`, defaults overridable via options, optional Bearer token). Record ids `{documentId}:{chunkIndex}` make re-ingestion upsert-replace; chunk text is the record document, metadata is stored as-is plus `document_id`/`chunk_index` and filtered server-side with `$eq`/`$and`. Collections are created with the cosine space (dimensions inferred by Chroma on first upsert) and addressed by UUID: the name→UUID resolution is cached and transparently re-resolved once when the collection is recreated behind the store's back. Scores map `1 - cosine distance` (identical vector ⇒ 1). Tested against the official image via Testcontainers.

---

### Pinecone Vector Store
**Package:** `Rag.NET.VectorStores.Pinecone`

Implement `IVectorStore` backed by Pinecone's serverless index via the official REST API. Supports namespace-based collection isolation (maps to `collectionName`), metadata filtering, and sparse-dense hybrid search via Pinecone's native sparse vectors.

**Why:** Pinecone is the dominant managed vector store in production enterprise deployments. Many teams choose Rag.NET for the pipeline but already have Pinecone in their stack.

**Status:** Delivered. `PineconeVectorStore` (register with `UsePinecone(apiKey, indexName, vectorDimensions, configure?)`) serves `IVectorStore` and `ICollectionManageable` from one singleton on the official `Pinecone.Client` SDK — pinned to 3.1.0 because the 4.x control-plane models cannot deserialize Pinecone Local's responses (upstream #54; SDK repo archived). `ICollectionManageable` manages serverless indexes (cloud/region options, readiness-polled create, idempotent delete); record ids `{documentId}:{chunkIndex}` make re-ingestion upsert-replace; chunk text lives in record metadata next to `document_id`/`chunk_index` and is read back into results (~40 KB metadata cap per record). Native cosine scores with `MinScore` applied directly, `$eq`/`$and` server-side metadata filters, and optional `Namespace` scoping every operation. Delete-by-document uses list-ids-by-prefix + delete-by-ids (serverless rejects delete-by-metadata-filter) with an exact-document guard for ids containing `:`. Opt-in `EnableSparseVectors` registers `PineconeSparseVectorStore : ISparseSearchable` (Qdrant type-split precedent): sparse values ride on the same records, dotproduct metric enforced fail-fast, sparse-only queries via a zero dense vector. Tested against Pinecone Local via Testcontainers (sparse round-trip skipped there — the emulator drops sparse values on dense indexes; documented in the guide).

---

## Ingestion Sources

### Data Provider Abstraction
**Status:** ✅ Done
**Package:** `Rag.NET` (core) + `Rag.NET.DataProviders.GitHub`

Decouple "where files come from" from "how to ingest them" via an `IFileContentProvider` abstraction.

- `LocalFilesDataProvider` — scans a local directory, filters by extension and `IgnoreFile` predicate
- `GitHubFilesDataProvider` — fetches files from a GitHub repository via Octokit; supports recursive traversal, extension filtering, and delta ingestion via `LastIngestedCommitSha` watermark

```csharp
await pipeline.IngestFromProviderAsync(provider, source, metadata, options);
```

**Why:** Enables batch and incremental ingestion workflows without custom glue code.

---

### Recursive Web Crawler
**Status:** ✅ Done
**Package:** `Rag.NET.DataProviders.Web`

Fetch a seed URL and follow links up to a configurable depth, loading all discovered pages as documents.

**Why:** Covers the common "index all docs on this site" use case without manual URL enumeration.

---

### Sitemap Loader
**Status:** ✅ Done
**Package:** `Rag.NET.DataProviders.Web`

Read a `sitemap.xml` and load all listed URLs. A structured, polite alternative to recursive crawling for sites that publish sitemaps.

URLs can be skipped by prefix or by regular expression via `SitemapOptions` (issue #252): a large
site's sitemap routinely lists sections nobody wants ingested. Excluding a `<sitemapindex>` link
prunes every page under it *without fetching it* — on by default, and switchable off for an index
partitioned by something unrelated to the URLs inside it.

**Why:** Simpler and more reliable than link-following for well-maintained sites.

---

### RSS Feed Loader
**Status:** ✅ Done
**Package:** `Rag.NET.DataProviders.Web`

Ingest documents from RSS/Atom feeds, enabling near-real-time ingestion of news, blog posts, and update streams.

**Why:** Easy-to-implement, high-utility source for continuously updated knowledge bases.

---

### SaaS Connectors
**Package:** Various `Rag.NET.DataProviders.*`

Production connectors for cloud and enterprise systems, each exposing `IFileContentProvider` with delta sync where the platform supports it. Each connector is an independent package and implementation task.

**Why:** Enterprise customers store knowledge in Confluence, Notion, SharePoint, and Slack — not on disk. Without connectors, every enterprise deployment requires a custom integration layer.

#### Group 1 — Cloud Storage

**Status:** ✅ Done

| Package | SDK | Delta sync |
|---|---|---|
| `Rag.NET.DataProviders.AzureBlob` | `Azure.Storage.Blobs` | ETag / `LastModified` watermark |
| `Rag.NET.DataProviders.Microsoft365` | Microsoft Graph SDK (SharePoint connector) | `deltaLink` token |
| `Rag.NET.DataProviders.Microsoft365` | Microsoft Graph SDK (OneDrive connector) | `deltaLink` token |
| `Rag.NET.DataProviders.GoogleDrive` | `Google.Apis.Drive.v3` | `pageToken` change stream |
| `Rag.NET.DataProviders.Dropbox` | `Dropbox.Api` | cursor-based delta |
| `Rag.NET.DataProviders.Box` | `Box.V2` | events cursor |

#### Group 2 — Collaboration

**Status:** ✅ Done

| Package | SDK | Delta sync |
|---|---|---|
| `Rag.NET.DataProviders.Confluence` | Confluence REST API + CQL | `lastModified` filter |
| `Rag.NET.DataProviders.Notion` | Notion REST API | `last_edited_time` filter |
| `Rag.NET.DataProviders.Jira` | Jira REST API + JQL | `updated >` JQL clause |
| `Rag.NET.DataProviders.Asana` | Asana REST API | sync token |
| `Rag.NET.DataProviders.Airtable` | Airtable REST API | `filterByFormula` on modified time |

#### Group 3 — Communication

**Status:** ✅ Done

| Package | SDK | Delta sync |
|---|---|---|
| `Rag.NET.DataProviders.Slack` | Slack Web API | cursor + `oldest` timestamp |
| `Rag.NET.DataProviders.Microsoft365` | Microsoft Graph SDK (Teams connector) | `deltaLink` token |
| `Rag.NET.DataProviders.Gmail` | MailKit (IMAP) | UID watermark |

#### Group 4 — Source Control

**Status:** ✅ Done

| Package | SDK | Delta sync |
|---|---|---|
| `Rag.NET.DataProviders.GitLab` | `GitLabApiClient` | compare API (same pattern as GitHub) |
| `Rag.NET.DataProviders.Bitbucket` | Bitbucket REST API | compare API |

#### Group 5 — Support

**Status:** ✅ Done

| Package | SDK | Delta sync |
|---|---|---|
| `Rag.NET.DataProviders.Zendesk` | Zendesk REST API | incremental export cursor |

---

### Webhook / Event-Driven Ingestion
**Packages:** `Rag.NET.DataProviders` (queue, processor, polling), `Rag.NET.Api` (webhook endpoint), `Rag.NET.Ingestion.AzureServiceBus` (Service Bus trigger)

**Status:** ✅ Done — all three triggers delivered (webhook, polling, Azure Service Bus). Provider-specific payload parsers (GitHub/Notion/Slack) remain deferred; the pluggable `IWebhookPayloadParser` is the seam for those.

Producers push `IngestionJob`s (byte payload + metadata) onto a bounded `IIngestionJobQueue` (`ChannelIngestionJobQueue`, `BoundedChannelFullMode.Wait` backpressure, capacity via `EventDrivenIngestionOptions.QueueCapacity`); the `IngestionJobProcessor` `BackgroundService` drains it into `IIngestor.IngestAsync` with per-job failure isolation. Registered via `UseEventDrivenIngestion`. Triggers:

- `MapRagNetWebhooks` (`Rag.NET.Api`) — minimal API POST endpoint verified by HMAC-SHA256 over the raw body (timing-safe, `sha256=` prefix tolerated, exempt from API-key auth); payloads parsed by the pluggable `IWebhookPayloadParser` (generic `{documentId, content, metadata?}` parser shipped). The only trigger that uses the job queue
- `BackgroundPollingTrigger` + `UsePollingIngestion` — wraps any `IFileContentProvider` and re-runs `IngestFromProviderAsync` (hash-skip preserved) on a configurable interval; each registration is an independent poller. Interval only — cron/NCrontab deferred
- `AzureServiceBusIngestionTrigger` + `UseServiceBusIngestion` (`Rag.NET.Ingestion.AzureServiceBus`) — `IHostedService`/`IAsyncDisposable` over `ServiceBusProcessor` (or `ServiceBusSessionProcessor`), consuming a queue or topic subscription. **Bypasses `IIngestionJobQueue` by design** and calls `IIngestor.IngestAsync` itself: routing a durable broker message through an in-memory channel and settling it would convert at-least-once into at-most-once on crash. Settles on the outcome — complete / abandon for redelivery / dead-letter with a fixed `DeadLetterReasons` value plus a variable description — which makes it the **first and only ingestion path with a DLQ** (`IngestionJobProcessor` logs a failed job at Warning and drops it). Opt-in sessions give per-document FIFO with `SessionId` required to equal `documentId`; same JSON payload contract as the webhook, narrowed to reject arrays. Both credential shapes: connection string and `TokenCredential`

**Why:** The current data providers are pull-only — a scheduler or human must kick off re-ingestion. Event-driven ingestion keeps the index current without polling overhead or operator intervention.

---

### Email Connectors (Outlook / Exchange)
**Package:** `Rag.NET.DataProviders.Microsoft365` (Exchange connector)

Ingest emails and attachments from Outlook/Exchange via Microsoft Graph (`/users/{mailbox}/mailFolders/{folder}/messages`, app-only auth). Emits raw RFC 822 `.eml` entries (Graph `$value`), so a registered `AddEmailParser()` parses subject/body and delegates attachment parsing to the existing parsers (PDF/Word/text/…). Supports folder filtering, a `receivedDateTime` watermark (`GetDeltaToken()`), and `MaxResults` capping. Complements the existing Gmail connector.

**Why:** Exchange/Outlook is the dominant enterprise email system. Enterprise RAG over internal communications requires both Gmail and Exchange coverage.

**Status:** ✅ Done

---

### Linear Issue Tracker
**Package:** `Rag.NET.DataProviders.Linear`

Ingest issues and comments from Linear via the GraphQL API (`POST /graphql`, the repo's first GraphQL connector — built on the existing ZeroAlloc.Rest POST-with-body pattern, no new client dependency). Issues are exported as Markdown with state/project/assignee and comment attribution. Supports team filtering, state-type filtering (`triage`/`backlog`/`unstarted`/`started`/`completed`/`canceled`), and delta ingestion via an `updatedAt` watermark (`GetDeltaToken()`; advances only on a complete traversal since Linear does not document pagination sort direction).

**Why:** Linear is the issue tracker of choice for many engineering teams. Ingesting it alongside GitHub and Jira gives complete engineering knowledge coverage.

**Status:** ✅ Done

---

## Multimodal Ingestion

### Image Description via Vision LLM
**Package:** `Rag.NET.Parsers.Vision`
**Status:** ✅ Done

For image files (PNG, JPG, etc.) and embedded figures in PDFs/DOCX: if OCR yields too little text, call a vision LLM (e.g., GPT-4o) to generate a natural-language description. Inject the description as a chunk adjacent to surrounding document text with position metadata. A context-aware variant passes surrounding paragraph text to ground the description.

**Why:** Technical documents convey critical information in diagrams and charts that text-only parsers silently discard.

---

### Video Description via Vision LLM
**Package:** `Rag.NET.Parsers.Vision`
**Status:** ✅ Done

Pass video files (MP4, MOV, MKV) to a vision LLM that generates a textual description of the content, stored as chunks for retrieval.

**Why:** Video content (demo recordings, training videos, presentations) is otherwise invisible to RAG pipelines.

---

### Audio Transcription
**Package:** `Rag.NET.Parsers.Audio`

Transcribe WAV, MP3, FLAC, OGG, and other audio files using [Whisper.net](https://github.com/sandrohanea/whisper.net) — a native .NET binding to OpenAI's Whisper model that runs fully local with no API key. Model size is configurable (`tiny` → `large`) to trade accuracy for speed and memory.

**Why:** Meeting recordings, podcasts, and voice notes are a growing source of enterprise knowledge that text-only pipelines cannot reach.

**Status:** ✅ Done

---

## Document Parsing

### PDF Table Extraction
**Package:** `Rag.NET.Parsers.Pdf`
**Status:** ✅ Done — pure-geometry heuristic over PdfPig word boxes (Y-band row clustering + persistent X-gap column detection), on by default (`PdfParserOptions.ExtractTables`); tables emit as pipe-delimited Markdown sections with `Heading = "table"`, prose interleaved in document order; conservative guards bail to prose (per-page only, tight-gutter/long-cell/2-3-column-layout runs degrade to prose — see the ingestion guide).

**Exercised by:** test — `DocumentParserTests` parses a real `sample-table.pdf` and asserts the table's own rows survive as pipe-delimited text (`| Alice | 30 | Paris |`) in a section headed `table`, with the surrounding prose still emitted separately. Content from the file, not shape: a parser that found a table and lost its cells would fail this.

Detect and extract tables from PDFs as structured text rather than flowing prose. Use heuristic line/column detection (via PdfPig's geometry primitives) to reconstruct table rows as pipe-delimited Markdown tables. Each table becomes its own `DocumentSection` with `Heading = "table"` so chunking and retrieval can treat them distinctly.

**Why:** The current PDF parser treats all content as flowing text — tables become garbled sequences of cell values with no row/column structure. This is a known quality gap for financial reports, legal contracts, and technical specifications.

---

### OCR for Scanned PDFs
**Package:** `Rag.NET.Parsers.Pdf`, `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence`
**Status:** ✅ Done — two engines, both triggered when a page's extracted text falls below `OcrMinCharacters` and both losslessly degrading to the plain-text path on failure. **Tesseract**: per-image, local, and **source-build only — the published `Rag.NET.Parsers.Pdf` package compiles the engine out**. `EnableOcr` is an MSBuild property of this repository's own build (`dotnet build -p:EnableOcr=true` on a source checkout; mirrors `Rag.NET.Parsers.Vision`), deliberately, so package consumers do not carry Tesseract's native payload; setting `UseOcrFallback = true` against the published package throws an instructive error at parser construction that points at Azure Document Intelligence instead. In a gate-on source build it OCRs embedded images largest-first into `Heading = "ocr"` sections; vector-only scanned pages degrade to plain text — no rasterizer dependency. **Azure Document Intelligence**: whole-document, ungated, registered with `UseAzureDocumentIntelligenceOcr(endpoint, credential)` (`AzureKeyCredential` or `TokenCredential`); one call per document, server-side rasterization, `prebuilt-read` by default. Configuring both is a registration-time error. Azure bills every page of the submitted document, so spend is capped by `MaxOcrPages` (default 200) and recorded to `ICostLedger` as a `CostKind.Ocr` entry with `Pages` and zero tokens — which counts toward `UseCostBudgeting`'s window but emits no `ragnet.llm.*` telemetry (`CostAccounting` is internal to `Rag.NET`).

Add an OCR pass for PDFs where `PdfPig` extracts no text (scanned documents). Integrate `Tesseract` (via `Tesseract.Net`) or delegate to `Azure Document Intelligence` for higher accuracy. Falls back automatically when text extraction yields fewer than a configurable minimum character count per page.

**Why:** A significant portion of enterprise PDFs are scanned — contracts, invoices, legacy reports. The current parser silently produces empty sections for these, with no indication to the caller.

---

### EPUB Parser
**Package:** `Rag.NET.Parsers.Epub`

Parse EPUB files (e-books, exported docs from tools like Notion, Bear, Obsidian) into `DocumentSection` objects by chapter/spine item. Extracts embedded HTML via `VersOne.Epub` and delegates to `HtmlDocumentParser` per chapter.

**Why:** EPUB is common for exported documentation, e-books, and long-form content. There's no parser today.

**Status:** ✅ Done

**Exercised by:** test — `DocumentParserTests` parses a real `sample.epub` through `VersOne.Epub` and the real `HtmlDocumentParser`, asserting chapter text from inside the archive (`Integration Test Document`, `Second Chapter`) rather than a section count.

---

### Email File Parser (EML / MSG)
**Package:** `Rag.NET.Parsers.Email`

Parse `.eml` (RFC 5322) and `.msg` (Outlook) files into sections: subject → heading, body → text, attachments dispatched to the registered parser by content type. Uses `MimeKit` for EML and `MsgReader` for MSG.

**Why:** Email archives are a major enterprise knowledge source. The existing Gmail/Exchange connectors ingest live mailboxes, but `.eml`/`.msg` exports from archives or migrations are unaddressed.

An embedded or forwarded message is parsed in place and carries the parent's `DocumentId`, distinguished by a composed file name — `parent.eml#Forwarded Subject.eml`. Depth is bounded by `EmailParserOptions.MaxEmbeddedDepth` (default `3`, hard ceiling `64`) and total fan-out by `MaxEmbeddedMessages` (default `50`); exceeding either logs a warning and skips that branch rather than throwing. The ceiling is not adjustable — it is a safety bound on how much work a crafted file can ask for, not a preference, so the `MaxEmbeddedDepth` setter clamps to it and `AddEmailParser` throws on a larger value rather than clamping silently.

> **Changed in 3.6 — embedded-attachment names may differ.** The composed stem now goes through the shared `FileNameSanitizer` instead of a private copy. Four things follow. Long subjects keep **128** characters instead of 64, so a name that used to truncate mid-word no longer does. A subject made entirely of invalid characters (`"///"`) now yields `embedded-message` instead of `___`. A subject ending in a non-breaking space followed by a dot no longer keeps that space — the old single-pass `TrimEnd('.', ' ')` stripped the dot and left whitespace it could not match. And the two sanitizers order their steps oppositely: the old copy trimmed before replacing invalid characters, the shared one replaces first. TAB, LF, VT, FF and CR are control characters *and* whitespace, so one at the start or end of a subject is now turned into `_` — which is not whitespace, so trimming cannot remove it. `"report\t"` yields `report_` where it used to yield `report`. Most visible via `.msg`, whose subject comes straight from MAPI with no header normalization. Only the stem changes; the `parent.eml#child.eml` composition, the `#` separator and the `embedded-message` fallback for a missing subject are unchanged. The composed name stays inside the parse: it is written to the embedded message's `DocumentMetadata.FileName` and read only as the prefix for the next level's name. `DocumentSection` has no file-name field, so the name reaches no section, tag, log message or stored chunk — nothing downstream is keyed on it.

**Status:** ✅ Done (EML via MimeKit, MSG via MsgReader; embedded/forwarded messages followed since 2.1, bounded by depth and node caps — traversed depth-first over an explicit stack rather than by recursion since 3.9, so nesting depth costs heap and the emitted section order is unchanged)

**Exercised by:** test — `DocumentParserTests` parses a real `.eml` and a real `.msg`, the two formats being separately parsed rather than one converted to the other, asserting body text out of each.

---

### Archive Parser (ZIP)
**Package:** `Rag.NET.Parsers.Archive`

Parse `.zip` archives by decompressing each entry and handing it to the registered parser for its content type, the same way the email parsers dispatch attachments. Register with `AddArchiveParser()`. Entry names compose as `archive.zip#report.pdf`, mirroring the `parent.eml#child.eml` convention; directory entries and zero-length entries are skipped; an entry no registered parser claims is warn-and-skipped exactly as an unclaimed attachment is; and an entry whose parser throws costs that entry rather than the archive.

**Why:** A zipped attachment on an email is a common enterprise shape, and before this it was silently dropped. The attachment dispatcher found no parser for `application/zip`, logged "No parser registered for attachment content type", and yielded nothing — so the archive's contents never reached the index, and the only signal was a warning line. The same was true of a `.zip` ingested directly.

**Claims `application/zip` and `application/x-zip-compressed`** — the second is what older Windows and Internet Explorer emit and is common in real mail — **and deliberately nothing else.** Not `application/epub+zip`: an EPUB *is* a zip, but `EpubDocumentParser` owns it and a generic zip parser answering it would emit entry-by-entry rubbish instead of chapters. Not `application/octet-stream`: nothing format-specific may answer "unknown binary", the invariant Phase 3.11 made load-bearing. Both exclusions are enforced rather than intended — `AddArchiveParser()` declares one `ParserClaim` per claimed type and the startup conflict guard fails on any overlap, so a future change that over-claims fails at registration rather than at ingestion.

Three constraints, because an archive is the first parser to take an untrusted structure that can *expand*:

- **Three bomb caps, and they are counted rather than read.** `ZipArchiveEntry.Length` comes from the central directory, which is written by whoever built the archive, so no cap is enforced by pre-flighting a declared size — every entry is read through a `LimitedReadStream` that counts the bytes it actually produces, and a breach refuses the archive with an `ArchiveLimitExceededException` naming which limit was hit. **Where the refusal is raised matters and is not where the breach is detected.** `LimitedReadStream` throws on the entry parser's call stack, and `ContainerEntryDispatcher` contains everything an entry parser throws — it cannot tell a bomb from a corrupt PDF. So a breach of either byte cap is recorded and re-raised by `ZipDocumentParser` after the entry finishes, and when one read passes both, the *ratio* is the one reported: it names an entry as malicious where the total only says the archive got too big.

  | Cap | Default | Ceiling |
  |---|---|---|
  | `MaxTotalUncompressedBytes` | 256 MB | 2 GB |
  | `MaxCompressionRatio` | 100:1 | 1000:1 |
  | `MaxEntries` | 1,024 | 65,535 |

  Each is configurable downward freely and upward only to its ceiling: the setter clamps, so no construction path can exceed it, and `AddArchiveParser()` **throws** on a larger request rather than correcting it silently. Three rather than two, because a 10 GB file of zeros at 1000:1 and a 10 GB file at 2:1 are both fatal and the second passes any ratio check. The total is one running count per ***document***, not per entry and not per archive: a per-entry counter would enforce `cap × entries`, and a per-archive one enforced roughly `51 × cap` at the default nesting budget, because a nested archive re-entering through the dispatcher started a fresh count and cost its parent only its compressed size. It rides `ContainerContext`'s reserved tags for the same reason depth and the container budget do. Two honest limits: `MaxEntries` is checked after `ZipArchive.Entries` has parsed the whole central directory, because that property reads it on first access; and the ratio's denominator is the archive's own `CompressedLength`, which is attacker-controlled but fails safe, since understating it makes the computed ratio higher and trips the cap earlier.
- **Entry names are naming hygiene, not zip-slip mitigation.** Zip-slip is an *extraction* vulnerability: `../../etc/passwd` is dangerous because an extractor writes it to disk. **This parser never touches the filesystem.** An entry is handed to another `IDocumentParser` as a forward-only stream that decompresses as that parser reads it — nothing is buffered here and nothing is written anywhere — so a traversal-shaped entry name reaches `DocumentMetadata.FileName` and nowhere else. `FileNameSanitizer` is applied anyway, for exactly the reason it is applied to a mail subject — a name that reaches metadata should be a clean name — but it is recorded as hygiene rather than as a vulnerability that was closed here, because a future reader told otherwise would believe this parser was exposed to something it never was. (The content type is derived from the *unsanitised* name: the sanitiser caps length, so a pathological name could lose its extension, and typing an entry is not something a display concern should be able to change.)
- **Nested containers share one budget, and a refusal stops at the container above it.** `zip → .eml → zip` is the same unbounded-recursion shape the email parsers already bound, and it is bounded once. An archive that breaches a cap refuses *itself* — the exception leaves its `ParseAsync` — so a `.zip` ingested directly fails the ingestion, while a bomb attached to an `.eml` is contained by the email parser's dispatcher like any other failing attachment: the message keeps its subject, body and other attachments, and the bomb becomes a warning. That is the intended boundary, not a gap. The refusal has already stopped the read, and what the caller named was a message. A message made of nothing but such attachments is still bounded, because the byte total crosses the boundary even though the refusal does not. `ContainerContext` carries depth and entry budget through `DocumentMetadata.Tags` so the accounting survives a hop through `IDocumentParser`, and the archive parser rides that channel rather than inventing a second one — two separate budgets would leave an alternating chain bounded by neither. `ArchiveParserOptions.MaxNestingDepth` (default `3`) and `MaxNestedContainers` (default `50`) match `EmailParserOptions.MaxEmbeddedDepth` and `MaxEmbeddedMessages` deliberately, since the shared bound is only predictable while the two packages agree. Which content types count as containers lives in one place, `ContainerContentTypes`; a container format missing from that list is not bounded at all.

**Status:** ✅ Done (Phase 3.10 — `ZipDocumentParser` on `System.IO.Compression` from the BCL, no new third-party dependency; the container machinery it shares with the email parsers was promoted out of `Rag.NET.Parsers.Email` into `Rag.NET.Abstractions` in the same phase, with no behaviour change). Other archive formats (7z, tar, rar) and encrypted archives are out of scope.

**Exercised by:** test — `RealZipFixtureTests` reads a ZIP **written by CPython**, not by .NET's own `ZipArchive`, so the test cannot pass by round-tripping this library's own output; it asserts every entry's text survives, including entries under directory prefixes.

---

## Knowledge Graph

### GraphRAG — Entity Extraction + Community Summarization
**Status:** ✅ Done — shipped, exercised end to end since 2026-08-12, and benchmarked against the dense baseline since 2026-08-15. Six defects that first run found are fixed, a seventh (#209, unbounded relationship weights) that only the full corpus could show is fixed with it. **The benchmark's answer is that on MultiHop-RAG the graph path costs 0.02761 of nDCG@10 against the same candidates scored without it** — the tick means the feature is implemented and works, never that it improves retrieval; see below.
**Exercised by:** benchmark — the whole 609-article MultiHop-RAG corpus, every model call replayed: `BeirGraphRagCorpusTests` (nDCG@10 pinned in `BeirReproduction`: GraphRag 0.56897, GraphRagDepthControl 0.63967, plus the #239 ablations), `BeirGraphRagAnswerTests` (accuracy against the gold answers pinned in `MultiHopRagAnswerReproduction`: dense 0.3499, control 0.1384, local 0.2102, global 0.5951), and `GraphRagFunctionsTests` over the pinned 60-article slice. The runs that found eight shipping defects and #247.
**Package:** `Rag.NET.GraphRag`

Full Microsoft GraphRAG pipeline: LLM-driven entity and relationship extraction from chunks using iterative "gleaning" — with the model-supplied relationship weight bounded at the extraction boundary by `GraphRagOptions.MaxRelationshipWeight` (default 10), because it feeds modularity's null model directly and an unbounded one is not a bad number in a report, it is the clustering — hierarchical Leiden community detection (Traag/Waltman/van Eck over modularity — Louvain's local moving and aggregation with the paper's refinement phase between them, so every returned community is connected; see the `Leiden` type's own remarks), PageRank-weighted entity scoring, and LLM-generated community summary reports. At query time, combines dense entity retrieval, relation retrieval by text similarity, and community report retrieval — merged and scored by cosine similarity and PageRank.

**Why:** Multi-hop reasoning and global summarization require graph structure that pure vector search cannot provide.

**This row said `✅ Done` for a package nothing had ever run.** That was never false about the code existing and shipping, and it was false about the code working — a distinction this repository has now learned twice. `Rag.NET.GraphRag` shipped at 0.1.0, carried unit tests, and a dead-settings audit (#108) then found three documented behaviours that did not exist. The row is qualified rather than quietly left alone, because "it is implemented" and "it works" are different claims and only one of them was ever checked.

**What is exercised now.** `GraphRagFunctionsTests` (Phase 5.2) runs the whole path — extraction, Leiden, PageRank, community reports, local search, global search — over a pinned 60-article slice of MultiHop-RAG, asserting against that dataset's qrels rather than against plausible-looking output. Measured 2026-08-12: 8,999 entities and 16,403 relationships from 60 articles; entities recurring across articles ("Google" in 16 of them); 607 communities, the largest holding **7.3%** of the graph; local search returning a known-relevant document in the top 10 for **all 27** of the slice's judged queries; and global search's map/reduce running over the community reports it found in the *unfiltered* candidate set any caller would get.

**The six defects that first run found, all in shipped library code:**

| Defect | Fix |
|---|---|
| Four chunkers sliced by UTF-16 code unit, bisecting surrogate pairs and emitting strings `String.Normalize` throws on — fatal to any downstream embedder on text containing emoji | `6f86f0a7` |
| The clusterer matched relationship endpoints with `StringComparer.Ordinal` while `SqliteGraphStore`'s `name` column is `COLLATE NOCASE`, so real edges the store held were dropped | `e9178aee` |
| `Leiden` discarded intra-community weight when aggregating, so merging always paid: ten disjoint 10-node cliques ring-bridged returned **one** community of 100, and on the real slice one community held 89.7% of the graph | `929d45a3` |
| Community detection persisted PageRank scores through `AddEntitiesAsync`, whose merge clause appends — so every entity description was concatenated onto a copy of itself, once per document ingested | `46ff566b` |
| `LeidenOptions` was unreachable through the public API while this repository's own guide told readers to tune `Resolution` through it | `c34d270e` |
| The community report prompt was unbounded — 1,806,352 characters, some 450,000 tokens against gpt-4o-mini's 128,000-token context — and global search could not reach its own reports through the pipeline's retrieval, so its map/reduce never ran | `49da36ae`, `2abc17e4` |

None of the six was found by a test, a review or a user. All six were found by running the package once, on a real corpus.

**What is still unverified, and should not be read into the tick above:**

- **Real community reports are replayed by the guard as of #172, and the figures above predate them.** Reports used to answer through `PromptEchoChatClient`, which returns a bounded head of the prompt, so a "report" was its community's own entity descriptions rather than prose. They are now generated once against `openai/gpt-4o-mini` at temperature 0 by the generation tool's `--stage reports` and replayed refuse-on-miss out of `graph-reports`, exactly as extractions are. What that leaves open is narrower but real: **the two report rank figures below (1,098 → 209) were measured against the stub and are not statements about real reports** — the run prints the rank it measures on every pass, and that printed number is the one to read. Only global search's map-reduce still answers through the stub, because its prompts depend on retrieval order and caching them would make the guard machine-specific.
- **Retrieval-mode routing does not exist.** Issue #104 describes a `Mode` setting selecting local, global or automatic search. Neither the property nor a `GraphRagRetrievalMode` enum is in the package — the one that existed until 0.1.0 was deleted because no behavior read it (#125). Which search runs is a registration decision: place `GraphGlobalSearchBehavior` in the retrieval pipeline, resolve `Rag.NET.GraphRag.LocalSearch.IGraphRagSearch` directly, or both.
- **Whether GraphRAG *helps* is measured now, and on this corpus it does not — it hurts.** Measured 2026-08-15 over the whole 609-article MultiHop-RAG corpus, all 2,255 judged queries, 43 m 29 s: local search scored **nDCG@10 = 0.56897** against **0.59658** for a candidate-set control handed the *same* dense top-500 over the *same* 321,151-chunk store, so store size and candidate depth are held constant and the graph behaviour is the only variable — **−0.02761**, with Recall@10 and MRR@10 moving the same way. The delta against the dense Real leg's 0.63967 is −0.07070, and it was first published as "depth-confounded" (17,648 chunks at top-2,010 against 321,151 at top-500). **That caveat was measured the same day and it is wrong: depth costs nothing.** A dense run over the Real leg's 17,648-chunk store at top-500 reproduces the Real leg's nDCG@10, Recall@10 and MRR@10 to five decimals — the same ten documents in the same order on all 2,255 queries — so the −0.07070 decomposes exactly into **−0.04309 of store pollution** (303,503 entity, relationship and community-report chunks competing with the article chunks for rank at the same depth) and **−0.02761 of graph behaviour**. Both halves are attributable, and the pollution half is the larger; it is a ranking-policy problem for synthetic chunks, which RAPTOR shares, and it is not fixed here. That control is pinned as `BeirReproduction`'s `multihop-rag` / `GraphRagDepthControl` cell. **And the −0.02761 is entirely the PageRank blend** (#239, measured 2026-08-15): at `PageRankWeight = 0` local search returns the candidate-set control's ranking on 2,255 of 2,255 queries, because the blend adds a PageRank that sums to one over 62,392 entities to a cosine of 0.3–0.6 and so demotes every graph-connected chunk by ~30%; the walk itself adds no candidates and, measured as reach, would add +0.0015 of Recall@100 to a dense 0.986. None of the retrieval deficit is the graph. **And answers were scored, not only rankings** (Phase 5.2.2, 2026-08-15): all 2,556 MultiHop-RAG queries answered by `gpt-4o-mini` at temperature 0 from top-6 context under four retrieval arms, judged by the dataset authors' own rule against the gold answers. Dense **0.350**, GraphRAG local as shipped 0.210, dense over the graph store with no behaviour 0.138, GraphRAG global **0.595** — but per type: on the 816 entity questions, which cannot be guessed, **global 0.844 beats dense 0.772** (a real +59); on the yes/no types no arm beats an always-yes baseline (0.598 / 0.463) and global's lead there is commitment bias — it says "yes" 532 times and "no" 55, and abstains on only 9% of unanswerable questions against dense's 49%. Store pollution costs an answer −0.21, five times what it cost the ranking; local search as shipped is worse than dense for answers too. So: local hurts, for a design reason; global helps, on the questions where an answer must be found. Figures pinned in `MultiHopRagAnswerReproduction`, replayed from cache; design and reading in `docs/plans/2026-08-15-graphrag-answer-level-evaluation.md` and the ROADMAP's 5.2.2 entry. One visible mechanism: a community report reached the top 10 on **891 of 2,255 queries (39.5%)**, indexed under a synthetic document that no judgement covers — partly a measurement artefact, since a user might want the report, and partly real, since the rank slot is spent either way. **This is one dataset, one embedder and one implementation**: it says this local search does not beat plain dense scoring of the same candidates on MultiHop-RAG, not that GraphRAG is worthless. The figure is reproducible only with #231 (the fix for #230) applied, and it is pinned in `BeirReproduction`'s `multihop-rag` / `GraphRag` cell. The 60-article guard still publishes no nDCG and never will — 60 of 609 documents and 27 of 2,255 queries would not be comparable to anything.
- **The slice is a sixtieth of the corpus, and issue #209 is what that costs.** Unbounded relationship weights collapsed the *full* corpus into a single community holding 57,484 of 62,392 entities (92.13%) at modularity 0.0001, while this slice sat at 7.3% throughout — the heaviest weight the model returned over these sixty articles is 6.0, and the two nine-figure ones are in articles the slice does not contain. A degeneracy detector on a sixtieth of the corpus is worth having and is not evidence about the corpus.
- **The clusterer guarantees connected communities as of 2026-08-12 (#180), and that is measured, not argued.** The `Leiden` type was Louvain-family local moving plus a refinement pass that lacked the three constraints the Leiden paper's well-connectedness guarantee is made of (#171) — it was renamed to `LouvainWithRefinement` for as long as that was true, and back again once it was not, all of it before any release — and it returned internally disconnected communities: a sweep of ~30,000 detections found them on sparse *weighted* graphs (48 of 2,220 random weighted trees, none of 2,220 unweighted ones, none on anything dense), with a ten-node counterexample pinned in `CommunityConnectivityTests`. The refinement now restricts moves to nodes alone in their sub-community, requires both parties to be γ-connected to their community in the unrefined partition, and draws the merge target at random weighted by `exp(ΔQ / θ)`; the aggregate graph is built from the refined partition while the next level is seeded from the unrefined one. **Re-swept the same day over 40,000 random weighted trees at five resolutions and four seeds: 0 disconnected communities in 3,359,331, against 132 in 3,351,175 for the same sweep on the previous implementation.** The counterexample now returns connected communities and is asserted that way round.

---

### Mind-Map Extractor
**Package:** `Rag.NET.GraphRag`

**Status:** ✅ Done

Build a hierarchical concept tree from document content using a single LLM call. Nodes are stored as `GraphEntity` (Type = `"mind_map_node"`) and parent→child edges as `GraphRelationship` (Description = `"has_subtopic"`) in the existing `IGraphStore`. Retrieve via `GetFullGraphAsync()` and filter on type. Optionally runs automatically at ingestion time.

**Options**

| Option | Default | Description |
|---|---|---|
| `ExtractAtIngestion` | `false` | When true, runs automatically during ingestion. |
| `MaxDepth` | `3` | Maximum depth of the generated concept tree. |
| `ChatClient` | `null` | Optional cheaper model override. Null uses the DI-registered `IChatClient`. |
| `Prompt` | *(built-in)* | LLM prompt template. `{text}` and `{depth}` are replaced at runtime. |

**Usage**

```csharp
// On-demand extraction (inject MindMapExtractor directly):
services.AddRagNet(rag => rag.UseMindMapExtraction());
var extractor = sp.GetRequiredService<MindMapExtractor>();
var tree = await extractor.ExtractAsync(documentText, documentId, ct);

// With automatic ingestion-time extraction + IGraphStore persistence:
services.AddRagNet(rag => rag
    .UseGraphRag()
    .UseMindMapExtraction(o => {
        o.ExtractAtIngestion = true;
        o.MaxDepth = 3;
    }));
```

`UseMindMapExtraction` places `MindMapExtractionBehavior` into the ingestion pipeline after `ChunkSanitiserBehavior`, so `ExtractAtIngestion = true` is the only switch the second form needs. Until issue #191 it registered the behavior without placing it anywhere, so the second form above extracted nothing — the extractor was reachable only through the on-demand path in the first form.

---

## Security

### Prompt Injection Fortification
**Status:** ✅ Done
**Package:** `Rag.NET.Security`


Defence-in-depth against indirect prompt injection — the primary RAG security risk where attacker-controlled content (documents, images, web pages) contains embedded instructions that hijack the LLM's behaviour at query time.

Mitigation layers to consider:

- **Chunk-time sanitisation** — strip or flag known injection patterns (role-switch phrases, instruction delimiters) from ingested text and vision-LLM transcriptions before storing
- **Retrieval-time tagging** — propagate a `trust_level` metadata field (e.g. `internal` / `external` / `untrusted`) set at ingestion; surfaced to the answer engine so it can apply stricter system prompts for low-trust chunks
- **Prompt hardening at answer time** — inject a system prompt prefix that instructs the model to treat all retrieved content as data, never as instructions; configurable per-pipeline
- **Post-retrieval content scan** — run a lightweight classifier or regex guard over the ranked chunk set before it enters the answer prompt; flag or drop suspicious chunks
- **Vision-specific guard** — for vision-LLM transcriptions, pass output through the sanitiser before storing, since image-embedded text is a common injection vector

**Prior art in codebase:** `Rag.NET.Parsers.Vision` ships an internal `PromptInjectionSanitiser` (regex-based, case-insensitive) that targets role-switch phrases (`"ignore previous instructions"`, `"you are now"`, `"act as"`, `"disregard"`, `"system prompt"`), delimiter injection (`<|system|>`, `[INST]`, `###` blocks), and null-byte/whitespace padding. Matched spans are replaced with `[REDACTED]` and logged via `[LoggerMessage]`. This is the lightweight layer; the full fortification feature should promote this to a public, pipeline-level `IChunkSanitiser` abstraction and add the semantic classifier and retrieval-time trust tagging on top.

**Why:** Vision LLM parsers, web crawlers, and email connectors all ingest content from potentially adversarial sources. Without explicit mitigations, a single malicious document can redirect the model's behaviour for any user whose query retrieves that chunk.

---

## Observability

### OpenTelemetry Tracing & Metrics
**Status:** ⏳ Planned — Phase 4.4. Core instrumentation exists today; first-class OTel wiring does not.
**Package:** `Rag.NET` (core)

What exists today is the core package's built-in instrumentation: `RagTelemetry`, an `internal static` class holding one `ActivitySource` and one `Meter`, both named `Rag.NET`. It is always active and zero-overhead when no listener is attached — subscribers opt in with `.AddSource("Rag.NET")` / `.AddMeter("Rag.NET")` on their own OpenTelemetry SDK setup; there is no registration call on the `RagBuilder`.

- **Spans:** `ragnet.query`, `ragnet.ingest`, `ragnet.parse`, `ragnet.chunk`, `ragnet.embed`, `ragnet.store`, `ragnet.retrieve`, `ragnet.ask`
- **Histograms (ms):** `ragnet.ingest.duration`, `ragnet.embed.duration`, `ragnet.retrieve.duration`, `ragnet.ask.duration`, `ragnet.ratelimit.wait.duration`
- **Counters:** `ragnet.chunks.stored`, `ragnet.chunks.retrieved`, `ragnet.ingest.errors`, `ragnet.retrieve.errors`, `ragnet.llm.tokens`, `ragnet.llm.cost`

Span attributes, nesting, and exporter examples are documented in [OpenTelemetry Integration](opentelemetry.md).

What this feature adds — and what Phase 4.4 delivers — is first-class OTel wiring on top of that: exporter guidance, resource attributes, sample dashboards, and OpenTelemetry GenAI semantic conventions (`gen_ai.system`, `gen_ai.request.model`, `gen_ai.usage.input_tokens`, etc.). None of that is built, which is why this feature's matrix row below is unchecked.

**Why:** Production RAG systems need latency breakdowns to answer "is it slow at retrieval or at generation?" and cost visibility via token counters. The raw spans and instruments exist; the packaged wiring and GenAI conventions do not yet.

---

### Structured Logging Enrichment
**Package:** `Rag.NET` (core)

Enrich all existing `[LoggerMessage]` log entries with structured properties (`document_id`, `chunk_index`, `vector_store`, `strategy`) using log scopes. Standardise log event names to snake_case so logs are queryable in Seq/Loki/Datadog without parsing.

**Why:** The existing logs are present but not structured consistently — searching for all events related to a specific document ID requires string matching rather than a structured query.

---

## Management & Observability

### Data Management API
**Status:** ✅ Done
**Package:** `Rag.NET` (core)

A read/delete surface for browsing and managing ingested data via `IRagDataManager`.

- `GetCollectionsAsync()`, `GetSourcesAsync(collectionId)`, `GetChunksAsync(collectionId, sourceId)`
- `DeleteSourceAsync(collectionId, sourceId)`, `DeleteCollectionAsync(collectionId)`
- `GetStatsAsync()` — chunk counts per collection/source

**Hierarchy:** `Collection → Source → Chunk`

**Why:** No way today to inspect or clean up ingested data without going directly to the vector store.

---

### Conversational Memory Management
**Package:** `Rag.NET` (core) · `PersistentConversationMemory` → `Rag.NET.Memory`

Automatic conversation history management for multi-turn RAG. `ConversationMemoryPipeline` handles windowed trimming and optional LLM summarization. `PersistentConversationMemory` wraps the pipeline and adds cross-session recall: each exchange is embedded and stored in the vector store; relevant past exchanges are retrieved by similarity and injected as a system prefix.

**Why:** Multi-turn RAG is the dominant use case, but `ConversationHistory` is currently a raw list the caller must manage. Without auto-summarization and windowing, conversations either blow the context window or lose important context through naive truncation.

**Status:** ✅ Done

---

## Evaluation

### RAGAS-Style Metrics
**Package:** `Rag.NET.Evaluation.Ragas`

The four core RAGAS metrics alongside the existing `LlmJudgeEvaluator`:

- **Faithfulness** — are all claims in the answer supported by the retrieved chunks? LLM extracts atomic claims and verifies each against sources; score = supported / readable verdicts.
- **Answer Relevance** — does the answer address the question? One call generates `n` distinct synthetic questions from the answer; the mean cosine similarity to the original question embedding is clamped to `[0, 1]`. An evasive or noncommittal answer scores `0.0`.
- **Context Precision** — were the relevant chunks ranked highly? LLM classifies each chunk against the ground-truth answer, scored as rank-aware average precision `Σ(P@k × rel_k) / total_relevant` over the retrieved order.
- **Context Recall** — do the retrieved chunks cover the ground-truth answer? LLM verifies each ground-truth statement against the chunks; score = supported / readable verdicts.

Each metric implements the **internal** `IRagasMetric` — unrelated to `IRagEvaluator` — so suite composition is closed: `RagasEvaluationSuite`'s constructor is internal and `RagasEvaluationSuiteBuilder` exposes four fixed `Add*` methods. Custom metric registration is a deliberate non-goal. What *is* open is standalone use: each evaluator class is public with a public `ScoreAsync`, so any one of them can be constructed and called on its own.

The suite runs the registered metrics concurrently per sample and returns a `RagasReport` carrying per-metric means, an overall score, per-sample scores, and an unscoreable count per metric. Every score is nullable: a sample the model gave no readable verdict for is excluded from the mean rather than scored, and a metric — or a whole run — with nothing scoreable reports `null` rather than `0.0`. All metrics in a run share one judge, so `RagasOptions.MaxConcurrentCalls` bounds the whole run rather than each metric, and an optional `ICostLedger` records the run's spend — `CostKind.Chat` per judgement call and `CostKind.Embedding` for Answer Relevance's embedding batch.

**Why:** LLM-as-judge grades answer quality holistically. RAGAS metrics decompose quality into retrieval and generation components — essential for pinpointing whether failures are retrieval misses or generation errors.

**Status:** ✅ Done — verified against the published RAGAS definitions, pinned by tests, and documented in [the evaluation guide](../guide/evaluation.md#ragas-style-metrics) in Phase 3.1. Scores changed in that phase (rank-aware precision, the evasion penalty, and no fabricated `1.0` on a parse failure); re-baseline before comparing against older runs. Chat and embedding spend are both recorded to the cost ledger, priced from `RagasOptions`.

---

### Evaluation Dataset Builder
**Package:** `Rag.NET.Evaluation`

Generate synthetic question-answer pairs from an existing document corpus for offline evaluation. Samples `k` chunks by seeded reservoir sampling, uses an LLM to generate a question whose answer is grounded in the chunk, optionally generates a ground-truth answer. Output: an `EvaluationDataset` carrying the `EvaluationSample`s, how many chunks were actually sampled, and how many produced no usable sample and why — ready to feed into any `IRagEvaluator`.

**Why:** Bootstrapping an evaluation dataset from scratch requires manual annotation. Synthetic generation is imperfect but enables rapid iteration — run a bulk eval before/after a retrieval change to detect regressions.

**Status:** ✅ Done — verified, pinned by tests and documented in [the evaluation guide](../guide/evaluation.md#evaluationdatasetbuilder) in Phase 3.2. Four behaviours changed in that phase: sampling is seeded and reproducible (`Seed`, whose limits the guide states — the same seed and the *same corpus* draw the same chunks, and neither the model's text nor a changed corpus is fixed by it); the corpus is streamed through a reservoir instead of being materialised to sort it; a generation the model returned nothing for is dropped and counted in `EvaluationDataset.Skipped` instead of being emitted as an empty-question sample; and the build runs under `MaxConcurrentCalls` and records its chat spend to an optional `ICostLedger`. `BuildAsync` returns `EvaluationDataset` rather than `IReadOnlyList<EvaluationSample>` — a source-breaking change, taken cleanly because nothing is published yet. **Datasets built before this phase are not reproducible and may contain empty-question samples; rebuild rather than trust them.**

---

### LLM-as-Judge Evaluation
**Package:** `Rag.NET.Evaluation`
**Status:** ✅ Done

Use `LlmJudgeEvaluator` to grade predicted answers against named criteria (correctness, faithfulness, relevance) using any `IChatClient`. One LLM call per sample, all evaluated concurrently. Results carry per-criterion scores (0–1) and reasoning strings. `LlmJudgeResult.MeanScore(criterion)` and `AllPass(criterion, threshold)` support CI gate patterns. When `SourceChunks` is null or empty, faithfulness is automatically excluded. Custom criteria can be passed to the constructor.

**Why:** Embedding distance gives a single blunt signal that cannot detect hallucinations, factual errors, or off-topic answers. LLM-as-judge closes this gap with interpretable, per-criterion verdicts.

---

## Chunking

### Late Chunking
**Package:** `Rag.NET.Chunking`

**Status:** ✅ Done — delivered via `Rag.NET.Chunking` (`LateChunkingStrategy`, `UseLateChunking`) + `Rag.NET.Embeddings.Onnx` (`OnnxTokenEmbeddingGenerator`, `UseOnnxTokenEmbeddings`).

Embed the full document (or section) first to capture global context, then split the resulting token-level embeddings into chunks — instead of splitting text first and embedding each chunk independently. Requires a model that exposes token-level embeddings (e.g. `jina-embeddings-v2`). Implements `IDocumentChunkingStrategy`.

**Why:** Standard chunk-then-embed loses cross-chunk context. Late chunking preserves full-document attention during embedding, improving retrieval for references, pronouns, and cross-paragraph reasoning.

**Text it does not apply to (Phase 3.13):** token offsets must survive the BERT tokenizer's normalization, and two kinds of text change length under it — **CJK**, which grows as the normalizer spaces out every ideograph, and **NFD-decomposed** text, which shrinks as combining marks are stripped (fixable with `string.Normalize()`). Those sections fall back to ordinary chunk-then-embed rather than failing. Newlines, tabs and CRs used to be a third case and are not any more; see [the chunking guide](../guide/chunking.md#latechunkingstrategy) for the detail.

---

### Proposition Extraction Chunking
**Package:** `Rag.NET.Chunking`

LLM-driven chunking that decomposes document text into atomic, self-contained propositions — each a single factual claim expressed as a complete sentence. Each proposition becomes its own chunk, making it highly retrievable for specific questions. Implements `IDocumentChunkingStrategy`.

**Why:** Traditional chunks are paragraph-shaped and contain multiple ideas. Proposition chunks are query-shaped — one chunk, one fact — maximising precision at the cost of more chunks and an LLM pass at ingest time.

**Status:** Delivered by `PropositionChunkingStrategy` in `Rag.NET.Chunking` (`UsePropositionChunking`): token-bounded passage windows (cl100k_base), one `IChatClient` call per passage returning a JSON array of propositions, passage-chunk fallback on LLM/parse failure, and `parent.start`/`parent.end` + passage-span `StartPosition`/`EndPosition` for Parent Document Retrieval compatibility.

---

### Sliding Window Chunking with Overlap
**Package:** `Rag.NET.Chunking`

Fixed-size chunks with configurable token overlap between adjacent chunks. The simplest baseline chunking strategy — no LLM, no regex, O(n) time. Useful as a fast fallback or comparison baseline.

**Why:** Despite being the oldest technique, sliding window is still the default in many frameworks and serves as an important performance baseline.

**Status:** Delivered by `TokenAwareChunkingStrategy` in `Rag.NET.Chunking`, upgraded with `TokenAwareChunkingOptions` (`WindowSizeTokens` / `OverlapTokens` with fallback to `ChunkingOptions`).

---

## Retrieval Techniques

### Contextual Compression
**Package:** `Rag.NET.QueryTechniques`

Post-retrieval step that compresses each retrieved chunk to only the content most relevant to the query. Two strategies ship: **extractive** (embedding similarity, no LLM) and **abstractive** (per-chunk parallel LLM rewrite). Stopping criteria are either `KeepTopSentences` (top-N, default 3) or `MaxTokensPerChunk` (token budget via `cl100k_base`). Output is **non-destructive**: compressed text lives on `SearchResult.CompressedText`, the original `Chunk.Text` is preserved. Register with `builder.UseContextualCompression(opts => ...)` (default: answer-engine path — `ChatAnswerEngine`, `MapReduceAnswerEngine`, and `RefineAnswerEngine` all apply it) or additionally `builder.UseContextualCompressionInRetrieval()` to also compress retrieval-facing results. Skip per call with `RagOptions.SkipCompression = true`.

**Why:** Retrieved chunks often contain boilerplate or tangential sentences that waste context window space and dilute the signal for the LLM. Compression reduces prompt-token usage by dropping boilerplate and off-topic sentences from retrieved chunks, preserving the semantic signal for the LLM. Actual reduction depends on chunk content and stopping-criterion choice.

---

### Hypothetical Document Embeddings v2 — Multi-Hypothesis
**Package:** `Rag.NET.QueryTechniques`

Extend the existing `HydeQueryTechnique` to generate `n` hypothetical documents (configurable, default 3) and merge their embeddings by averaging before searching. More hypotheses improve recall at low `n` values and reduce the variance introduced by a single bad hypothesis.

**Why:** Single-hypothesis HyDE can degrade when the generated document takes a wrong angle on the query. Multi-hypothesis averaging is more robust and costs only `n` extra embedding calls.

**Status:** Delivered. `HydeOptions.HypothesisCount` (default 3) and `HypothesisTemperature` (default 0.8) drive `IHypotheticalDocumentGenerator.GenerateManyAsync`; `HydeBehavior` embeds all hypotheses in one batch, mean-pools + L2-normalizes, and passes the averaged vector downstream via the internal `RetrievalOptions.EmbeddingOverride` (consumed by the vector-store and ensemble dense arms, bypassing the embedding cache). Partial generation failures are tolerated; total failure falls back to the plain query. Cost per retrieval: `n` LLM calls + `n` embedding inputs (one batch call).

---

### Adaptive Retrieval (Query Complexity Routing)
**Package:** `Rag.NET.QueryTechniques`

Classify incoming queries by complexity — simple factoid, multi-hop, or summarization — using a lightweight LLM call or embedding classifier, then route to the appropriate retrieval strategy:

- Simple → standard top-K vector search
- Multi-hop → deep research loop or multi-query retrieval
- Summarization → RAPTOR cluster retrieval

**Why:** Running RAPTOR or multi-query on every query is expensive. Routing based on query type preserves quality for complex queries while keeping simple lookups fast and cheap.

---

### Corrective RAG (CRAG)
**Package:** `Rag.NET.QueryTechniques`

After standard retrieval, evaluate each chunk's relevance to the query using a lightweight LLM or cross-encoder. If all chunks score below a confidence threshold, fall back to a web search (`IWebSearchProvider`) to supplement or replace retrieved context before answer generation.

**Why:** Standard RAG has no awareness of whether its retrieved context actually answers the question. CRAG adds a self-correction loop — if the index doesn't know, search the web rather than hallucinate.

---

### FLARE — Forward-Looking Active Retrieval
**Package:** `Rag.NET.AnswerEngines` (engine) + `Rag.NET.Abstractions` (scorer contract)

Generate the answer incrementally sentence by sentence. When a sentence scores below a confidence threshold, pause generation, reformulate a query from the partial answer so far, retrieve fresh context, and continue generation with the new context injected.

**Why:** A single retrieval at query time misses information needed mid-answer. FLARE retrieves exactly when and what is needed — especially useful for long-form generation and multi-step reasoning.

**Status:** Delivered. `FlareAnswerEngine` (register with `UseFlare(...)`, or per call via `RagOptions.SynthesisStrategy = SynthesisStrategy.Flare` with the dispatching engine) generates one sentence per LLM call, scores it via the pluggable `IConfidenceScorer`, and below `FlareOptions.ConfidenceThreshold` (default 0.6) runs a lookahead retrieval (query + sentence, `LookaheadTopK`) through the retrieval pipeline — plain by default (HyDE/multi-query disabled to avoid hidden expansion calls; override via `FlareOptions.LookaheadRetrievalOptions`) — merges/dedups sources, and regenerates the sentence once. Caps: `MaxSentences` (15), `MaxRetrievals` (3). Default scorer is `SelfAssessmentConfidenceScorer` — one small LLM call, works with any `IChatClient`, fails open on failure; a logprob-based scorer is a documented extension point (`FlareOptions.Scorer`). Degraded-never-broken: retrieval failures keep the sentence, scorer failures count as confident.

---

### Sparse Embedding Retrieval (SPLADE)
**Package:** `Rag.NET.Embeddings.Onnx` (encoder) + `Rag.NET` (in-memory store, ensemble) + `Rag.NET.VectorStores.Qdrant` / `Rag.NET.VectorStores.PgVector` / `Rag.NET.VectorStores.Pinecone`

Generate sparse embedding vectors via SPLADE (Sparse Lexical and Expansion Model) using an ONNX model, stored alongside dense vectors. Each store searches its sparse vectors **natively and server-side** — Qdrant via a named sparse vector, Pinecone via sparse values on the record, PgVector via a `sparsevec` column and the `<#>` operator over an HNSW index. RRF is not part of any store's sparse path: fusion happens once, in the ensemble, across the dense, BM25 and sparse arms.

**Why:** SPLADE outperforms BM25 on out-of-vocabulary terms while remaining sparse enough for efficient retrieval. Pairs with dense embeddings for state-of-the-art hybrid search without a separate BM25 index.

**Status:** Delivered for Qdrant + PgVector + Pinecone + in-memory. `OnnxSpladeEncoder` (register with `UseSpladeEncoder(...)`; e.g. an ONNX export of `naver/splade-cocondenser-ensembledistil`) pools MLM logits per chunk/query into a pruned `SparseVector` (`log(1 + ReLU(logit))`, max over tokens, `TopTerms` largest). Ingestion computes sparse vectors automatically (`SparseEmbeddingBehavior`) when the store implements the `ISparseSearchable` capability — `QdrantSparseVectorStore` via `UseQdrant(..., enableSparseVectors: true)` (named sparse vector "splade" on the same points, deterministic point ids, fail-fast on pre-existing dense-only collections), `PgVectorSparseVectorStore` via `UsePgVector(..., enableSparseVectors: true, sparseVocabularySize: 30522)` (a `sparsevec(N)` column on the same `rag_chunks` rows with an `hnsw sparsevec_ip_ops` index, searched by `<#>`; requires pgvector 0.7.0+, fail-fast on both an older extension and a pre-existing column of the wrong dimension; no dense/sparse write-ordering contract, since the dense upsert excludes the sparse column; `OnnxSpladeOptions.TopTerms` must stay ≤ 1000 while the sparse HNSW index exists), `PineconeSparseVectorStore` via `UsePinecone(..., configure: o => o.EnableSparseVectors = true)` (native sparse values on the same records, dotproduct metric enforced fail-fast; the sparse *write* path is verified by construction only — Pinecone Local rejects sparse writes, so it is untested against a live serverless index), or `InMemoryVectorStore` (inverted postings, dot product). Retrieval: `EnsembleBehavior` grows a third arm fused by weighted RRF (`EnsembleOptions.SparseWeight`; `RetrievalOptions.UseSparseSearch` null follows `UseHybridSearch`). Sparse scores are raw dot products on every store, so `SearchOptions.MinScore` is on a different scale there than on the cosine dense path. Degraded-never-broken: sparse failures at ingest or query time log a warning and continue dense/BM25-only.

---

### Multi-Index Federation
**Package:** `Rag.NET` (core)

A `FederatedVectorStore` that wraps multiple `IVectorStore` instances and merges results via RRF. Enables searching across collections in different vector stores simultaneously — e.g. a private PgVector index plus a shared Qdrant index.

**Why:** Enterprise deployments often have multiple vector stores for different data domains (HR docs in one, engineering docs in another). Federation enables unified search without data migration.

**Status:** Delivered. `FederatedVectorStore` (register with `UseFederatedSearch(f => f.AddStore(...).AddStore(...))`) fans searches out to all stores concurrently and merges the per-store rankings with N-way RRF (`FederatedStoreOptions.RrfK`, default 60); each merged result is tagged with a `source.store` metadata entry (store name or index). Writes and deletes go to the primary store only (`WithPrimary(...)`, default the first). Degraded-never-broken: a failing store is skipped with a warning; the search throws only when every store failed. Limitation: federation is dense-only — hybrid (`IHybridSearchable`), sparse, and collection-management capabilities of the underlying stores are not federated.

---

## Security & Compliance

### PII Detection and Redaction
**Package:** `Rag.NET.Security`

Detect and redact personally identifiable information (names, emails, phone numbers, SSNs, credit card numbers, IP addresses) from chunks before storage. Two modes: regex-based (`IChunkSanitiser` extension using named capture groups) and LLM-based (higher accuracy, slower). Redacted spans replaced with typed placeholders (`[EMAIL]`, `[PHONE]`, etc.) with optional reversible tokenisation for authorised retrieval.

**Why:** Ingesting CRM data, HR documents, or customer emails without PII scrubbing creates compliance risk (GDPR, HIPAA). Chunk-time redaction is the correct interception point — once embedded and stored, PII is hard to purge.

---

### Role-Based Access Control (RBAC) on Chunks
**Package:** `Rag.NET.Security`

Store an `allowed_roles` metadata field on each chunk at ingest time (sourced from `DocumentMetadata.Tags`). `RbacRetrievalGuard` (implements `IRetrievalGuard`) filters retrieved chunks to only those whose `allowed_roles` intersect with the caller's roles, passed via `RagOptions.MetadataFilter` or a new `RagOptions.CallerRoles` property.

**Why:** Multi-tenant or multi-department deployments need document-level access control. Without it, a query from a junior employee could surface HR performance reviews or M&A documents.

---

### Audit Log
**Package:** `Rag.NET.Security`

Structured audit trail of every pipeline operation: who asked what, which chunks were retrieved (document ID + chunk index), what answer was generated, and any sanitiser/guard actions taken. Implemented as an `IAuditLog` abstraction with a `SqliteAuditLog` default and a `NoOpAuditLog` for opt-out. Integrates as an `IRetrievalBehavior` and answer engine decorator.

**Why:** Regulated industries (finance, healthcare, legal) require demonstrable audit trails for AI-generated answers. Without logging, there's no way to investigate complaints or demonstrate compliance.

---

## Infrastructure & Reliability

### LLM Fallback Chain
**Package:** `Rag.NET` (core)

A `FallbackChatClient` (implements `IChatClient`) that tries a primary client, catches transient failures or rate limits, and retries with a secondary client. Configurable fallback list with per-client timeout and error classification. Wraps any `IChatClient` transparently. Registered via `UseFallbackChain` (ordered client factories + optional `PerClientTimeout`); supersedes any prior `IChatClient` registration.

**Why:** Production RAG systems cannot tolerate a single LLM provider as a hard dependency. A fallback chain from OpenAI → Anthropic → local Ollama gives resilience without changing pipeline code.

**Status:** ✅ Done — `FallbackChatClient` with per-client timeout, DI registration via `UseFallbackChain`, documented in the [resilience guide](../guide/resilience.md)

---

### Embedding Versioning & Re-indexing
**Package:** `Rag.NET` (core)

`UseEmbeddingVersioning` registers a SQLite `IEmbeddingVersionStore`; after each successful store the ingestion pipeline stamps the document with the embedding model identity (from `EmbeddingGeneratorMetadata` or the explicit `EmbeddingVersioningOptions.ModelId` override) and vector dimension, and `DeleteAsync` removes the stamp. `pipeline.ReindexStaleAsync(...)` finds documents whose stamp differs from the current model or dimension and re-embeds them from the chunk text stored by the registered `IRagDataManager` (re-store replaces by `(DocumentId, ChunkIndex)`; sparse vectors are regenerated when a sparse encoder + sparse-capable store are present). Without a data manager, stale documents are reported for caller-driven re-ingest.

**Why:** Switching embedding models (a common upgrade path) previously required wiping and re-ingesting the entire corpus. Version tracking makes incremental re-indexing possible.

**Status:** ✅ Done (library API in Phase 1.3; the `ragnet reindex --stale` CLI command lands with the CLI tool in Milestone 3)

---

### Rate Limiting & Cost Budgeting
**Package:** `Rag.NET` (core)

An `IRateLimiter` abstraction with a token-bucket implementation (`System.Threading.RateLimiting`) that throttles chat and embedding calls to stay within API rate limits — callers over the per-minute budget wait rather than fail. Registered via `UseRateLimiting`, which decorates whatever `IChatClient`/`IEmbeddingGenerator` is registered. An `ICostLedger` abstraction (SQLite-persisted `SqliteCostLedger`, in-memory alternative) tracks spend (tokens × user-supplied price) across restarts, and the `UseCostBudgeting` decorators throw `BudgetExceededException` when a configured daily/monthly limit is reached; token counts use provider-reported usage when available, tiktoken estimation otherwise. The ledger also carries per-page kinds: a `CostKind.Ocr` entry records `Pages` and zero tokens, counts toward the same budget window, and `SqliteCostLedger` adds its `pages` column to a pre-existing table with an automatic additive `ALTER TABLE` on first open.

**Why:** Uncontrolled LLM API usage in production can produce surprise invoices. Rate limiting prevents 429 cascades; budgeting provides a hard guardrail for cost-sensitive deployments.

**Status:** ✅ Done — `UseRateLimiting` + `UseCostBudgeting` with stacking support over the fallback chain, documented in the [resilience guide](../guide/resilience.md)

---

### Batch Ingestion Optimiser
**Package:** `Rag.NET` (core)

Chunk-batch embedding inside a single document: `EmbeddingBehavior` slices pending chunks into batches of `IngestionOptions.EmbedBatchSize` (default 100) and embeds the batches concurrently with `Parallel.ForEachAsync`, bounded by `IngestionOptions.MaxConcurrentEmbeddingBatches` (default 2). Results reassemble by original chunk index, and documents at or below the batch size keep the original single-call path. This complements two things that already existed before this feature: document-level parallelism (`IngestFromProviderAsync` + `IngestionOptions.MaxDegreeOfParallelism`) and the single bulk upsert to the vector store in `StorageBehavior`.

**Why:** A large document embedded as one giant generator call serialises the slowest step of ingestion and can exceed embedding-API request limits. Document-level parallelism only helps across documents; chunk batching with bounded concurrency speeds up large individual documents without overwhelming embedding-service rate limits.

**Status:** ✅ Done (chunk-batch embedding added in Phase 1.3; document-level parallelism and bulk upsert pre-existed)

---

## Developer Experience

### Rag.NET CLI Tool
**Package:** `Rag.NET.Cli` (dotnet tool)

A `dotnet tool` (`ragnet`) running against a pipeline configured the same way as
`Rag.NET.Mcp.Tool` — `Rag.NET.Hosting`'s `AddRagNetPipelineFromConfiguration`, reading the
`RagNet` section of `appsettings.json` or environment variables. See the [MCP server
guide](../guide/mcp.mdx)'s Pattern D for the configuration shape (chat client, embeddings,
vector store kind), the startup validation, and the `InMemory` warning — they are identical
here.

- `ragnet ingest <path> [--overwrite]` — ingest a single file, or every file under a directory
  (recursively)
- `ragnet query "<question>" [--top-k N]` — retrieve the chunks a question matches

Output is JSON on stdout, one object per invocation, meant to be piped onward; diagnostics,
warnings, and errors go to stderr.

`ragnet evaluate` is **not implemented**. `Rag.NET.Evaluation`'s evaluators
(`EmbeddingDistanceEvaluator`, `LlmJudgeEvaluator`) score `EvaluationSample` instances that
already carry a *predicted* answer, and no dataset file format exists anywhere in this
repository to read a set of question/reference pairs from — wiring a working command needs that
format designed first, not a thin call onto an existing seam the way `ingest`/`query` are.
Running `ragnet evaluate` prints this reason to stderr and exits non-zero.

**Why:** The library is code-first, but operations tasks (ad-hoc ingestion, retrieval checks) are painful to script. A CLI tool makes these accessible without writing a custom harness.

---

### Pipeline Debugger / Trace Viewer
**Package:** `Rag.NET.Diagnostics` (capture, no ASP.NET dependency) + `Rag.NET.Diagnostics.AspNetCore` (the opt-in endpoint)

A lightweight `RagDebugMiddleware` for ASP.NET Core that exposes a `/ragnet/trace` endpoint returning a structured JSON trace of the last N pipeline executions: which chunks were retrieved, their scores, what the answer engine received, sanitiser/guard actions, and latency breakdown per stage.

**Why:** Diagnosing why a RAG pipeline gave a bad answer currently requires adding debug logging and re-running. A persistent in-memory trace ring buffer with a JSON viewer endpoint lets developers inspect production traces without code changes.

**Status:** ✅ Done — shipped in Phase 3.4 and documented in [the pipeline debugger guide](../guide/diagnostics.md). `AddRagDiagnostics()` keeps a bounded ring buffer of the last `Capacity` (default 50) query executions, readable in-process through `ITraceStore` or over `MapRagNetTrace()`. A `RagTrace` carries the retrieved chunks with their `DocumentId`, `ChunkIndex` and `Score`, the latency of each `ragnet.*` stage, and a `TraceGuardAction` per guard and sanitiser that ran — component name, counts in and out, and whether it changed anything. That last part is the diagnostic hole this row existed to close: nothing anywhere recorded that `RbacRetrievalGuard` had dropped a chunk or `PiiChunkSanitiser` had rewritten one, so *"why is that chunk missing from the answer"* was unanswerable.

It is assembled from what already existed rather than from new instrumentation: an `ActivityListener` over the `ragnet.*` spans supplies the timings **and** decides when a trace is complete, a retrieval behavior and an answer decorator mirror `AuditRetrievalBehavior` and `AuditAnswerEngineDecorator`, and every part joins on `Activity.Current.TraceId`. Only two seams are new — `IPromptObserver`, which `ChatAnswerEngine` calls on both the streamed and non-streamed paths, and the three tracing decorators over `IRetrievalGuard`, `IQuerySanitiser` and `IChunkSanitiser`.

Read against the original specification above, five things differ:

- **Two packages, not one.** Capture has no ASP.NET dependency, so it works in a console app, a worker or a test; `Rag.NET.Diagnostics.AspNetCore` adds the endpoint for applications that want one. `Rag.NET.Diagnostics` references `Rag.NET` and deliberately **not** `Rag.NET.Security` — reusing `AuditChunkRef` would have dragged SQLite and its native binaries, the ML tokenizers and their data file, Polly and protobuf onto a team that wanted a debugger and never enabled auditing. `TraceChunk` mirrors its field names instead.
- **No middleware, and no automatic route.** `MapRagNetTrace()` is an explicit endpoint mapping: `GET /ragnet/traces` returns summaries carrying no captured text at all, `GET /ragnet/traces/{traceId}` returns one whole trace. Authentication is `ApiKeyMiddleware`'s — the routes are behind the key by being mapped into an authenticated application and by not being added to `ApiKeyOptions.ExemptPathPrefixes`. Mapping without `AddRagDiagnostics` answers an empty list and a 404 rather than throwing.
- **Not "persistent".** The buffer is in memory and dropped on restart, and an evicted trace is gone. `IAuditLog` remains the durable, compliance-grade record; the two are deliberately separate systems that share a vocabulary.
- **Content is behind four further flags.** Registering captures structure only. `CaptureQueryText`, `CaptureChunkText`, `CapturePromptText` and `CaptureAnswerText` each default to `false`, cap per field at `MaxCapturedCharacters` (default 4000) with a visible truncation marker, and pass through a single gate in the collector — a query sanitiser's text is governed by `CaptureQueryText` and a retrieval guard's by `CaptureChunkText`, because they are not the same kind of content.
- **Capture is not re-sanitised**, so a trace may hold text the pipeline itself went on to strip. That is the point — the commonest reason to open a trace is to see what a sanitiser did — and it is a reason to leave content capture off in production, which is the default.

Two limitations, both pinned by tests: a **streamed** prompt only correlates when the host supplies an ambient activity, because `ChatAnswerEngine` assembles it after the first `yield return` and the observer then runs on the consumer's execution context (true under ASP.NET, not in a console app; chunks, stages and the commit are unaffected). And `IChunkSanitiser` runs at **ingestion**, so its actions land in an ingestion trace rather than in the query that later surfaces the chunk.

One edit to existing production code beyond the seam: `RagPipeline` gained an enclosing `ragnet.query` span, because `ragnet.retrieve` and `ragnet.ask` are siblings — without a parent they would be separate roots with different trace ids in any host that starts no activity of its own, and nothing could tell a finished ask from a finished retrieval.

---

### A/B Testing Framework
**Package:** `Rag.NET.Evaluation.Ragas` (`RagAbTester`) + `Rag.NET.Evaluation` (the report model)

`RagAbTester.CompareAsync` runs one evaluation dataset through **two** variants — a variant being a whole `IRagPipeline` plus optional `RagOptions` and its own `ICostLedger`, so a comparison can span chunking, vector store, embedding model and reranker rather than only per-call settings — and reports a paired comparison of the two.

Execution is sequential with **the lead alternating by sample**: whichever variant runs second benefits from provider prompt caching and a warm store, so a fixed order hands one side a systematic advantage and reports it as a result. Concurrent execution was rejected because the two variants would then contend for one provider and one connection pool, and the latency numbers would measure the contention.

Both sides answer the same questions, so the comparison is paired (`delta = B − A` per sample), which removes between-sample variance — some questions are simply harder — and is what makes a fifty-sample comparison worth running. Per metric the `AbReport` carries each variant's mean **over the compared pairs**, the mean delta, a win/loss/tie tally, and a **seeded 95% percentile-bootstrap confidence interval** on the mean delta. Bootstrap rather than a t-interval because RAGAS scores are bounded on `[0, 1]` and frequently skewed. Plus per-variant latency p50/p95 with the same paired interval, and per-variant cost where a ledger was supplied.

Dropped pairs are counted under two separate headings, because they have different causes and different fixes: a sample either variant failed to answer leaves **every** metric (`DroppedForRunFailure`), while a metric returning `null` on either side drops that pair **for that metric only** (`DroppedAsUnscoreable`). Nothing is fabricated to fill a gap — a metric with no comparable pair reports `null` means and a `null` interval rather than `0.0`, and a variant with no ledger is absent from the cost map rather than recorded as free.

The composition root sits in `Rag.NET.Evaluation.Ragas` rather than `Rag.NET.Evaluation`: pairing needs a per-sample score *per metric*, `RagasReport.Samples` is the only thing in the stack that produces one (`IRagEvaluator` returns an aggregate with no metric breakdown and no way to express unscoreable), and `Rag.NET.Evaluation.Ragas` already references `Rag.NET.Evaluation` — so a tester taking a `RagasEvaluationSuite` on the other side of that edge would be a reference cycle. `AbReport`, `AbMetricComparison`, `AbLatencyComparison`, `AbVariant`, `AbOptions`, `AbTally` and `AbConfidenceInterval` stay in `Rag.NET.Evaluation`, owing nothing to RAGAS.

**Why:** Changing retrieval strategy, chunking size, or reranking has unpredictable quality effects. A/B testing with automatic evaluation scores makes it safe to iterate on pipeline configuration in production.

**Status:** ✅ Done — the **offline harness with paired statistics**, shipped in Phase 3.3 and documented in [the evaluation guide](../guide/evaluation.md#ab-testing). Read against the original specification above, three things differ and all three are deliberate:

- **Not "simultaneously".** Sequential with alternating order, for the fairness reason above.
- **Exactly two variants, not N.** `PairedDeltas` and the tally are strictly pairwise, and N-way needs a multiple-comparisons correction; a third variant is rejected before anything runs rather than executed at full LLM cost and dropped.
- **Scored through `RagasEvaluationSuite`, not `IRagEvaluator`.** Per-metric pairing is impossible through an interface that returns one unnamed non-nullable score per sample.

**Shadow mode — the deferred half of this row — was delivered by Phase 3.8** (`Rag.NET.Evaluation`, namespace `Rag.NET.Evaluation.Shadow`, documented in [the shadow mode guide](../guide/shadow-mode.md)). It got the separate design the deferral demanded, because each of the production-path failure modes named above shaped a structural decision rather than a flag: `ShadowRagPipeline` returns the primary's response **before** anything is scheduled, and enqueueing is a synchronous, caught, drop-on-full write (`BoundedChannelFullMode.DropWrite` — deliberately not the ingestion queue's `Wait`, whose backpressure would couple the primary's latency to the secondary's throughput), so a failing, slow or wedged secondary is structurally incapable of touching a caller the primary already served. Spend is opt-in and visible: `SampleRate` defaults to `0.0`, every sampled request roughly doubles that request's spend, the secondary's spend is measured per capture via a dedicated ledger, and the primary's is honestly absent — it serves concurrent traffic on a shared ledger, so no honest per-request figure exists. Fire-and-forget loss became counted loss: the consumer drains on shutdown within a bounded timeout, and `DroppedCount` plus `AbandonedCount` is the entire gap between the configured sample rate and what the store holds. And the two-of-four-metrics constraint became the argument *for* capturing: `ShadowReplay.From(captures, references)` feeds stored captures into `CompareAsync`, unannotated replays score with the reference-free metrics, and references supplied at replay time unlock all four — which inline scoring never could. Captures hold the production question and retrieved document text **verbatim** by default; the sanitiser seam (`IShadowCaptureSanitiser`) defaults to none, and retention, encryption and deletion belong to whoever implements `IShadowCaptureStore`.

**Still not built:** side-by-side review of two live answers — this paragraph previously bundled it with shadow mode as "scheduled as Phase 3.8", but 3.8's goal never included it and it remains undelivered. Also out of scope by decision: power analysis, which would tell a caller how many samples they need before running; the interval reports what the run achieved, which is the honest half of that question — and shadow mode adds no significance testing of its own, so two averages over ten captured pairs is still not a result.

---

## Packaging & Distribution

### NuGet Publishing Pipeline
**Package:** N/A (CI/CD)

Automated NuGet publishing on git tag push:

- Multi-package `.nupkg` generation from all `src/` projects via `dotnet pack`
- Version stamped from git tag (e.g. `v1.0.0` → `1.0.0`) using `MinVer` or `Nerdbank.GitVersioning`
- GitHub Actions workflow: build → test → pack → push to `nuget.org`
- Package icons, `README.md` embedded per package, license expression (`MIT`)
- Symbol packages (`.snupkg`) for source-linked debugging

**Why:** Rag.NET has no NuGet packages today — consuming it requires a git submodule or local project reference. Publishing to NuGet is the single highest-leverage action for adoption.

---

### Sample Applications
**Package:** `samples/`

Curated, runnable sample projects demonstrating real-world Rag.NET usage:

- `samples/QuickStart` — minimal console app: ingest a folder of `.txt` files, ask a question, print the answer
- `samples/WebApi` — ASP.NET Core minimal API wrapping the RAG pipeline with swagger UI
- `samples/MultiModal` — ingest PDFs + images, ask questions that require visual understanding
- `samples/DataProvider` — schedule-based re-ingestion from GitHub using the content-hash record manager
- `samples/Evaluation` — run RAGAS metrics against a synthetic dataset

**Why:** The library is feature-rich but the learning curve is steep. Runnable samples reduce time-to-first-answer from hours to minutes.

---

## Priority / Dependencies

| Done | Feature | Complexity | Dependencies |
|------|---------|------------|--------------|
| [x] | Azure AI Search Tests via Simulator | Low | `Testcontainers` + simulator Docker image |
| [x] | Cohere Rerank | Low | Cohere API key |
| [x] | Embedding Distance Evaluation | Low | `IEmbeddingGenerator` |
| [x] | Header-Aware Markdown/HTML Splitting | Low | Existing Markdown parser |
| [x] | Lost-in-the-Middle Reordering | Low | None |
| [x] | Progress Reporting | Low | None |
| [x] | Redundancy Filter | Low | Embedding access |
| [x] | Token-Aware Splitting | Low | `Microsoft.ML.Tokenizers` |
| [x] | Audio Transcription | Medium | `Whisper.net` |
| [x] | BM25 Keyword Retrieval | Medium | None |
| [x] | BM25 Synonym Expansion | Medium | BM25 retriever |
| [x] | Content-Hash Record Manager | Medium | Persistence store |
| [x] | Cross-Encoder Reranking | Medium | Model or API |
| [x] | Data Management API | Medium | `IVectorStore` extension |
| [x] | Data Provider Abstraction | Medium | Existing `IDocumentParser` |
| [x] | Decorator Pipeline Refactoring | Medium | None |
| [x] | Hierarchical Merger | Medium | None |
| [x] | HyDE | Medium | `IChatClient` |
| [x] | LLM-as-Judge Evaluation | Medium | `IChatClient` |
| [x] | Map-Reduce / Refine Synthesis | Medium | `IChatClient` |
| [x] | MCP Server | Medium | MCP SDK |
| [x] | MMR Retrieval | Medium | Embedding access |
| [x] | Multi-Language Code Splitting | Medium | None (regex) |
| [x] | Multi-Query Retrieval | Medium | `IChatClient` |
| [x] | SaaS: Azure Blob Storage | Low | `Azure.Storage.Blobs` |
| [x] | SaaS: GitLab | Low | `GitLabApiClient` |
| [x] | SaaS: Bitbucket | Low | Bitbucket REST API |
| [x] | SaaS: Zendesk | Low | Zendesk REST API |
| [x] | SaaS: Confluence | Medium | Confluence REST API |
| [x] | SaaS: Notion | Medium | Notion REST API |
| [x] | SaaS: Jira | Medium | Jira REST API |
| [x] | SaaS: Asana | Medium | Asana REST API |
| [x] | SaaS: Airtable | Medium | Airtable REST API |
| [x] | SaaS: Slack | Medium | Slack Web API |
| [x] | SaaS: Gmail / IMAP | Medium | MailKit |
| [x] | SaaS: Google Drive | Medium | `Google.Apis.Drive.v3` |
| [x] | SaaS: Dropbox | Medium | `Dropbox.Api` |
| [x] | SaaS: Box | Medium | `Box.V2` |
| [x] | SaaS: SharePoint | Medium | Microsoft Graph SDK |
| [x] | SaaS: OneDrive | Medium | Microsoft Graph SDK |
| [x] | SaaS: Microsoft Teams | Medium | Microsoft Graph SDK |
| [x] | Search Result Caching | Medium | None |
| [x] | SQLite Persistence for In-Memory Indexes | Medium | `Microsoft.Data.Sqlite` |
| [x] | Tag-Based Retrieval | Medium | Hybrid search |
| [x] | Time-Weighted Retrieval | Medium | None |
| [x] | Web Crawler / Sitemap / RSS | Medium | HTTP client |
| [x] | Semantic Chunking (Embedding-Based) | Medium | `IEmbeddingGenerator` |
| [x] | C# Semantic Chunking (Roslyn) | High | `Microsoft.CodeAnalysis.CSharp` |
| [x] | Deep Research Loop | High | `IChatClient` |
| [x] | Domain-Specific Chunking Templates | High | Per-domain logic |
| [x] | Ensemble / RRF | High | Multiple retrievers |
| [x] | Image / Video Description | High | Vision LLM |
| [x] | Prompt Injection Fortification | Medium | None (sanitiser) / `IChatClient` (classifier) |
| [x] | LLM Metadata Extraction at Ingest | High | `IChatClient` |
| [x] | Conversational Memory Management | High | `IChatClient` + tokenizer |
| [x] | Parent-Document Retrieval | High | Dual index |
| [x] | RAPTOR | High | UMAP + GMM + `IChatClient` |
| [x] | Self-Query Filtering | High | `IChatClient` + schema |
| [x] | GraphRAG | Very High | Graph DB + `IChatClient` |
| [x] | Mind-Map Extractor | Medium | `IChatClient` + `IGraphStore` |
| [ ] | IOptions Alignment + ZeroAlloc Validation for pipeline options | Low | `Microsoft.Extensions.Options` + ZeroAlloc.Validation |
| [ ] | NuGet Publishing Pipeline | Low | GitHub Actions + MinVer |
| [ ] | Structured Logging Enrichment | Low | None |
| [x] | Sliding Window Chunking with Overlap | Low | None |
| [x] | Hypothetical Document Embeddings v2 | Low | `IChatClient` + `IEmbeddingGenerator` |
| [x] | EPUB Parser | Low | `VersOne.Epub` |
| [x] | Email File Parser (EML/MSG) | Low | `MimeKit` + `MsgReader` |
| [x] | Archive Parser (ZIP) | Low | `System.IO.Compression` |
| [x] | Linear Issue Tracker | Low | Linear GraphQL API |
| [x] | RAGAS-Style Metrics | Medium | `IChatClient` + `IEmbeddingGenerator` |
| [x] | Evaluation Dataset Builder | Medium | `IChatClient` |
| [x] | A/B Testing Framework | Medium | `RagasEvaluationSuite` (offline harness, 3.3) + `ShadowRagPipeline`/`ShadowReplay` (shadow mode, 3.8; side-by-side review not built) |
| [x] | Weaviate Vector Store | Medium | REST + GraphQL via `ZeroAlloc.Rest` |
| [x] | Chroma Vector Store | Medium | Chroma REST API |
| [x] | Pinecone Vector Store | Medium | Official `Pinecone.Client` SDK (3.1.0) |
| [x] | Multi-Index Federation | Medium | `IVectorStore` composition (dense-only) |
| [x] | PDF Table Extraction | Medium | PdfPig geometry |
| [x] | OCR for Scanned PDFs | Medium | Tesseract in source builds only (`EnableOcr` compile gate — the published package compiles it out); Azure Document Intelligence (ungated, per-page billed) |
| [x] | Contextual Compression | Medium | `IChatClient` or embeddings |
| [x] | Corrective RAG (CRAG) | Medium | `IChatClient` + web search |
| [x] | Proposition Extraction Chunking | Medium | `IChatClient` |
| [x] | Webhook / Event-Driven Ingestion | Medium | ASP.NET Core minimal API + `System.Threading.Channels`; polling trigger; Azure Service Bus trigger (`Azure.Messaging.ServiceBus`) |
| [ ] | OpenTelemetry Tracing & Metrics | Medium | `System.Diagnostics.ActivitySource` |
| [x] | Email Connector (Outlook/Exchange) | Medium | Microsoft Graph SDK |
| [x] | PII Detection and Redaction | Medium | Regex / `IChatClient` |
| [x] | Role-Based Access Control (RBAC) | Medium | `IRetrievalGuard` extension |
| [x] | Audit Log | Medium | `IAuditLog` + SQLite |
| [x] | LLM Fallback Chain | Medium | `IChatClient` decorator |
| [x] | Rate Limiting & Cost Budgeting | Medium | Token bucket |
| [x] | Batch Ingestion Optimiser | Medium | `Parallel.ForEachAsync` |
| [ ] | Sample Applications | Medium | All packages |
| [x] | Rag.NET CLI Tool | Medium | `dotnet tool` (`ingest`/`query`; `evaluate` deferred — no dataset file format exists yet) |
| [x] | Pipeline Debugger / Trace Viewer | Medium | `ActivityListener` + ring buffer; endpoint in `Rag.NET.Diagnostics.AspNetCore` |
| [x] | Adaptive Retrieval (Query Routing) | High | `IChatClient` + classifier |
| [x] | FLARE | High | `IChatClient` (self-assessment scorer; logprob scorer = extension point) |
| [x] | Sparse Embedding Retrieval (SPLADE) | High | ONNX + vector store (Qdrant, PgVector `sparsevec`, Pinecone, in-memory) |
| [x] | Late Chunking | High | Token-level embedding model |
| [x] | Embedding Versioning & Re-indexing | High | SQLite version store (CLI command deferred to Milestone 3) |
