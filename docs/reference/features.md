---
id: features
title: Feature Backlog
sidebar_position: 2
---

# Rag.NET Feature Backlog

Candidate features for future design and implementation. Completed features are documented in their own pages.

---

## Chunking

### Semantic Chunking (Embedding-Based Boundary Detection)
**Package:** `Rag.NET` (core)

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
**Package:** `Rag.NET` (core)

Configurable chunking stage driven by user-supplied regex patterns for each heading level and an integer hierarchy depth. Builds a heading tree and extracts subtrees up to the specified depth as chunks — each chunk starts with its section heading and contains all body text within that section. Applicable to any document format.

**Why:** Many enterprise document types (legal codes, technical specs, internal wikis) use non-standard heading structures. Regex-driven depth chunking lets operators tune chunking without writing a custom `IChunker`.

**Status:** ✅ Done

---

### Multi-Language Code Splitting (Heuristic)
**Package:** `Rag.NET` (core)

Language-specific separator hierarchies for Python, JS/TS, Java, Go, Ruby, Rust, C#, and more — splitting at class/function/method boundaries before falling back to line/block. Works via regex-based heuristics; no compiler infrastructure required. Complements the Roslyn-based C# chunker for other languages.

**Why:** Generic character splitting ignores code structure. Heuristic splitters work for all languages without per-language compiler dependencies.

**Status:** ✅ Done

---

### C# Semantic Chunking (Roslyn)
**Package:** `Rag.NET.Parsers.CSharp`

Split C# source files into semantically meaningful chunks using Roslyn. Each chunk maps to a single code construct — class, method, interface, enum, delegate, constructor, etc. — and carries: kind, namespace, parent type, identifier name, XML doc summary, source text, and a dependency list (parameter types, base types, property types) for graph-aware retrieval. `CSharpChunkingOptions` controls whether member bodies, private members, and internal members are included.

**Why:** Generic text chunking splits code mid-method or mid-class, destroying semantic meaning.

---

### Domain-Specific Chunking Templates
**Package:** `Rag.NET` (core)

Pre-built chunking templates for common vertical document types:

- **Academic Papers** — two-column layout detection, index from abstract, filter front matter
- **Legal Documents** — detect numbered clause hierarchy, merge sub-clauses under parent article
- **Q&A Pairs** — ingest CSV/Excel rows as question (chunk) + answer (payload) pairs
- **Books** — hierarchical merge with table-of-contents removal
- **Email** — parse `.eml` including attachments, recursively chunk body + attachments
- **Resumes** — parallel LLM extraction of basic info, work history, and education sections

**Why:** Domain templates dramatically reduce noise. Legal documents chunked generically lose clause hierarchy; academic papers mix references into the body. Pre-built templates serve vertical markets out of the box.

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

Combine semantic similarity score with a recency decay factor. Fresher documents receive a score boost, older ones decay. Configurable decay rate. Valuable for knowledge bases where recency matters (support docs, regulatory updates, news).

**Why:** Pure semantic similarity ignores document age entirely.

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

**Status:** ✅ Done

---

### RAPTOR — Recursive Abstractive Tree Summarization
**Package:** `Rag.NET` (core)

Embed chunks, dimensionality-reduce with UMAP, soft-cluster with a Gaussian Mixture Model (BIC selects optimal cluster count), then LLM-summarize each cluster into a new higher-level chunk. Recurse until one cluster remains, building a full summary tree. Store all intermediate summary chunks alongside originals; all levels participate in retrieval simultaneously.

**Why:** Enables retrieval at multiple granularities — high-level theme queries match cluster summaries, fine-grained questions match leaf chunks. Essential for long documents (books, reports, legal corpora) where a flat chunk pool is insufficient.

---

### Deep Research Loop (Sufficiency-Gated Sub-Query Decomposition)
**Package:** `Rag.NET` (core)

After initial retrieval, use an LLM to judge whether the retrieved information is sufficient. If not, generate follow-up sub-queries and explore them recursively to a configurable depth. Merge and deduplicate results across all branches. Optional: integrate live web search in the same loop.

**Why:** Answers complex questions that require discovering what is missing and forming follow-up questions — moves Rag.NET from single-pass retrieval toward autonomous research capability.

**Status:** ✅ Done

---

## Post-Retrieval

### Cohere Rerank
**Package:** `Rag.NET.Reranking.Cohere`

