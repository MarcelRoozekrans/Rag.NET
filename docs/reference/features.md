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
**Package:** `Rag.NET.Chunking.Semantic`

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

### Multi-Language Code Splitting (Heuristic)
**Package:** `Rag.NET.Chunking`

Language-specific separator hierarchies for Python, JS/TS, Java, Go, Ruby, Rust, C#, and more — splitting at class/function/method boundaries before falling back to line/block. Works via regex-based heuristics; no compiler infrastructure required. Complements the Roslyn-based C# chunker for other languages.

**Why:** Generic character splitting ignores code structure. Heuristic splitters work for all languages without per-language compiler dependencies.

**Status:** ✅ Done

---

### C# Semantic Chunking (Roslyn)
**Package:** `Rag.NET.Parsers.CSharp`

Split C# source files into semantically meaningful chunks using Roslyn. Each chunk maps to a single code construct — class, method, interface, enum, delegate, constructor, etc. — and carries: kind, namespace, parent type, identifier name, XML doc summary, source text, and a dependency list (parameter types, base types, property types) for graph-aware retrieval. `CSharpChunkingOptions` controls whether member bodies, private members, and internal members are included.

**Why:** Generic text chunking splits code mid-method or mid-class, destroying semantic meaning.

**Status:** ✅ Done

---

### Domain-Specific Chunking Templates
**Status:** ✅ Done
**Package:** `Rag.NET.Chunking.Templates`

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

**Status:** ✅ Done

---

### RAPTOR — Recursive Abstractive Tree Summarization
**Status:** ✅ Done
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

Implement `IVectorStore` and `ICollectionManageable` backed by Weaviate via the official `WeaviateSharp` or REST client. Supports hybrid search (BM25 + vector), metadata filtering via Weaviate's `where` filter, and multi-tenancy. Registration: `.UseWeaviate(endpoint, collection, vectorDimensions)`.

**Why:** Weaviate is a popular managed vector store with native hybrid search and a generous free tier. Adds a third open-source option alongside PgVector and Qdrant.

---

### Chroma Vector Store
**Package:** `Rag.NET.VectorStores.Chroma`

Implement `IVectorStore` backed by ChromaDB via its REST API. Chroma is the most widely used embedded/local vector store in Python RAG tutorials — a .NET adapter lowers the barrier for teams already running Chroma.

**Why:** Chroma is commonly used in prototyping and local development. A lightweight adapter makes Rag.NET accessible to teams already invested in Chroma.

---

### Pinecone Vector Store
**Package:** `Rag.NET.VectorStores.Pinecone`

Implement `IVectorStore` backed by Pinecone's serverless index via the official REST API. Supports namespace-based collection isolation (maps to `collectionName`), metadata filtering, and sparse-dense hybrid search via Pinecone's native sparse vectors.

**Why:** Pinecone is the dominant managed vector store in production enterprise deployments. Many teams choose Rag.NET for the pipeline but already have Pinecone in their stack.

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
| `Rag.NET.DataProviders.SharePoint` | Microsoft Graph SDK | `deltaLink` token |
| `Rag.NET.DataProviders.OneDrive` | Microsoft Graph SDK | `deltaLink` token |
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
| `Rag.NET.DataProviders.MicrosoftTeams` | Microsoft Graph SDK | `deltaLink` token |
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
**Package:** `Rag.NET.DataProviders`

An `IIngestionTrigger` abstraction that lets connectors push documents to the pipeline reactively rather than polling. Implementations:

- `WebhookIngestionEndpoint` — a minimal ASP.NET Core endpoint that accepts connector webhook payloads (GitHub push events, Notion page updates, Slack message events) and dispatches them as ingestion jobs
- `AzureServiceBusIngestionTrigger` — consumes messages from a Service Bus queue/topic and ingests the referenced documents
- `BackgroundPollingTrigger` — wraps any `IFileContentProvider` in a `BackgroundService` that polls on a configurable schedule (cron expression via `NCrontab`)

**Why:** The current data providers are pull-only — a scheduler or human must kick off re-ingestion. Event-driven ingestion keeps the index current without polling overhead or operator intervention.

---

### Email Connectors (Outlook / Exchange)
**Package:** `Rag.NET.DataProviders.Exchange`

Ingest emails and attachments from Outlook/Exchange via Microsoft Graph (`/me/messages`, `/me/mailFolders`). Supports folder filtering, date-range watermarks, and attachment parsing (delegates to existing parsers for PDF/Word/Excel attachments). Complements the existing Gmail connector.

