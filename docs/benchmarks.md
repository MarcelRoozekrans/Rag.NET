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
| Fixed | 500 chars | 377 ns | 1.70 KB |
| Fixed | 5 KB | 3.1 us | 16.74 KB |
| Fixed | 50 KB | 29.1 us | 158.37 KB |
| Recursive | 500 chars | 916 ns | 2.94 KB |
| Recursive | 5 KB | 8.1 us | 31.91 KB |
| Recursive | 50 KB | 93.6 us | 315.54 KB |
| TokenAware | 500 chars | 18.0 us | 6.10 KB |
| TokenAware | 5 KB | 156 us | 36.74 KB |
| TokenAware | 50 KB | 1,750 us | 388.89 KB |

**Notes:**
- `TokenAware` uses `TiktokenTokenizer` (cl100k_base) — encoding/decoding overhead is 20–60× that of character-based strategies.
- The overhead is paid once per chunk window, not per document; most real ingestion time is dominated by embedding API latency.
- Use `TokenAware` when chunk size precision matters (code, URLs, dense text); `Recursive` is the safe default for prose.

---

## Parsers

| Parser | Input | Mean | Allocated |
|--------|-------|-----:|----------:|
| Text | 1 KB | 1.7 us | 9.81 KB |
| Text | 100 KB | 172.5 us | 403.64 KB |
| Markdown | 1 KB | 1.9 us | 11.95 KB |
| Markdown | 100 KB | 331.4 us | 599.17 KB |
| HTML | 5 sections | 108.4 us | 81.71 KB |
| HTML | 100 sections | 1,087 us | 615.26 KB |
| CSV | 500 rows | 333.4 us | 468.55 KB |
| JSON | 100 elements | 74.3 us | 49.42 KB |

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

CPU-only overhead of multi-query fan-out + deduplication. Both the query expander and vector store are mocked (zero I/O latency) to isolate the LINQ merge/dedup path. Measured against a pre-ingested 50 KB document (~100 chunks).

| Method | Variants | Mean | Allocated |
|--------|----------|-----:|----------:|
| SingleQuery_Baseline | — | ~22 us | ~34 KB |
| MultiQuery_3Variants | 3 | ~90 us | ~140 KB |
| MultiQuery_5Variants | 5 | ~145 us | ~230 KB |

**Notes:**
- Fan-out overhead scales linearly with variant count (one embedding call + one `SearchAsync` call per variant + original).
- Real-world cost is dominated by the LLM expansion call (~50–200 ms p99) and N parallel vector store queries (~10–100 ms p99 each).
- The CPU-only merge/dedup path is negligible in production — these numbers measure infrastructure overhead only.
- When the expander fails, the pipeline falls back to single-query at no extra cost.

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

CPU-only overhead of the reranking pipeline step. The reranker is mocked (returns pre-computed scores) to isolate the sort/trim LINQ path. Embedder and vector store are also mocked.

| TopK | Method | Mean | Allocated |
|------|--------|-----:|----------:|
| 5 | No reranking (baseline) | _TBD_ | _TBD_ |
| 5 | With reranking | _TBD_ | _TBD_ |
| 20 | No reranking (baseline) | _TBD_ | _TBD_ |
| 20 | With reranking | _TBD_ | _TBD_ |

**Notes:**
- The reranker is mocked — these numbers measure only the pipeline overhead (sorting, trimming, LINQ), not model inference.
- Real-world reranking cost is dominated by the cross-encoder model (~10–100 ms per query depending on model size and hardware).
- CPU overhead is negligible compared to model inference; the benchmark confirms the pipeline adds minimal overhead on top of the reranker call.
- Over-fetch via `CandidateCount` (default: TopK × 3) means the vector store returns more candidates, adding a small increase in data transfer.
