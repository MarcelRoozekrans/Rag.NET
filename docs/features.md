# Rag.NET Feature Backlog

Candidate features for future design and implementation.

---

## Chunking

### Token-Aware Splitting
**Package:** `Rag.NET` (core)

Split text by token count (using `Microsoft.ML.Tokenizers`) rather than character count. `FixedSizeChunking` is currently character-based, which can produce chunks that exceed embedding model token limits on dense text like code or URLs.

**Why:** Embedding models and LLM context windows have token limits, not character limits.

---

### Header-Aware Markdown / HTML Splitting
**Package:** `Rag.NET` (core)

Split Markdown at header boundaries and attach the full header hierarchy (`H1`, `H2`, `H3`) as chunk metadata. Likewise for HTML by heading elements. Retrieved chunks carry structural context usable for display or metadata filtering.

**Why:** Rag.NET's Markdown parser does not propagate heading context into chunk metadata.

---

### Hierarchical Merger (Regex-Driven Tree Chunking)
**Package:** `Rag.NET` (core)

Configurable chunking stage driven by user-supplied regex patterns for each heading level and an integer hierarchy depth. Builds a heading tree and extracts subtrees up to the specified depth as chunks — each chunk starts with its section heading and contains all body text within that section. Applicable to any document format.

**Why:** Many enterprise document types (legal codes, technical specs, internal wikis) use non-standard heading structures. Regex-driven depth chunking lets operators tune chunking without writing a custom `IChunker`.

---

### Multi-Language Code Splitting (Heuristic)
**Package:** `Rag.NET` (core)

Language-specific separator hierarchies for Python, JS/TS, Java, Go, Ruby, Rust, C#, and more — splitting at class/function/method boundaries before falling back to line/block. Works via regex-based heuristics; no compiler infrastructure required. Complements the Roslyn-based C# chunker for other languages.

**Why:** Generic character splitting ignores code structure. Heuristic splitters work for all languages without per-language compiler dependencies.

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

### Multi-Query Retrieval
**Package:** `Rag.NET` (core)

Use an LLM to generate several alternative phrasings of the user's question (typically 3–5 variants), run each against the vector store, then deduplicate and merge results. Addresses the single-embedding limitation: one vector captures only one semantic angle of a question.

**Why:** Significantly broadens recall for ambiguous or multi-faceted questions at the cost of one extra LLM call.

---

### Hypothetical Document Embeddings (HyDE)
**Package:** `Rag.NET` (core)

Use an LLM to generate a hypothetical "ideal answer document" for the query, embed that hypothetical document, and use the resulting vector for similarity search. Since the hypothetical document lives in the same embedding space as indexed content, it retrieves far more relevant passages than embedding the short query itself.

**Why:** Especially powerful for asymmetric retrieval (short query vs. long document).

---

### Parent-Document / Small-Chunk Retrieval
**Package:** `Rag.NET` (core)

Index small chunks for precise embedding matching but return their larger parent documents to the LLM for answer generation. Resolves the tension between embedding precision (favors small chunks) and LLM context richness (favors large context). Requires storing both a fine-grained child index and a coarser parent store.

**Why:** Improves answer quality by giving the LLM richer context while keeping retrieval precision high.

---

### BM25 / In-Memory Keyword Retrieval
**Package:** `Rag.NET` (core)

A lightweight in-memory BM25/TF-IDF retriever that operates without a vector store. Enables hybrid search for stores lacking native BM25 (Qdrant, pgvector) and serves as a fast, zero-infrastructure retrieval baseline for unit tests and small deployments.

**Why:** Enables hybrid retrieval on all vector stores, not just Azure AI Search.

---

### BM25 Synonym Expansion
**Package:** `Rag.NET` (core)

Augment BM25 retrieval with runtime-updatable domain-specific synonym dictionaries (e.g., "MI" → "myocardial infarction", "k8s" → "kubernetes"). Synonyms receive a configurable boost weight lower than the original token. Dictionary updatable at runtime without restart.

**Why:** Domain terminology mismatches silently reduce BM25 recall in specialised corpora (medical, legal, engineering).

---

### Ensemble / Reciprocal Rank Fusion (RRF)
**Package:** `Rag.NET` (core)

