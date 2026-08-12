---
id: benchmarks
title: Benchmarks
sidebar_position: 1
---

# Rag.NET Benchmarks

**This page measures speed. For accuracy, see [Retrieval Quality](./retrieval-quality.md).** Nothing
here says anything about whether retrieval returns the right chunks; the two are separate pages with
separate names on purpose.

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) v0.15.8 on .NET 10.0.4, Windows 11 (25H2).
Hardware: Intel Core i9-12900HK 2.50 GHz, 20 logical cores.

Run yourself:

```bash
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*"
```

---

## Chunking

`MaxChunkSize = 512, Overlap = 50`. Input sizes approximate character counts.

Re-measured **2026-07-31**, whole table in one run, after Phase 3.16 changed
`RecursiveChunkingStrategy` to pack split parts back towards `MaxChunkSize`.

| Strategy | Input | Mean | Allocated |
|----------|-------|-----:|----------:|
| Fixed | 500 chars | 190 ns | 1.74 KB |
| Fixed | 5 KB | 1.5 μs | 17.05 KB |
| Fixed | 50 KB | 13.7 μs | 160.97 KB |
| Recursive | 500 chars | 188 ns | 1.41 KB |
| Recursive | 5 KB | 4.0 μs | 38.52 KB |
| Recursive | 50 KB | 38.5 μs | 354.21 KB |
| TokenAware | 500 chars | 8.9 μs | 6.13 KB |
| TokenAware | 5 KB | 86 μs | 36.81 KB |
| TokenAware | 50 KB | 853 μs | 389.39 KB |
| C# | 500 chars | 28.4 μs | 24.98 KB |
| C# | 50 KB | 239 μs | 214.16 KB |

**What Phase 3.16 did to `Recursive`, and what it did not.** Packing made it
**faster at every size** — 512 → 188 ns, 5.0 → 4.0 μs, 47.3 → 38.5 μs — because it
emits far fewer chunks and therefore far fewer `TextChunk` allocations. Allocation
moves in *both* directions: down at 500 characters (2.94 → 1.41 KB, fewer chunk
objects) and up on larger inputs (315.54 → 354.21 KB at 50 KB), where the
`StringBuilder` joins that rebuild each packed chunk cost more than the chunk
objects they save.

**Do not read the other three strategies' movement as a change.** Phase 3.16
touched only `Recursive`, yet `Fixed`, `TokenAware` and `C#` all shifted by
10–25% between the two runs on the same hardware. That is run-to-run variance and
toolchain drift, not behaviour: standard deviations here reach ±14% of the mean
(for example `Fixed` at 500 chars is 190 ± 24 ns) and BenchmarkDotNet reported
bimodal distributions for five of the eleven benchmarks. Treat every figure on
this page as an order of magnitude with a wide band, not a number to compare at
one significant figure.

**Reproducing this requires no git worktrees under the repository.** BenchmarkDotNet
searches subfolders for the project it is asked to build and refuses when it finds
two matches, so a leftover worktree containing a second copy of
`Rag.NET.Benchmarks.csproj` makes the whole suite fail in about three seconds with
a message that reads like a build error. Run `git worktree list` first if the suite
exits immediately having executed nothing.

**Notes:**
- `TokenAware` uses `TiktokenTokenizer` (cl100k_base) — encoding/decoding overhead is 20–60× that of character-based strategies.
- The overhead is paid once per chunk window, not per document; most real ingestion time is dominated by embedding API latency.
- Use `TokenAware` when chunk size precision matters (code, URLs, dense text); `Recursive` is the safe default for prose.
- `C#` chunker uses Roslyn syntax analysis — overhead is from source parsing, not chunking logic. Allocations are per-parse.

---

## Semantic Chunking

CPU-only overhead of `SemanticChunkingStrategy`. The embedder is mocked (returns random vectors) to isolate the cosine-similarity merging logic. Baseline is `RecursiveChunkingStrategy` on the same input.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Semantic_Small (500 chars) | 5.8 μs | 5.39 KB |
| Semantic_Large (50 KB) | 115.6 μs | 86.20 KB |

---

## Parsers

| Parser | Input | Mean | Allocated |
|--------|-------|-----:|----------:|
| Text | 1 KB | 907 ns | 9.81 KB |
| Text | 100 KB | 106.1 μs | 403.67 KB |
| Markdown | 1 KB | 1.3 μs | 11.95 KB |
| Markdown | 100 KB | 240.8 μs | 599.43 KB |
| HTML | 5 sections | 51.2 μs | 81.71 KB |
| HTML | 100 sections | 470.1 μs | 615.26 KB |
| CSV | 500 rows | 137.4 μs | 468.55 KB |
| JSON | 100 elements | 32.1 μs | 49.42 KB |

