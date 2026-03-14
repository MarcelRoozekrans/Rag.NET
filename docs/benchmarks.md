# Rag.NET Benchmarks

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) v0.15.8 on .NET 10.0.4, Windows 11 (25H2).
Hardware: Intel Core i9-12900HK 2.50 GHz, 20 logical cores.

Run yourself:

```bash
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*"
```

---

## Chunking

`MaxChunkSize = 512, Overlap = 50`. Input sizes approximate character counts.

| Strategy | Input | Mean | Allocated |
|----------|-------|-----:|----------:|
| Fixed | 500 chars | 209 ns | 1.70 KB |
| Fixed | 5 KB | 2.0 us | 16.74 KB |
| Fixed | 50 KB | 33.4 us | 158.37 KB |
| Recursive | 500 chars | 520 ns | 2.94 KB |
| Recursive | 5 KB | 4.3 us | 31.91 KB |
| Recursive | 50 KB | 42.5 us | 315.54 KB |
| TokenAware | 500 chars | 10.7 us | 6.10 KB |
| TokenAware | 5 KB | 122 us | 36.74 KB |
| TokenAware | 50 KB | 1,050 us | 388.90 KB |

**Notes:**
- `TokenAware` uses `TiktokenTokenizer` (cl100k_base) — encoding/decoding overhead is 20–60× that of character-based strategies.
- The overhead is paid once per chunk window, not per document; most real ingestion time is dominated by embedding API latency.
- Use `TokenAware` when chunk size precision matters (code, URLs, dense text); `Recursive` is the safe default for prose.

---

## Parsers

| Parser | Input | Mean | Allocated |
|--------|-------|-----:|----------:|
| Text | 1 KB | 1.1 us | 9.81 KB |
| Text | 100 KB | 155.4 us | 403.64 KB |
| Markdown | 1 KB | 1.6 us | 11.95 KB |
| Markdown | 100 KB | 208.0 us | 599.17 KB |
| HTML | 5 sections | 43.3 us | 81.71 KB |
| HTML | 100 sections | 453.0 us | 615.26 KB |
| CSV | 500 rows | 119.3 us | 468.55 KB |
| JSON | 100 elements | 40.3 us | 49.42 KB |

---

## Pipeline (end-to-end ingestion)

50 KB document with mocked embedder and no-op vector store to isolate parse + chunk overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| IngestAsync (50 KB) | 377.9 us | 629.14 KB |

The pipeline benchmark uses `RecursiveChunkingStrategy`. Embedding and vector-store calls are mocked — add your provider's p99 latency for real-world estimates.

---

## Hybrid Search (BM25 fallback)

In-memory BM25 + RRF merge path, activated when `UseHybridSearch = true` and the vector store does not implement `IHybridSearchable`. Dense search is mocked (no-op), BM25 operates on chunks from a pre-ingested 50 KB document (~100 chunks).

| Method | Mean | Allocated |
|--------|-----:|----------:|
| RetrieveAsync_HybridBm25 | 21.91 us | 34.19 KB |

**Notes:**
- Dense search is mocked (no I/O). Real-world latency is dominated by the vector store query (~10–100 ms p99).
- BM25 uses a `ReaderWriterLockSlim` with concurrent reads — parallel retrieval scales well.
- RRF merge is O(topK log topK) after BM25 scoring.

---

## Multi-Query Fan-out

CPU-only overhead of the `MultiQueryRetriever` decorator chain: query expansion via `IQueryExpander`, parallel fan-out to inner `VectorStoreRetriever`, and LINQ merge/dedup. Both the query expander and vector store are mocked (zero I/O latency).

| Method | Variants | Mean | Allocated |
|--------|----------|-----:|----------:|
| SingleQuery_Baseline | — | 180 ns | 656 B |
| MultiQuery_3Variants | 3 | 1,073 ns | 3,504 B |
| MultiQuery_5Variants | 5 | 1,287 ns | 4,704 B |

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
| NoHyde_Baseline | 140.5 ns | 624 B |
| WithHyde | 173.1 ns | 920 B |

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
| 5 | 33.4 us | 9.2 KB |
| 20 | 246.9 us | 35.36 KB |

**Notes:**
- Cost scales quadratically with TopK — each new candidate is compared against all already-accepted chunks.
- In production, the filter loop is negligible compared to the re-embedding API call (typically 10–50 ms for a batch of 5–20 texts).
- Use `RedundancyThreshold = 0.95f` (default) for typical prose; lower to 0.85 for highly redundant corpora.

---

## Cross-Encoder Reranking

CPU-only overhead of the `RerankingRetriever` decorator. The reranker is mocked (returns pre-computed scores) to isolate the sort/trim LINQ path. Embedder and vector store are also mocked.

| TopK | Method | Mean | Allocated |
|------|--------|-----:|----------:|
| 5 | No reranking (baseline) | 101.9 ns | 688 B |
| 5 | With reranking | 188.8 ns | 1,128 B |
| 20 | No reranking (baseline) | 101.1 ns | 688 B |
| 20 | With reranking | 209.6 ns | 1,128 B |

**Notes:**
- The reranker is mocked — these numbers measure only the decorator overhead (sorting, trimming, LINQ), not model inference.
- Real-world reranking cost is dominated by the cross-encoder model (~10–100 ms per query depending on model size and hardware).
- CPU overhead is negligible compared to model inference; the benchmark confirms the decorator adds minimal overhead on top of the reranker call.
- Over-fetch via `CandidateCount` (default: TopK × 3) means the inner retriever returns more candidates, adding a small increase in data transfer.

---

## Search Result Caching

CPU-only overhead of the `EmbeddingCacheRetriever` and `ResultCacheRetriever` decorators backed by `HybridCache`. Both the embedder and vector store are mocked (zero I/O) to isolate the cache lookup and serialization overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| CacheMiss_NoCaching (baseline) | 200.6 ns | 924 B |
| CacheHit_EmbeddingOnly | 734.0 ns | 944 B |
| CacheHit_ResultCache | 1,007.8 ns | 1,328 B |

**Notes:**
- The embedding-only cache hit (~734 ns) skips `IEmbeddingGenerator` but still queries the vector store. The full result cache hit (~1.0 μs) skips the entire pipeline. Both are negligible compared to what they replace: embedding API calls (~10–50 ms) and vector store queries (~10–100 ms).
- The baseline uses `UseCacheResult = false, UseCacheEmbedding = false` with mocked (zero-latency) providers, so it represents the absolute minimum retrieval cost. In production, cache hits eliminate the two most expensive operations in the pipeline.
- `HybridCache` provides L1 in-process cache by default. Add an `IDistributedCache` (Redis, SQL Server) for L2 cross-instance caching.
- Default TTLs: embedding cache = 30 minutes, result cache = 5 minutes. Configure via `UseCaching(o => { o.EmbeddingTtl = ...; o.ResultTtl = ...; })`.