Call Cohere's hosted reranking API as a post-retrieval step. Production-grade managed reranker requiring no local model hosting.

**Why:** Highest-quality managed reranking with a simple API key — no GPU required.

---

## Answer Generation

### Map-Reduce Synthesis
**Package:** `Rag.NET` (core)

Answer questions over large document sets by first mapping an LLM call over each retrieved chunk (partial answers), then reducing with a second LLM call into a final answer. Handles cases where retrieved text collectively exceeds the model's context window. Rag.NET's `AskAsync` currently stuffs all chunks into a single context.

**Why:** Essential for long-document and large-corpus RAG workloads.

**Status:** ✅ Done

---

### Refine (Iterative Synthesis)
**Package:** `Rag.NET` (core)

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
**Package:** `Rag.NET` (core)

Track which document content hashes have been written to which vector store namespace, persisted to a SQL/file store. On re-ingestion: skip truly unchanged documents, re-index modified ones, optionally delete documents whose sources have disappeared (`CleanupMode.Full`). Goes beyond `IngestionOptions.Overwrite` — that flag re-ingests unconditionally; this skips unchanged content.

**Why:** Critical for efficient incremental indexing of large corpora.

---

## Ingestion Sources

### Data Provider Abstraction
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
**Package:** `Rag.NET.DataProviders.Web`

Fetch a seed URL and follow links up to a configurable depth, loading all discovered pages as documents.

**Why:** Covers the common "index all docs on this site" use case without manual URL enumeration.

---

### Sitemap Loader
**Package:** `Rag.NET.DataProviders.Web`

Read a `sitemap.xml` and load all listed URLs. A structured, polite alternative to recursive crawling for sites that publish sitemaps.

**Why:** Simpler and more reliable than link-following for well-maintained sites.

---

### RSS Feed Loader
**Package:** `Rag.NET.DataProviders.Web`

Ingest documents from RSS/Atom feeds, enabling near-real-time ingestion of news, blog posts, and update streams.

**Why:** Easy-to-implement, high-utility source for continuously updated knowledge bases.

---

### SaaS Connectors
**Package:** Various `Rag.NET.DataProviders.*`

Production connectors for cloud and enterprise systems, each exposing `IFileContentProvider` with delta sync where the platform supports it:

- **Collaboration**: Confluence, Notion, Jira, Asana, Airtable
- **Communication**: Slack, Microsoft Teams, Gmail / IMAP
- **Cloud Storage**: Google Drive, Dropbox, Box, Azure Blob, SharePoint, OneDrive
- **Source Control**: GitLab, Bitbucket (incremental delta sync)
- **Support**: Zendesk

**Why:** Enterprise customers store knowledge in Confluence, Notion, SharePoint, and Slack — not on disk. Without connectors, every enterprise deployment requires a custom integration layer.

---

## Multimodal Ingestion

### Image Description via Vision LLM
**Package:** `Rag.NET.Parsers.Vision`

For image files (PNG, JPG, etc.) and embedded figures in PDFs/DOCX: if OCR yields too little text, call a vision LLM (e.g., GPT-4o) to generate a natural-language description. Inject the description as a chunk adjacent to surrounding document text with position metadata. A context-aware variant passes surrounding paragraph text to ground the description.

**Why:** Technical documents convey critical information in diagrams and charts that text-only parsers silently discard.

---

### Video Description via Vision LLM
**Package:** `Rag.NET.Parsers.Vision`

Pass video files (MP4, MOV, MKV) to a vision LLM that generates a textual description of the content, stored as chunks for retrieval.

**Why:** Video content (demo recordings, training videos, presentations) is otherwise invisible to RAG pipelines.

---

### Audio Transcription
**Package:** `Rag.NET.Parsers.Audio`