---

## Pipeline (end-to-end ingestion)

50 KB document with mocked embedder and no-op vector store to isolate parse + chunk overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| IngestAsync (50 KB) | 1,220 μs | 16,418 KB |
| RetrieveAsync_HybridBm25 | 22.7 μs | 34.58 KB |

The pipeline benchmark uses `RecursiveChunkingStrategy`. Embedding and vector-store calls are mocked — add your provider's p99 latency for real-world estimates.

---

## Hybrid Search (BM25 fallback)

In-memory BM25 + RRF merge path, activated when `UseHybridSearch = true` and the vector store does not implement `IHybridSearchable`. Dense search is mocked (no-op), BM25 operates on chunks from a pre-ingested 50 KB document (~100 chunks).

| Method | Mean | Allocated |
|--------|-----:|----------:|
| RetrieveAsync_HybridBm25 | 22.7 μs | 34.58 KB |

**Notes:**
- Dense search is mocked (no I/O). Real-world latency is dominated by the vector store query (~10–100 ms p99).
- BM25 uses a `ReaderWriterLockSlim` with concurrent reads — parallel retrieval scales well.
- RRF merge is O(topK log topK) after BM25 scoring.

---

## BM25 Synonym Expansion

CPU overhead of synonym expansion during BM25 `Add` and `Search`. Baseline is `NoSynonyms`. Input sizes: Short (~10 tokens), Medium (~50 tokens), Long (~200 tokens).

| Operation | Input | Synonym Map | Mean | Allocated |
|-----------|-------|-------------|-----:|----------:|
| Add | Short | None | 1.8 μs | 6.14 KB |
| Add | Short | Small (10) | 2.9 μs | 6.55 KB |
| Add | Short | Large (100) | 3.1 μs | 6.55 KB |
| Add | Medium | None | 6.9 μs | 16.57 KB |
| Add | Medium | Small (10) | 12.3 μs | 18.38 KB |
| Add | Medium | Large (100) | 15.1 μs | 18.38 KB |
| Add | Medium | Phrase | 42.1 μs | 72.13 KB |
| Add | Long | None | 27.8 μs | 56.30 KB |
| Add | Long | Small (10) | 48.8 μs | 63.39 KB |
| Add | Long | Large (100) | 51.8 μs | 63.39 KB |
| Search | — | None | 2.2 μs | 7.20 KB |
| Search | — | Small (10) | 2.4 μs | 7.27 KB |
| Search | — | Large (100) | 2.6 μs | 7.27 KB |

---

## Multi-Query Fan-out

CPU-only overhead of the `MultiQueryRetriever` decorator chain: query expansion via `IQueryExpander`, parallel fan-out to inner `VectorStoreRetriever`, and LINQ merge/dedup. Both the query expander and vector store are mocked (zero I/O latency).

| Method | Variants | Mean | Allocated |
|--------|----------|-----:|----------:|
| SingleQuery_Baseline | — | 673 ns | 904 B |
| MultiQuery_3Variants | 3 | 2,188 ns | 4,616 B |
| MultiQuery_5Variants | 5 | 2,828 ns | 6,232 B |

**Notes:**
- Fan-out overhead scales linearly with variant count (one embedding call + one `SearchAsync` call per variant + original).
- Real-world cost is dominated by the LLM expansion call (~50–200 ms p99) and N parallel vector store queries (~10–100 ms p99 each).
- The CPU-only decorator overhead is negligible in production — these numbers measure infrastructure overhead only.
- When the expander fails, the decorator falls back to single-query at no extra cost.

---

## HyDE (Hypothetical Document Embeddings)

CPU-only overhead of the `HydeRetriever` decorator. The hypothetical document generator is mocked (returns a fixed string) to isolate the decorator's option-rewriting and pass-through cost. Embedder and vector store are also mocked.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| NoHyde_Baseline | 633 ns | 904 B |
| WithHyde | 719 ns | 1,288 B |

**Notes:**
- The generator is mocked — these numbers measure only the decorator overhead (option rewriting, embedding text override), not LLM inference.
- Real-world HyDE cost is dominated by the LLM call to generate the hypothetical document (~50–500 ms p99 depending on model and prompt length).
- CPU overhead is negligible compared to the LLM call; the benchmark confirms the decorator adds minimal overhead on top of the generator call.
- When HyDE generation fails, the decorator falls back to the original query embedding at no extra cost.