Combine results from multiple retrievers (e.g., BM25 + dense vector) using Reciprocal Rank Fusion with configurable per-retriever weights. Unlike Rag.NET's current hybrid search (tied to Azure AI Search), RRF works across all vector stores and allows mixing any two retrieval strategies.

**Why:** RRF consistently outperforms individual retrievers by combining rank signals.

---

### MMR (Maximal Marginal Relevance)
**Package:** `Rag.NET` (core)

Select retrieved documents that are both relevant to the query and maximally dissimilar from each other. Reduces redundancy in retrieved context — especially important when a corpus has many near-duplicate chunks. Operates as an optional retrieval mode alongside existing score-only ranking.

**Why:** Rag.NET currently returns by score only, which can yield repetitive context.

---

### Self-Query / Metadata Filter Generation
**Package:** `Rag.NET` (core)

Use an LLM to translate a natural-language question into a vector search query plus a structured metadata filter expression (e.g., "2023 finance reports" → semantic query + `year=2023 AND category='finance'`). Requires an `AttributeInfo` schema describing which metadata fields and types are available.

**Why:** Rag.NET has no mechanism to automatically derive metadata filters from user questions.

---

### Tag-Based Retrieval Filtering
**Package:** `Rag.NET` (core)

Maintain a "tag knowledge base" of content-tag pairs. At query time, match the user's question against all known tags via hybrid search and inject top-k matching tags as keyword filters for the primary retrieval. Two-stage funnel: tag filtering narrows candidates before full semantic search.

**Why:** Lightweight scoping alternative to self-query — useful when documents carry human-assigned categories (product names, departments, issue types) without requiring an LLM call.

---

### Time-Weighted Retrieval
**Package:** `Rag.NET` (core)

Combine semantic similarity score with a recency decay factor. Fresher documents receive a score boost, older ones decay. Configurable decay rate. Valuable for knowledge bases where recency matters (support docs, regulatory updates, news).

**Why:** Pure semantic similarity ignores document age entirely.

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

---

## Post-Retrieval: Reranking & Compression

### Lost-in-the-Middle Reordering
**Package:** `Rag.NET` (core)

Reorder retrieved documents so the most relevant appear at the beginning and end of the context window, exploiting the known "lost-in-the-middle" phenomenon (Liu et al. 2023) where LLMs pay less attention to content in the middle of long contexts. Zero LLM calls, zero cost.

**Why:** Trivially cheap transformation that consistently improves answer quality on multi-document retrieval.

---

### Redundancy / Clustering Filter
**Package:** `Rag.NET` (core)

Remove near-duplicate retrieved chunks by computing pairwise embedding similarity and suppressing documents too similar to an already-selected one. Reduces context bloat and token consumption without any LLM call.

**Why:** Corpora with many near-duplicate passages waste context window space and degrade answer quality.

---

### Cross-Encoder Reranking
**Package:** `Rag.NET` (core)

After initial vector retrieval (e.g., top-50 candidates), re-score each (query, document) pair with a cross-encoder model that jointly encodes both, producing a much more accurate relevance score. Return only the top-K after reranking. Pluggable via `IDocumentReranker` abstraction with local-model and hosted-API implementations.

**Why:** Bi-encoder similarity (used at retrieval time) trades accuracy for speed; cross-encoder reranking recovers accuracy at the final step.

---

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

---

### Refine (Iterative Synthesis)
**Package:** `Rag.NET` (core)

Process chunks sequentially: generate an initial answer from the first chunk, then iteratively refine by feeding each subsequent chunk plus the running answer to the LLM. More token-efficient than map-reduce for sequential coherence tasks.

**Why:** Handles context-window overflow gracefully with a different trade-off profile than map-reduce.

---

## Document Enrichment

### LLM Metadata Extraction at Ingest
**Package:** `Rag.NET` (core)

Run an LLM over each ingested document to generate representative Q&A pairs or structured metadata tags (topics, entities, document type) and attach them to chunks. At retrieval time, user questions match against these pre-generated questions, yielding much better recall than embedding raw chunk text alone.

**Why:** One of the highest-impact RAG accuracy improvements — applied at index time, zero retrieval overhead.

---