Transcribe WAV, MP3, FLAC, OGG, and other audio files using [Whisper.net](https://github.com/sandrohanea/whisper.net) — a native .NET binding to OpenAI's Whisper model that runs fully local with no API key. Model size is configurable (`tiny` → `large`) to trade accuracy for speed and memory.

**Why:** Meeting recordings, podcasts, and voice notes are a growing source of enterprise knowledge that text-only pipelines cannot reach.

**Status:** ✅ Done

---

## Knowledge Graph

### GraphRAG — Entity Extraction + Community Summarization
**Package:** `Rag.NET.GraphRag`

Full Microsoft GraphRAG pipeline: LLM-driven entity and relationship extraction from chunks using iterative "gleaning", Leiden community detection (hierarchical graph clustering), PageRank-weighted entity scoring, and LLM-generated community summary reports. At query time, combines dense entity retrieval, relation retrieval by text similarity, and community report retrieval — merged and scored by cosine similarity and PageRank.

**Why:** Multi-hop reasoning and global summarization require graph structure that pure vector search cannot provide.

---

### Mind-Map Extractor
**Package:** `Rag.NET.GraphRag`

Build a hierarchical JSON tree from document content representing the document's conceptual structure. Useful as a structured knowledge representation alongside flat chunk retrieval.

**Why:** Provides a navigable overview of large documents for display, summarization, and structured retrieval.

---

## Management & Observability

### Data Management API
**Package:** `Rag.NET` (core)

A read/delete surface for browsing and managing ingested data via `IRagDataManager`.

- `GetCollectionsAsync()`, `GetSourcesAsync(collectionId)`, `GetChunksAsync(collectionId, sourceId)`
- `DeleteSourceAsync(collectionId, sourceId)`, `DeleteCollectionAsync(collectionId)`
- `GetStatsAsync()` — chunk counts per collection/source

**Hierarchy:** `Collection → Source → Chunk`

**Why:** No way today to inspect or clean up ingested data without going directly to the vector store.

---

### Conversational Memory Management
**Package:** `Rag.NET` (core)

Automatic conversation history management for multi-turn RAG. `ConversationMemoryPipeline` handles windowed trimming and optional LLM summarization. `PersistentConversationMemory` wraps the pipeline and adds cross-session recall: each exchange is embedded and stored in the vector store; relevant past exchanges are retrieved by similarity and injected as a system prefix.

**Why:** Multi-turn RAG is the dominant use case, but `ConversationHistory` is currently a raw list the caller must manage. Without auto-summarization and windowing, conversations either blow the context window or lose important context through naive truncation.

**Status:** ✅ Done

---

## Evaluation

### LLM-as-Judge Evaluation
**Package:** `Rag.NET.Evaluation`
**Status:** ✅ Done

Use `LlmJudgeEvaluator` to grade predicted answers against named criteria (correctness, faithfulness, relevance) using any `IChatClient`. One LLM call per sample, all evaluated concurrently. Results carry per-criterion scores (0–1) and reasoning strings. `LlmJudgeResult.MeanScore(criterion)` and `AllPass(criterion, threshold)` support CI gate patterns. When `SourceChunks` is null or empty, faithfulness is automatically excluded. Custom criteria can be passed to the constructor.

**Why:** Embedding distance gives a single blunt signal that cannot detect hallucinations, factual errors, or off-topic answers. LLM-as-judge closes this gap with interpretable, per-criterion verdicts.

---

## Priority / Dependencies

| Done | Feature | Complexity | Dependencies |
|------|---------|------------|--------------|
| [x] | Azure AI Search Tests via Simulator | Low | `Testcontainers` + simulator Docker image |
| [ ] | Cohere Rerank | Low | Cohere API key |
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
| [ ] | SaaS Connectors | Medium | Per-platform SDK |
| [x] | Search Result Caching | Medium | None |
| [x] | SQLite Persistence for In-Memory Indexes | Medium | `Microsoft.Data.Sqlite` |
| [x] | Tag-Based Retrieval | Medium | Hybrid search |
| [x] | Web Crawler / Sitemap / RSS | Medium | HTTP client |
| [x] | Semantic Chunking (Embedding-Based) | Medium | `IEmbeddingGenerator` |
| [ ] | C# Semantic Chunking (Roslyn) | High | `Microsoft.CodeAnalysis.CSharp` |
| [x] | Deep Research Loop | High | `IChatClient` |
| [ ] | Domain-Specific Chunking Templates | High | Per-domain logic |
| [x] | Ensemble / RRF | High | Multiple retrievers |
| [ ] | Image / Video Description | High | Vision LLM |
| [x] | LLM Metadata Extraction at Ingest | High | `IChatClient` |
| [x] | Conversational Memory Management | High | `IChatClient` + tokenizer |
| [x] | Parent-Document Retrieval | High | Dual index |
| [ ] | RAPTOR | High | UMAP + GMM + `IChatClient` |
| [x] | Self-Query Filtering | High | `IChatClient` + schema |
| [ ] | GraphRAG | Very High | Graph DB + `IChatClient` |