---

## Redundancy Filter

Post-retrieval cosine-similarity filtering. Embedder is mocked (zero I/O latency) to isolate the CPU-only filter loop over 384-dimensional random vectors with threshold = 0.95.

| TopK | Mean | Allocated |
|------|-----:|----------:|
| 5 | 19.3 μs | 9.2 KB |
| 20 | 118.5 μs | 35.36 KB |

**Notes:**
- Cost scales quadratically with TopK — each new candidate is compared against all already-accepted chunks.
- In production, the filter loop is negligible compared to the re-embedding API call (typically 10–50 ms for a batch of 5–20 texts).
- Use `RedundancyThreshold = 0.95f` (default) for typical prose; lower to 0.85 for highly redundant corpora.

---

## Cross-Encoder Reranking

CPU-only overhead of the `RerankingRetriever` decorator. The reranker is mocked (returns pre-computed scores) to isolate the sort/trim LINQ path. Embedder and vector store are also mocked.

| TopK | Method | Mean | Allocated |
|------|--------|-----:|----------:|
| 5 | No reranking (baseline) | 618 ns | 904 B |
| 5 | With reranking | 785 ns | 1,424 B |
| 20 | No reranking (baseline) | 608 ns | 904 B |
| 20 | With reranking | 784 ns | 1,424 B |

**Notes:**
- The reranker is mocked — these numbers measure only the decorator overhead (sorting, trimming, LINQ), not model inference.
- Real-world reranking cost is dominated by the cross-encoder model (~10–100 ms per query depending on model size and hardware).
- CPU overhead is negligible compared to model inference; the benchmark confirms the decorator adds minimal overhead on top of the reranker call.
- Over-fetch via `CandidateCount` (default: TopK × 3) means the inner retriever returns more candidates, adding a small increase in data transfer.

---

## Cohere Reranker

Measures the serialization, HTTP call, and deserialization path through the Cohere reranker adapter with a stubbed HTTP response. Zero real network I/O.

| Documents | Mean | Allocated |
|----------:|-----:|----------:|
| 10 | 474 μs | 39.19 KB |
| 50 | 675 μs | 97.63 KB |
| 100 | 1,013 μs | 176.93 KB |
| 500 | 4,338 μs | 789.28 KB |
| 1,000 | 4,470 μs | 1,553.68 KB |

---

## Parent-Document Retrieval

CPU-only overhead of the `ParentDocumentRetriever` decorator. The inner retriever is mocked (returns 5 pre-built child results, each with a `_parentKey` metadata entry) and the parent store is `InMemoryParentChunkStore` pre-populated with 5 parent entries (doc1:0 through doc1:4). Zero I/O — these numbers measure only dictionary lookup, deduplication, and result assembly.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| NoParentDocument_Baseline | 624 ns | 904 B |
| WithParentDocument (5 children → 5 parents) | 1,567 ns | 2,888 B |

**Notes:**
- The inner retriever and parent store are mocked (no I/O). Real-world cost is dominated by the vector store query and, if using a remote parent store, the parent text fetch.
- When `UseParentDocument = false`, the decorator passes through immediately — zero overhead on top of the inner retriever call.
- Deduplication (multiple children sharing one parent) reduces result count; over-fetch (`TopK × 3`) compensates so the final list still reaches the requested `TopK`.
- Both vector store query and in-process dictionary lookups are negligible compared to embedding API latency (~10–50 ms) and vector store network latency (~10–100 ms p99).

---

## Telemetry Overhead

`ActivitySource.StartActivity("ragnet.ingest")` overhead under two conditions: no listener attached (the null-return fast path) and a listener registered with `AllData` sampling (full `Activity` allocation path). Validates the "zero overhead when no listener" guarantee provided by the .NET `ActivitySource` API.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| NoListener (baseline) | 3.8 ns | 0 B |
| WithListener | 158 ns | 416 B |

**Notes:**
- When no `ActivityListener` is registered for `Rag.NET`, `StartActivity` returns `null` immediately — no allocation, no object construction.
- When a listener is attached (e.g. an OpenTelemetry SDK exporter), a full `Activity` object is allocated and populated. The cost is ~158 ns and 416 B per span, which is negligible compared to real I/O operations.
- Production deployments without an OTel collector configured pay zero cost for instrumentation calls.
- Run in Release mode to avoid JIT noise: `dotnet run -c Release --project benchmarks/Rag.NET.Benchmarks -- --filter "*TelemetryOverhead*"`.