## Indexing Infrastructure

### Search Result Caching
**Package:** `Rag.NET` (core)

Two-level opt-in caching for `RetrieveAsync` / `AskAsync` to reduce embedding and vector search costs on repeated queries.

- `CacheEmbedding` — cache the query embedding vector; skip re-embedding identical queries
- `CacheSearchResult` — cache the full result list; skip the vector store round-trip entirely

```csharp
new RetrievalOptions
{
    CachingStrategy = new SearchCachingStrategy(SearchCachingStrategyType.CacheSearchResult, TimeSpan.FromMinutes(5))
}
```

Backing store: `IMemoryCache` by default; pluggable via `ISearchCache` for distributed caches (Redis, etc.).

**Why:** Repeated identical queries are expensive. Caching avoids redundant embedding and vector search calls without changing the public API.

---

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

### Progress Reporting via IProgress&lt;T&gt;
**Package:** `Rag.NET` (core)

Surface fine-grained ingestion progress to callers via `IProgress<RagProgress>` alongside existing `ILogger` telemetry.

```csharp
record RagProgress(RagProgressStage Stage, string Message, int? Current, int? Total, string? DocumentId);

Task<IngestionResult> IngestAsync(Stream content, DocumentMetadata metadata,
    IngestionOptions? options = null,
    IProgress<RagProgress>? progress = null,
    CancellationToken cancellationToken = default);
```

**Why:** `ILogger` is infrastructure-facing. `IProgress<T>` is caller-facing — enables CLI progress bars, SignalR updates, and test assertions.

---

### Data Management API
**Package:** `Rag.NET` (core)

A read/delete surface for browsing and managing ingested data via `IRagDataManager`.

- `GetCollectionsAsync()`, `GetSourcesAsync(collectionId)`, `GetChunksAsync(collectionId, sourceId)`
- `DeleteSourceAsync(collectionId, sourceId)`, `DeleteCollectionAsync(collectionId)`
- `GetStatsAsync()` — chunk counts per collection/source

**Hierarchy:** `Collection → Source → Chunk`

**Why:** No way today to inspect or clean up ingested data without going directly to the vector store.

---

### Long-Term Conversational Memory
**Package:** `Rag.NET` (core)

Persistent episodic memory store separate from chat history: messages stored in a vector store with both dense and BM25 fields, retrieved by hybrid search. An LLM-based ranking step re-scores memories by recency, relevance, and importance. Survives session boundaries.

**Why:** Enterprise chatbots and agents need stateful memory that persists across sessions, distinct from within-session chat history.

---

## Evaluation

### LLM-as-Judge Evaluation
**Package:** `Rag.NET.Evaluation`

Use an LLM to grade whether a predicted answer is correct given a reference answer or context document. Supports arbitrary rubric criteria (correctness, relevance, conciseness, coherence, harmlessness). Exposed as `IRagEvaluator` returning `EvaluationResult` (score + reasoning).

**Why:** Rag.NET has no evaluation infrastructure, making it impossible to measure answer quality in CI or experimentation.

---

### Embedding Distance Evaluation
**Package:** `Rag.NET.Evaluation`

Score semantic similarity between a predicted answer and a reference answer using cosine distance in embedding space. Lightweight, model-free quality signal suitable for regression testing.

**Why:** Captures paraphrase equivalence without an LLM call — a fast smoke test for answer quality.

---

## Testing Infrastructure

### Azure AI Search Integration Tests via Simulator
**Project:** `tests/Rag.NET.AzureAISearch.Tests`