**Why:** Exchange/Outlook is the dominant enterprise email system. Enterprise RAG over internal communications requires both Gmail and Exchange coverage.

---

### Linear Issue Tracker
**Package:** `Rag.NET.DataProviders.Linear`

Ingest issues, comments, and projects from Linear via the GraphQL API. Supports team filtering, state filtering (active/completed/cancelled), and delta ingestion via `updatedAt` watermark.

**Why:** Linear is the issue tracker of choice for many engineering teams. Ingesting it alongside GitHub and Jira gives complete engineering knowledge coverage.

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

Detect and extract tables from PDFs as structured text rather than flowing prose. Use heuristic line/column detection (via PdfPig's geometry primitives) to reconstruct table rows as pipe-delimited Markdown tables. Each table becomes its own `DocumentSection` with `Heading = "table"` so chunking and retrieval can treat them distinctly.

**Why:** The current PDF parser treats all content as flowing text — tables become garbled sequences of cell values with no row/column structure. This is a known quality gap for financial reports, legal contracts, and technical specifications.

---

### OCR for Scanned PDFs
**Package:** `Rag.NET.Parsers.Pdf`

Add an OCR pass for PDFs where `PdfPig` extracts no text (scanned documents). Integrate `Tesseract` (via `Tesseract.Net`) or delegate to `Azure Document Intelligence` for higher accuracy. Falls back automatically when text extraction yields fewer than a configurable minimum character count per page.

**Why:** A significant portion of enterprise PDFs are scanned — contracts, invoices, legacy reports. The current parser silently produces empty sections for these, with no indication to the caller.

---

### EPUB Parser
**Package:** `Rag.NET.Parsers.Epub`

Parse EPUB files (e-books, exported docs from tools like Notion, Bear, Obsidian) into `DocumentSection` objects by chapter/spine item. Extracts embedded HTML via `VersOne.Epub` and delegates to `HtmlDocumentParser` per chapter.

**Why:** EPUB is common for exported documentation, e-books, and long-form content. There's no parser today.

**Status:** ✅ Done

---

### Email File Parser (EML / MSG)
**Package:** `Rag.NET.Parsers.Email`

Parse `.eml` (RFC 5322) and `.msg` (Outlook) files into sections: subject → heading, body → text, attachments dispatched to the registered parser by content type. Uses `MimeKit` for EML and `MsgReader` for MSG.

**Why:** Email archives are a major enterprise knowledge source. The existing Gmail/Exchange connectors ingest live mailboxes, but `.eml`/`.msg` exports from archives or migrations are unaddressed.

**Status:** ✅ Done (EML only; MSG is a follow-up)

---

## Knowledge Graph

### GraphRAG — Entity Extraction + Community Summarization
**Status:** ✅ Done
**Package:** `Rag.NET.GraphRag`

Full Microsoft GraphRAG pipeline: LLM-driven entity and relationship extraction from chunks using iterative "gleaning", Leiden community detection (hierarchical graph clustering), PageRank-weighted entity scoring, and LLM-generated community summary reports. At query time, combines dense entity retrieval, relation retrieval by text similarity, and community report retrieval — merged and scored by cosine similarity and PageRank.

**Why:** Multi-hop reasoning and global summarization require graph structure that pure vector search cannot provide.

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
**Status:** ✅ Done
**Package:** `Rag.NET.Telemetry`

Instrument the full pipeline with OpenTelemetry `ActivitySource` spans and `Meter` metrics:

- **Spans:** `ragnet.ingest`, `ragnet.chunk`, `ragnet.embed`, `ragnet.store`, `ragnet.retrieve`, `ragnet.rerank`, `ragnet.answer` — each with standard attributes (`document_id`, `chunk_count`, `vector_store`, `model`)
- **Metrics:** `ragnet.ingest.duration`, `ragnet.retrieve.latency`, `ragnet.chunks.retrieved` (histogram), `ragnet.answer.tokens` (counter), `ragnet.embed.batch_size`
- **Semantic Conventions:** follow OpenTelemetry GenAI semantic conventions (`gen_ai.system`, `gen_ai.request.model`, `gen_ai.usage.input_tokens`, etc.)

Registration via `.UseTelemetry()` on the `RagBuilder` — zero overhead when no listener is attached.

**Why:** Production RAG systems need latency breakdowns to answer "is it slow at retrieval or at generation?" and cost visibility via token counters. Currently there is no way to observe what the pipeline is doing without custom logging.

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
**Package:** `Rag.NET.Evaluation`

Implement the four core RAGAS metrics as `IRagEvaluator` implementations alongside the existing `LlmJudgeEvaluator`:

- **Faithfulness** — are all claims in the answer supported by the retrieved chunks? LLM extracts atomic claims and verifies each against sources.
- **Answer Relevance** — does the answer address the question? Generate `n` synthetic questions from the answer, embed them, average cosine similarity to the original question embedding.
- **Context Precision** — are the retrieved chunks relevant? LLM classifies each chunk as relevant/irrelevant to the ground-truth answer; precision = relevant / total.
- **Context Recall** — do the retrieved chunks cover the ground-truth answer? LLM maps each ground-truth statement to a supporting chunk; recall = covered / total.

Each metric is a standalone `IRagEvaluator<T>` so they can be composed into a `RagasEvaluationSuite` that runs all four concurrently and returns a `RagasReport` with per-metric scores and an overall score.

**Why:** LLM-as-judge grades answer quality holistically. RAGAS metrics decompose quality into retrieval and generation components — essential for pinpointing whether failures are retrieval misses or generation errors.

**Status:** ✅ Done

---

### Evaluation Dataset Builder
**Package:** `Rag.NET.Evaluation`

Generate synthetic question-answer pairs from an existing document corpus for offline evaluation. Samples `k` chunks, uses an LLM to generate a question whose answer is grounded in the chunk, optionally generates a ground-truth answer. Output: `IReadOnlyList<EvaluationSample>` ready to feed into any `IRagEvaluator`.

**Why:** Bootstrapping an evaluation dataset from scratch requires manual annotation. Synthetic generation is imperfect but enables rapid iteration — run a bulk eval before/after a retrieval change to detect regressions.

**Status:** ✅ Done

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

Embed the full document (or section) first to capture global context, then split the resulting token-level embeddings into chunks — instead of splitting text first and embedding each chunk independently. Requires a model that exposes token-level embeddings (e.g. `jina-embeddings-v2`). Implements `IDocumentChunkingStrategy`.

**Why:** Standard chunk-then-embed loses cross-chunk context. Late chunking preserves full-document attention during embedding, improving retrieval for references, pronouns, and cross-paragraph reasoning.

---

### Proposition Extraction Chunking
**Package:** `Rag.NET.Chunking`

LLM-driven chunking that decomposes document text into atomic, self-contained propositions — each a single factual claim expressed as a complete sentence. Each proposition becomes its own chunk, making it highly retrievable for specific questions. Implements `IDocumentChunkingStrategy`.

**Why:** Traditional chunks are paragraph-shaped and contain multiple ideas. Proposition chunks are query-shaped — one chunk, one fact — maximising precision at the cost of more chunks and an LLM pass at ingest time.

---

### Sliding Window Chunking with Overlap
**Package:** `Rag.NET.Chunking`

Fixed-size chunks with configurable token overlap between adjacent chunks. The simplest baseline chunking strategy — no LLM, no regex, O(n) time. Useful as a fast fallback or comparison baseline.

**Why:** Despite being the oldest technique, sliding window is still the default in many frameworks and serves as an important performance baseline. Rag.NET currently lacks a first-class implementation.

---

## Retrieval Techniques

### Contextual Compression
**Package:** `Rag.NET.QueryTechniques`

Post-retrieval step that compresses each retrieved chunk to only the content most relevant to the query. Two strategies ship: **extractive** (embedding similarity, no LLM) and **abstractive** (per-chunk parallel LLM rewrite). Stopping criteria are either `KeepTopSentences` (top-N, default 3) or `MaxTokensPerChunk` (token budget via `cl100k_base`). Output is **non-destructive**: compressed text lives on `SearchResult.CompressedText`, the original `Chunk.Text` is preserved. Register with `builder.UseContextualCompression(opts => ...)` (default: answer-engine path) or additionally `builder.UseContextualCompressionInRetrieval()` to also compress retrieval-facing results. Skip per call with `RagOptions.SkipCompression = true`.

**Why:** Retrieved chunks often contain boilerplate or tangential sentences that waste context window space and dilute the signal for the LLM. Compression can halve token usage with minimal faithfulness loss.

---

### Hypothetical Document Embeddings v2 — Multi-Hypothesis
**Package:** `Rag.NET.QueryTechniques`

Extend the existing `HydeQueryTechnique` to generate `n` hypothetical documents (configurable, default 3) and merge their embeddings by averaging before searching. More hypotheses improve recall at low `n` values and reduce the variance introduced by a single bad hypothesis.

**Why:** Single-hypothesis HyDE can degrade when the generated document takes a wrong angle on the query. Multi-hypothesis averaging is more robust and costs only `n` extra embedding calls.

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
**Package:** `Rag.NET.QueryTechniques`

Generate the answer incrementally sentence by sentence. When the model produces a low-confidence token (detected via logprob threshold), pause generation, reformulate a query from the partial answer so far, retrieve fresh context, and continue generation with the new context injected.

**Why:** A single retrieval at query time misses information needed mid-answer. FLARE retrieves exactly when and what is needed — especially useful for long-form generation and multi-step reasoning.

---

### Sparse Embedding Retrieval (SPLADE)
**Package:** `Rag.NET.VectorStores.PgVector` / `Rag.NET.VectorStores.Qdrant`

Generate sparse embedding vectors via SPLADE (Sparse Lexical and Expansion Model) using an ONNX model, stored alongside dense vectors. Retrieval combines sparse and dense scores natively in the vector store (Qdrant supports this natively; PgVector via separate column + RRF merge).

**Why:** SPLADE outperforms BM25 on out-of-vocabulary terms while remaining sparse enough for efficient retrieval. Pairs with dense embeddings for state-of-the-art hybrid search without a separate BM25 index.

---

### Multi-Index Federation
**Package:** `Rag.NET` (core)

A `FederatedVectorStore` that wraps multiple `IVectorStore` instances and merges results via RRF. Enables searching across collections in different vector stores simultaneously — e.g. a private PgVector index plus a shared Qdrant index.

**Why:** Enterprise deployments often have multiple vector stores for different data domains (HR docs in one, engineering docs in another). Federation enables unified search without data migration.

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

A `FallbackChatClient` (implements `IChatClient`) that tries a primary client, catches transient failures or rate limits, and retries with a secondary client. Configurable fallback list with per-client timeout and error classification. Wraps any `IChatClient` transparently.

**Why:** Production RAG systems cannot tolerate a single LLM provider as a hard dependency. A fallback chain from OpenAI → Anthropic → local Ollama gives resilience without changing pipeline code.

**Status:** ✅ Done

---

### Embedding Versioning & Re-indexing
**Package:** `Rag.NET` (core)

Track which embedding model (name + version) produced each stored vector, persisted alongside the content hash. When the embedding model changes, detect stale vectors and re-embed only affected documents. Exposes `RagManager.ReindexStaleCllectionsAsync()` and a CLI command.

**Why:** Switching embedding models (a common upgrade path) currently requires wiping and re-ingesting the entire corpus. Version tracking makes incremental re-indexing possible.

---

### Rate Limiting & Cost Budgeting
**Package:** `Rag.NET` (core)

An `IRateLimiter` abstraction with a token-bucket implementation that throttles embedding and LLM calls to stay within API rate limits. A `ICostBudget` abstraction tracks estimated spend (tokens × price per token) and throws `BudgetExceededException` when a configured daily/monthly limit is reached.

**Why:** Uncontrolled LLM API usage in production can produce surprise invoices. Rate limiting prevents 429 cascades; budgeting provides a hard guardrail for cost-sensitive deployments.

---

### Batch Ingestion Optimiser
**Package:** `Rag.NET` (core)

Parallelise the embedding and storage steps during bulk ingestion: embed chunks in configurable batches (default 100) with `Parallel.ForEachAsync`, bulk-upsert to the vector store rather than one-by-one. Reduces large-corpus ingestion time from O(n) sequential API calls to O(n/batch) parallel calls.

**Why:** Ingesting 100,000 chunks one-at-a-time can take hours. Batched parallel embedding with bulk upsert can reduce this to minutes. The current pipeline is sequential.

**Status:** ✅ Done

---

## Developer Experience

### Rag.NET CLI Tool
**Package:** `Rag.NET.Cli` (dotnet tool)

A `dotnet tool` (`ragnet`) providing:
- `ragnet ingest <path> --store pgvector --connection <cs>` — ingest a folder
- `ragnet ask "<question>"` — interactive query
- `ragnet eval <dataset.json>` — run RAGAS metrics against a dataset
- `ragnet reindex --stale` — re-embed documents with outdated embedding model versions

**Why:** The library is code-first, but operations tasks (ad-hoc ingestion, health checks, re-indexing) are painful to script. A CLI tool makes these accessible without writing a custom harness.

---

### Pipeline Debugger / Trace Viewer
**Package:** `Rag.NET.Diagnostics`

A lightweight `RagDebugMiddleware` for ASP.NET Core that exposes a `/ragnet/trace` endpoint returning a structured JSON trace of the last N pipeline executions: which chunks were retrieved, their scores, what the answer engine received, sanitiser/guard actions, and latency breakdown per stage.

**Why:** Diagnosing why a RAG pipeline gave a bad answer currently requires adding debug logging and re-running. A persistent in-memory trace ring buffer with a JSON viewer endpoint lets developers inspect production traces without code changes.

---

### A/B Testing Framework
**Package:** `Rag.NET.Evaluation`

A `RagAbTester` that runs the same query through two pipeline configurations simultaneously and records results for offline comparison. Supports shadow mode (primary answer returned to caller, secondary run async for evaluation only) and side-by-side mode (both results returned for human review). Integrates with `IRagEvaluator` to score both results automatically.

**Why:** Changing retrieval strategy, chunking size, or reranking has unpredictable quality effects. A/B testing with automatic evaluation scores makes it safe to iterate on pipeline configuration in production.

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
| [ ] | Sliding Window Chunking with Overlap | Low | None |
| [ ] | Hypothetical Document Embeddings v2 | Low | `IChatClient` + `IEmbeddingGenerator` |
| [ ] | EPUB Parser | Low | `VersOne.Epub` |
| [ ] | Email File Parser (EML/MSG) | Low | `MimeKit` + `MsgReader` |
| [ ] | Linear Issue Tracker | Low | Linear GraphQL API |
| [ ] | RAGAS-Style Metrics | Medium | `IChatClient` + `IEmbeddingGenerator` |
| [ ] | Evaluation Dataset Builder | Medium | `IChatClient` |
| [ ] | A/B Testing Framework | Medium | `IRagEvaluator` |
| [ ] | Weaviate Vector Store | Medium | `WeaviateSharp` |
| [ ] | Chroma Vector Store | Medium | Chroma REST API |
| [ ] | Pinecone Vector Store | Medium | Pinecone REST API |
| [ ] | Multi-Index Federation | Medium | `IVectorStore` composition |
| [ ] | PDF Table Extraction | Medium | PdfPig geometry |
| [ ] | OCR for Scanned PDFs | Medium | Tesseract / Azure Doc Intelligence |
| [x] | Contextual Compression | Medium | `IChatClient` or embeddings |
| [x] | Corrective RAG (CRAG) | Medium | `IChatClient` + web search |
| [ ] | Proposition Extraction Chunking | Medium | `IChatClient` |
| [ ] | Webhook / Event-Driven Ingestion | Medium | ASP.NET Core / Service Bus |
| [ ] | OpenTelemetry Tracing & Metrics | Medium | `System.Diagnostics.ActivitySource` |
| [ ] | Email Connector (Outlook/Exchange) | Medium | Microsoft Graph SDK |
| [x] | PII Detection and Redaction | Medium | Regex / `IChatClient` |
| [x] | Role-Based Access Control (RBAC) | Medium | `IRetrievalGuard` extension |
| [x] | Audit Log | Medium | `IAuditLog` + SQLite |
| [ ] | LLM Fallback Chain | Medium | `IChatClient` decorator |
| [ ] | Rate Limiting & Cost Budgeting | Medium | Token bucket |
| [ ] | Batch Ingestion Optimiser | Medium | `Parallel.ForEachAsync` |
| [ ] | Sample Applications | Medium | All packages |
| [ ] | Rag.NET CLI Tool | Medium | `dotnet tool` |
| [ ] | Pipeline Debugger / Trace Viewer | Medium | ASP.NET Core middleware |
| [x] | Adaptive Retrieval (Query Routing) | High | `IChatClient` + classifier |
| [ ] | FLARE | High | `IChatClient` + logprobs |
| [ ] | Sparse Embedding Retrieval (SPLADE) | High | ONNX + vector store |
| [ ] | Late Chunking | High | Token-level embedding model |
| [ ] | Embedding Versioning & Re-indexing | High | Content hash store |