---

## Search Result Caching

CPU-only overhead of the `EmbeddingCacheRetriever` and `ResultCacheRetriever` decorators backed by `HybridCache`. Both the embedder and vector store are mocked (zero I/O) to isolate the cache lookup and serialization overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| CacheMiss_NoCaching (baseline) | 691 ns | 980 B |
| CacheHit_EmbeddingOnly | 1,274 ns | 1,048 B |
| CacheHit_ResultCache | 1,124 ns | 1,552 B |

**Notes:**
- The embedding-only cache hit (~1.3 μs) skips `IEmbeddingGenerator` but still queries the vector store. The full result cache hit (~1.1 μs) skips the entire pipeline. Both are negligible compared to what they replace: embedding API calls (~10–50 ms) and vector store queries (~10–100 ms).
- The baseline uses `UseCacheResult = false, UseCacheEmbedding = false` with mocked (zero-latency) providers, so it represents the absolute minimum retrieval cost. In production, cache hits eliminate the two most expensive operations in the pipeline.
- `HybridCache` provides L1 in-process cache by default. Add an `IDistributedCache` (Redis, SQL Server) for L2 cross-instance caching.
- Default TTLs: embedding cache = 30 minutes, result cache = 5 minutes. Configure via `UseCaching(o => { o.EmbeddingTtl = ...; o.ResultTtl = ...; })`.

---

## Metadata Serializer

Serialization/deserialization of `DocumentMetadata` via reflection vs. source-generated JSON. Measures round-trip cost for a typical metadata payload.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Serialize (reflection) | 256 ns | 328 B |
| Serialize (source-gen) | 294 ns | 328 B |
| Deserialize (reflection) | 456 ns | 960 B |
| Deserialize (source-gen) | 475 ns | 960 B |

---

## Resilience (FallbackChatClient)

CPU-only cost of the `FallbackChatClient` decorator. The primary client is mocked (returns immediately) and the fallback path is never triggered in the `NoFallback` case; the `WithFallback` case exercises the full try/catch/retry path using a stub that always fails primary and succeeds on fallback.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| GetResponseAsync_NoFallback | 25 ns | 144 B |
| GetResponseAsync_WithFallback | 3,936 ns | 968 B |

---

## Memory (Persistent)

CPU-only overhead of the `PersistentMemoryBehavior` decorator wrapping a retrieval pipeline. Both the vector store and the external memory store are mocked (no I/O).

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Ask_WithoutMemory | 1,108 ns | 2.53 KB |
| Ask_WithPersistentMemory | 1,081 ns | 2.53 KB |

---

## Mind Map Extraction

Cost of extracting a mind-map hierarchy from ingested chunks. `Extract_InMemoryOnly` uses an in-process graph store; `Extract_WithGraphStore` uses an async graph store mock (measures dispatch overhead).

| Method | Depth | Mean | Allocated |
|--------|------:|-----:|----------:|
| Extract_InMemoryOnly | 1 | 899 ns | 3.55 KB |
| Extract_InMemoryOnly | 2 | 2,506 ns | 5.88 KB |
| Extract_InMemoryOnly | 3 | 7,207 ns | 14.36 KB |
| Extract_WithGraphStore | 1 | 85.1 μs | 10.83 KB |
| Extract_WithGraphStore | 2 | 229.9 μs | 35.19 KB |
| Extract_WithGraphStore | 3 | 511.4 μs | 111.14 KB |

---

## RAPTOR

Hierarchical summarization and blended retrieval. `Ingestion_WithRaptor` measures the full UMAP + GMM + LLM summarisation path; `Retrieval_*` methods measure the query-time blending/filtering overhead only (no summarisation).