Replace the env-var-gated `AzureAISearchVectorStoreTests` (which silently skips when `AZURE_SEARCH_ENDPOINT`/`AZURE_SEARCH_API_KEY` are absent) with a [Testcontainers](https://dotnet.testcontainers.org/) fixture backed by the [azure-ai-search-simulator](https://github.com/Ellerbach/azure-ai-search-simulator) Docker image (`ghcr.io/ellerbach/azure-ai-search-simulator:latest`).

The simulator exposes a compatible Azure AI Search REST API surface (index management, document upload, vector search, hybrid search, synonym maps) and works directly with the official `Azure.Search.Documents` SDK. No Azure subscription required.

```csharp
// Before: silently skips in CI
if (_endpoint is null || _apiKey is null) return;

// After: always runs via Testcontainers
await using var container = new AzureAISearchSimulatorBuilder()
    .WithVectorDimensions(3)
    .Build();
await container.StartAsync();
var sut = new AzureAISearchVectorStore(container.GetEndpointUri(), indexName,
    new AzureKeyCredential(container.GetApiKey()), vectorDimensions: 3);
```

**Why:** Rag.NET's pgvector and Qdrant integration tests already run fully locally via Testcontainers. Azure AI Search is the only vector store that requires a live cloud service — closing this gap makes CI green on every machine and PR without any Azure credentials.

---

## Infrastructure & Interoperability

### MCP Server
**Package:** `Rag.NET.Mcp`

Expose `RetrieveAsync` and `AskAsync` as [Model Context Protocol](https://modelcontextprotocol.io/) tools, making Rag.NET consumable by any MCP-compatible LLM host (Claude Desktop, LM Studio, Cursor, etc.) without custom integration code.

**Why:** MCP is becoming the de-facto standard for wiring RAG backends into LLM tool-use.

---

## Priority / Dependencies

| Done | Feature | Complexity | Dependencies |
|------|---------|------------|--------------|
| [ ] | Cohere Rerank | Low | Cohere API key |
| [x] | Embedding Distance Evaluation | Low | `IEmbeddingGenerator` |
| [x] | Header-Aware Markdown/HTML Splitting | Low | Existing Markdown parser |
| [x] | Lost-in-the-Middle Reordering | Low | None |
| [x] | Progress Reporting | Low | None |
| [x] | Redundancy Filter | Low | Embedding access |
| [x] | Token-Aware Splitting | Low | `Microsoft.ML.Tokenizers` |
| [ ] | Audio Transcription | Medium | `Whisper.net` |
| [x] | BM25 Keyword Retrieval | Medium | None |
| [ ] | BM25 Synonym Expansion | Medium | BM25 retriever |
| [ ] | Content-Hash Record Manager | Medium | Persistence store |
| [x] | Cross-Encoder Reranking | Medium | Model or API |
| [ ] | Data Management API | Medium | `IVectorStore` extension |
| [ ] | Data Provider Abstraction | Medium | Existing `IDocumentParser` |
| [ ] | Hierarchical Merger | Medium | None |
| [ ] | HyDE | Medium | `IChatClient` |
| [ ] | LLM-as-Judge Evaluation | Medium | `IChatClient` |
| [ ] | Map-Reduce / Refine Synthesis | Medium | `IChatClient` |
| [x] | MCP Server | Medium | MCP SDK |
| [ ] | MMR Retrieval | Medium | Embedding access |
| [ ] | Multi-Language Code Splitting | Medium | None (regex) |
| [x] | Multi-Query Retrieval | Medium | `IChatClient` |
| [ ] | SaaS Connectors | Medium | Per-platform SDK |
| [ ] | Search Result Caching | Medium | None |
| [ ] | Tag-Based Retrieval | Medium | Hybrid search |
| [ ] | Web Crawler / Sitemap / RSS | Medium | HTTP client |
| [ ] | C# Semantic Chunking (Roslyn) | High | `Microsoft.CodeAnalysis.CSharp` |
| [ ] | Deep Research Loop | High | `IChatClient` |
| [ ] | Domain-Specific Chunking Templates | High | Per-domain logic |
| [ ] | Ensemble / RRF | High | Multiple retrievers |
| [ ] | Image / Video Description | High | Vision LLM |
| [ ] | LLM Metadata Extraction at Ingest | High | `IChatClient` |
| [ ] | Long-Term Conversational Memory | High | Vector store + `IChatClient` |
| [ ] | Parent-Document Retrieval | High | Dual index |
| [ ] | RAPTOR | High | UMAP + GMM + `IChatClient` |
| [ ] | Self-Query Filtering | High | `IChatClient` + schema |
| [ ] | GraphRAG | Very High | Graph DB + `IChatClient` |
| [x] | Azure AI Search Tests via Simulator | Low | `Testcontainers` + simulator Docker image |
| [x] | Decorator Pipeline Refactoring | Medium | None |