| Method | Chunks | Mean | Allocated |
|--------|-------:|-----:|----------:|
| Ingestion_WithoutRaptor | 10 | 121 ns | 632 B |
| Ingestion_WithRaptor | 10 | 3.9 ms | 290 KB |
| Ingestion_WithoutRaptor | 50 | 142 ns | 952 B |
| Ingestion_WithRaptor | 50 | 25.9 ms | 1,231 KB |
| Ingestion_WithoutRaptor | 200 | 252 ns | 2,152 B |
| Ingestion_WithRaptor | 200 | 205.7 ms | 12,656 KB |
| Retrieval_Blend | 10 | 51 ns | 368 B |
| Retrieval_Boost | 10 | 369 ns | 1,320 B |
| Retrieval_Filter | 10 | 346 ns | 592 B |
| Retrieval_Blend | 50 | 51 ns | 368 B |
| Retrieval_Boost | 50 | 1,696 ns | 3,304 B |
| Retrieval_Filter | 50 | 819 ns | 880 B |
| Retrieval_Blend | 200 | 50 ns | 368 B |
| Retrieval_Boost | 200 | 6,922 ns | 10,704 B |
| Retrieval_Filter | 200 | 2,738 ns | 1,680 B |

**Notes:**
- UMAP + GMM (cluster selection) dominates ingestion cost; LLM summarisation calls are mocked.
- Retrieval overhead is sub-microsecond for `Blend` and grows linearly with chunk count for `Boost` (scores all chunks) and `Filter`.

---

## GraphRAG

Community detection, PageRank, entity extraction, and graph-aware retrieval. Baseline (`Ingestion_WithoutGraphRag`) is a no-op ingestion step.

| Method | Nodes | Mean | Allocated |
|--------|------:|-----:|----------:|
| Leiden_Detect | 50 | 531 μs | 250 KB |
| PageRank_Compute | 50 | 67 μs | 13 KB |
| Ingestion_WithoutGraphRag | 50 | 6 μs | 712 B |
| Ingestion_WithGraphEntityExtraction | 50 | 376 μs | 131 KB |
| Retrieval_LocalSearch | 50 | 103 μs | 19 KB |
| Retrieval_GlobalSearch | 50 | 26 μs | 6 KB |
| Leiden_Detect | 200 | 2,605 μs | 1,077 KB |
| PageRank_Compute | 200 | 228 μs | 51 KB |
| Ingestion_WithGraphEntityExtraction | 200 | 370 μs | 131 KB |
| Retrieval_LocalSearch | 200 | 134 μs | 50 KB |
| Retrieval_GlobalSearch | 200 | 27 μs | 17 KB |
| Leiden_Detect | 1,000 | 5,457 μs | 8,134 KB |
| PageRank_Compute | 1,000 | 675 μs | 250 KB |
| Ingestion_WithGraphEntityExtraction | 1,000 | 411 μs | 131 KB |
| Retrieval_LocalSearch | 1,000 | 361 μs | 196 KB |
| Retrieval_GlobalSearch | 1,000 | 76 μs | 76 KB |

**Notes:**
- Leiden community detection scales super-linearly with node count — runs offline during ingestion, not on the query path. **These numbers predate #180**, which added the refinement's three well-connectedness constraints and a redraw when a refinement merges nothing; the cost per level and the number of levels both moved, and the table has not been re-recorded since.
- `Ingestion_WithGraphEntityExtraction` cost is dominated by LLM extraction calls (mocked here); real-world cost is 100–500 ms per document.
- `Retrieval_GlobalSearch` generates community summaries via LLM (mocked); real-world cost is 50–200 ms.

---

## Provider Ingestion

End-to-end `IngestFromProviderAsync` overhead — file enumeration, parsing, chunking, embedding, and vector store writes. Embedder and vector store are mocked; `WithDelay` variants use a 15 ms simulated I/O delay per file to model real API latency.

| Method | Files | Mean | Allocated |
|--------|------:|-----:|----------:|
| IngestFromProviderAsync_NoStore | 20 | 3.2 ms | 34.72 KB |
| IngestFromProviderAsync_WarmStore_AllSkipped (ETag hit) | 20 | 11.5 ms | 63.2 KB |
| IngestFromProviderAsync_Sequential_WithDelay | 20 | 309.6 ms | 47.05 KB |
| IngestFromProviderAsync_Parallel4_WithDelay | 20 | 77.6 ms | 45.97 KB |
| IngestFromProviderAsync_ColdStore_AllNew | 20 | 180.3 ms | 185.11 KB |

**Notes:**
- `Parallel4_WithDelay` is ~4× faster than `Sequential_WithDelay` with 4 workers and 15 ms I/O — matches theoretical parallelism for I/O-bound work.
- `WarmStore_AllSkipped` shows the ETag deduplication path: files are enumerated and ETags checked, but no parsing/embedding occurs. The overhead is the ETag lookup loop.

---

## Data Connectors

Benchmarks measure `GetFilesAsync()` enumeration throughput with mocked HTTP/IMAP backends (no network I/O).

### Shared Ingestion (20 items, IterationSetup)

| Connector | Mean | Allocated |
|-----------|-----:|----------:|
| Slack | 41.9 μs | 12.01 KB |
| ZendeskArticles | 222.7 μs | 51.16 KB |
| Confluence | 225.3 μs | 51.67 KB |
| GitLab | 200.0 μs | 38.84 KB |
| Bitbucket | 248.5 μs | 76.48 KB |
| Jira | 290.2 μs | 87.14 KB |
| Gmail | 365.7 μs | 206.78 KB |
| Notion | 456.3 μs | 116.31 KB |
| ZendeskTickets | 828.4 μs | 143.60 KB |
| Airtable | 140.5 μs | 66.68 KB |
| Asana | 2,148.8 μs | 399.08 KB |
| Teams | 1,988.6 μs | 612.83 KB |

### Connector-Specific

| Benchmark | Items | Mean | Allocated |
|-----------|------:|-----:|----------:|
| Confluence — FullTraversal | 20 | 31.1 μs | 34.25 KB |
| Confluence — DeltaTraversal | 20 | 44.3 μs | 34.86 KB |
| Confluence — LargeHtmlBodies | 5 | 192.1 μs | 474.19 KB |
| Jira — FullTraversal | 20 | 44.5 μs | 70.18 KB |
| Jira — DeltaTraversal | 20 | 54.0 μs | 70.86 KB |
| Jira — IssueWithManyComments | 5 (10 comments) | 47.3 μs | 90.43 KB |
| Notion — FullTraversal | 20 | 62.3 μs | 99.24 KB |
| Notion — ManyBlocksPerPage | 5 (50 blocks) | 202.7 μs | 414.87 KB |
| Asana — FullTraversal | 20 | 336.2 μs | 382.34 KB |
| Asana — ManySubtasks | 5 (20 subtasks) | 29.1 μs | 43.82 KB |
| Slack — SingleDayBatch | 20 | 6.5 μs | 12.01 KB |
| Slack — MultiDayBatch | 20 (5 days) | 8.5 μs | 17.09 KB |
| Slack — WithThreadReplies | 10 (3 replies) | 13.5 μs | 29.88 KB |
| Teams — SingleDayBatch | 20 | 551.7 μs | 589.84 KB |
| Teams — MultiDayBatch | 20 (5 days) | 608.0 μs | 595.63 KB |
| Teams — HtmlStripping | 20 | 570.7 μs | 609.62 KB |
| Gmail — FullTraversal | 20 | 391.7 μs | 206.78 KB |
| Gmail — TextBodyOnly | 5 | 227.6 μs | 117.61 KB |
| Gmail — HtmlBodyOnly | 5 | 933.4 μs | 484.05 KB |
| GitLab — FullTraversal | 20 | 143.0 μs | 40.97 KB |
| GitLab — DeltaTraversal | 20 | 149.3 μs | 34.89 KB |
| Bitbucket — FullTraversal | 20 | 33.0 μs | 59.66 KB |
| Bitbucket — DeltaTraversal | 20 | 28.6 μs | 54.45 KB |
| Zendesk — TicketsFullTraversal | 20 (2 comments) | 61.9 μs | 125.85 KB |
| Zendesk — ArticlesFullTraversal | 20 | 18.7 μs | 33.21 KB |
| Zendesk — ArticlesHtmlStripping | 5 (~10 KB HTML) | 78.5 μs | 758.77 KB |
| Airtable — FullTraversal | 20 | 26.0 μs | 48.42 KB |
| Airtable — WithAttachments | 10 (2 attachments) | 42.2 μs | 70.29 KB |
| Airtable — DeltaWithFilter | 20 | 30.2 μs | 48.54 KB |

**Notes:**
- All measurements use mocked HTTP/IMAP backends — no network I/O.
- `Shared Ingestion` uses `[IterationSetup]` for connectors backed by NSubstitute mocks (Gmail, GitLab, Airtable) to prevent call-record accumulation. Times are per-iteration overhead including mock recreation cost.
- Teams allocates significantly more than other connectors because it parses nested HTML activity feeds and resolves display names per message.
- Asana `FullTraversal` is slower than `ManySubtasks` because 20 tasks require 20 separate subtask API calls; `ManySubtasks` uses 5 tasks with 20 subtasks each.
- Gmail `HtmlBodyOnly` is 4× slower than `TextBodyOnly` due to AngleSharp HTML stripping of 5 KB bodies.
