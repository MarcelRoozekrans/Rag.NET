---
id: benchmarks
title: Benchmarks
sidebar_position: 1
---

# Rag.NET Benchmarks

**This page measures speed. For accuracy, see [Retrieval Quality](./retrieval-quality.md).** Nothing
here says anything about whether retrieval returns the right chunks; the two are separate pages with
separate names on purpose.

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) v0.15.8 on .NET 10.0.11
(SDK 10.0.303), Windows 11 (25H2, build 26200.9168). Hardware: Intel Core i9-12900HK 2.50 GHz,
20 logical and 14 physical cores.

**Whole table re-measured 2026-08-14**, in one 68-minute run of all 113 benchmark methods.

**Read these as orders of magnitude, not as a baseline to diff against.** Two things about this
run are worth knowing before comparing it to anything:

- **The machine was not idle-verified.** It had been running other work for days, and no settle
  probe was taken before the run. Contention of a few percent is the size of many of the effects
  on this page.
- **The runtime moved.** The previous whole-table figures (2026-07-31) were taken on .NET 10.0.4;
  these are on .NET 10.0.11. The median row on this page moved −22% between the two, which is far
  more than any code change here explains, and the runtime bump and the session cannot be
  separated after the fact.

The run-to-run band was measured rather than assumed: two full runs 24 hours apart on this machine,
with no code change to the paths involved, differ by a median of 4.3% and a 90th percentile of 9.6%,
and 17 of 185 rows move by more than 10% on nothing at all. **Treat a move under ~10% as noise.**
Allocation is the trustworthy column — exactly one row of 185 moved allocation by more than 2%
across those two runs — so where a number below is called a real change, it is because the
allocation moved, not the clock.

## The retrieval baseline costs 456 B more than it did

Five independent benchmarks measure a bare `VectorStoreRetriever` call with everything mocked, as
the baseline their decorator is compared against. All five moved by **exactly the same 456 B**
between 2026-07-31 and 2026-08-14 — 904 B → 1,360 B, a 50% increase — with times up 24–40%:

| Baseline | Section | Before | After |
|----------|---------|-------:|------:|
| `SingleQuery_Baseline` | Multi-Query Fan-out | 904 B | 1,360 B |
| `NoHyde_Baseline` | HyDE | 904 B | 1,360 B |
| `RetrieveAsync_NoReranking` (TopK 5) | Cross-Encoder Reranking | 904 B | 1,360 B |
| `RetrieveAsync_NoReranking` (TopK 20) | Cross-Encoder Reranking | 904 B | 1,360 B |
| `NoParentDocument_Baseline` | Parent-Document Retrieval | 904 B | 1,360 B |

An identical figure in five separately-written benchmarks is not measurement error, and allocation
does not drift between sessions. Every decorator's *own* cost above the baseline is unchanged, so
this is the shared retrieval path, not any one decorator — and it propagates: the caching rows
below carry it too.

It was **not** bisected to a commit and is not attributed to one. Candidates in the window include
#151 (`c9e9f801`, bounding retrieved context by length) and #120 (`a89f779e`, typed metadata end to
end), but neither was confirmed and neither should be cited as the cause.

Run yourself:

```bash
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*"
```

---

## Chunking

`MaxChunkSize = 512, Overlap = 50`. Input sizes approximate character counts.

Re-measured **2026-08-14**. Phase 3.16 changed `RecursiveChunkingStrategy` to pack split parts
back towards `MaxChunkSize`; that shape is still visible below, but the figures it was first
recorded against have now been re-measured twice.

| Strategy | Input | Mean | Allocated |
|----------|-------|-----:|----------:|
| Fixed | 500 chars | 162 ns | 1.90 KB |
| Fixed | 5 KB | 1.3 μs | 18.06 KB |
| Fixed | 50 KB | 11.5 μs | 169.64 KB |
| Recursive | 500 chars | 149 ns | 1.48 KB |
| Recursive | 5 KB | 3.3 μs | 39.30 KB |
| Recursive | 50 KB | 30.0 μs | 361.95 KB |
| TokenAware | 500 chars | 7.5 μs | 6.21 KB |
| TokenAware | 5 KB | 72.2 μs | 37.05 KB |
| TokenAware | 50 KB | 707.1 μs | 391.02 KB |
| C# | 500 chars | 23.0 μs | 27.63 KB |
| C# | 50 KB | 186.1 μs | 226.19 KB |

**What Phase 3.16 did to `Recursive` remains the point of this table.** Packing emits far fewer
chunks and therefore far fewer `TextChunk` allocations, which is why `Recursive` beats `Fixed` on
allocation at 500 characters despite doing more work; and why its allocation still exceeds
`Fixed`'s on large inputs, where the `StringBuilder` joins that rebuild each packed chunk cost more
than the chunk objects they save.

**Do not read the uniform speed-up as an optimisation.** Every row here is 14–22% faster than the
2026-07-31 recording — all four strategies, at every size, including the three that no recent
commit touched. That is the runtime bump and the session described at the top of this page, not
behaviour, and it is not claimed as an improvement.

Allocation drifted up slightly in four rows — `Fixed` at 500 chars (+9%), `C#` at 500 chars (+11%),
and `Fixed` at 5 KB and `C#` at 50 KB (+6% each) — while the other seven held to within 2%. That is
above the noise floor for a deterministic measurement, so something did change, but it was not
bisected to a commit and no claim is made about what.

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
| Semantic_Small (500 chars) | 4.6 μs | 5.49 KB |
| Semantic_Large (50 KB) | 89.6 μs | 86.91 KB |

---

## Parsers

| Parser | Input | Mean | Allocated |
|--------|-------|-----:|----------:|
| Text | 1 KB | 557 ns | 9.81 KB |
| Text | 100 KB | 77.7 μs | 403.64 KB |
| Markdown | 1 KB | 740 ns | 11.95 KB |
| Markdown | 100 KB | 135.9 μs | 599.17 KB |
| HTML | 5 sections | 28.2 μs | 70.71 KB |
| HTML | 100 sections | 320.8 μs | 519.38 KB |
| CSV | 500 rows | 88.2 μs | 468.55 KB |
| JSON | 100 elements | 25.5 μs | 49.42 KB |

**The HTML parser now allocates 13–16% less** — 81.71 → 70.71 KB at 5 sections, 615.26 → 519.38 KB
at 100. Text, Markdown, CSV and JSON allocate within 0.5% of their previous figures, so this is
specific to the HTML path rather than a page-wide shift. It was not bisected to a commit.

---

## Pipeline (end-to-end ingestion)

50 KB document with mocked embedder and no-op vector store to isolate parse + chunk overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| IngestAsync (50 KB) | 397.1 μs | 1,519.08 KB |
| RetrieveAsync_HybridBm25 | 6.7 μs | 18.52 KB |

The pipeline benchmark uses `RecursiveChunkingStrategy`. Embedding and vector-store calls are mocked — add your provider's p99 latency for real-world estimates.

**Both rows improved by far more than the measurement band, and the allocation confirms it.**

`RetrieveAsync_HybridBm25` went 22.7 → 6.7 μs and 34.58 → 18.52 KB. That is #137 (`53eb6bce`,
"stop allocating the whole corpus on every dense search") and #140 (`b9cb1270`, "make dense search
2.5–4.3× faster"), both landed 2026-08-11 — the allocation halving is precisely what #137 describes.

`IngestAsync` went 1,220 → 397 μs and **16,418 → 1,519 KB, a 10.8× allocation reduction**. This one
is not attributed. It is certainly real — the figure is 1,555,536 B in one run and 1,555,537 B in
another 24 hours later, so it is not session drift — but nothing in the ~60 commits since
2026-07-31 announces an ingestion allocation change of that size, and it was not bisected. Either a
change had a much larger effect than its commit message suggests, or the previous figure was
mis-recorded. Both remain open; do not cite this as a known optimisation.

---

## Hybrid Search (BM25 fallback)

In-memory BM25 + RRF merge path, activated when `UseHybridSearch = true` and the vector store does not implement `IHybridSearchable`. Dense search is mocked (no-op), BM25 operates on chunks from a pre-ingested 50 KB document (~100 chunks).

| Method | Mean | Allocated |
|--------|-----:|----------:|
| RetrieveAsync_HybridBm25 | 6.7 μs | 18.52 KB |

**Notes:**
- The 3.4× improvement over the 2026-07-31 figure is #137 and #140; see [Pipeline](#pipeline-end-to-end-ingestion) above.
- Dense search is mocked (no I/O). Real-world latency is dominated by the vector store query (~10–100 ms p99).
- BM25 uses a `ReaderWriterLockSlim` with concurrent reads — parallel retrieval scales well.
- RRF merge is O(topK log topK) after BM25 scoring.

---

## BM25 Synonym Expansion

CPU overhead of synonym expansion during BM25 `Add` and `Search`. Baseline is `NoSynonyms`. Input sizes: Short (~10 tokens), Medium (~50 tokens), Long (~200 tokens).

| Operation | Input | Synonym Map | Mean | Allocated |
|-----------|-------|-------------|-----:|----------:|
| Add | Short | None | 1.5 μs | 6.45 KB |
| Add | Short | Small (10) | 2.4 μs | 6.86 KB |
| Add | Short | Large (100) | 2.4 μs | 6.86 KB |
| Add | Medium | None | 5.7 μs | 16.88 KB |
| Add | Medium | Small (10) | 9.5 μs | 18.70 KB |
| Add | Medium | Large (100) | 10.0 μs | 18.70 KB |
| Add | Medium | Phrase | 28.7 μs | 72.44 KB |
| Add | Long | None | 20.3 μs | 56.62 KB |
| Add | Long | Small (10) | 37.8 μs | 63.70 KB |
| Add | Long | Large (100) | 36.8 μs | 63.70 KB |
| Search | — | None | 1.5 μs | 7.20 KB |
| Search | — | Small (10) | 1.6 μs | 7.27 KB |
| Search | — | Large (100) | 1.6 μs | 7.27 KB |

All thirteen rows are 17–38% faster than the 2026-07-31 recording with allocation within 5%
everywhere. Nothing here changed; see the note at the top of the page.

---

## Multi-Query Fan-out

CPU-only overhead of the `MultiQueryRetriever` decorator chain: query expansion via `IQueryExpander`, parallel fan-out to inner `VectorStoreRetriever`, and LINQ merge/dedup. Both the query expander and vector store are mocked (zero I/O latency).

| Method | Variants | Mean | Allocated |
|--------|----------|-----:|----------:|
| SingleQuery_Baseline | — | 833 ns | 1,360 B |
| MultiQuery_3Variants | 3 | 1,858 ns | 5,232 B |
| MultiQuery_5Variants | 5 | 2,325 ns | 6,928 B |

**Notes:**
- `SingleQuery_Baseline` allocates 456 B more than it did on 2026-07-31 (904 → 1,360 B) — see [The retrieval baseline costs 456 B more than it did](#the-retrieval-baseline-costs-456-b-more-than-it-did). The fan-out cost *above* the baseline is unchanged.
- Fan-out overhead scales linearly with variant count (one embedding call + one `SearchAsync` call per variant + original).
- Real-world cost is dominated by the LLM expansion call (~50–200 ms p99) and N parallel vector store queries (~10–100 ms p99 each).
- The CPU-only decorator overhead is negligible in production — these numbers measure infrastructure overhead only.
- When the expander fails, the decorator falls back to single-query at no extra cost.

---

## HyDE (Hypothetical Document Embeddings)

CPU-only overhead of the `HydeRetriever` decorator. The hypothetical document generator is mocked (returns a fixed string) to isolate the decorator's option-rewriting and pass-through cost. Embedder and vector store are also mocked.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| NoHyde_Baseline | 864 ns | 1,360 B |
| WithHyde | 920 ns | 1,784 B |

**Notes:**
- The baseline gained 456 B since 2026-07-31 — see [The retrieval baseline costs 456 B more than it did](#the-retrieval-baseline-costs-456-b-more-than-it-did). HyDE's own overhead on top of it is 424 B, against 384 B before: unchanged within rounding.
- The generator is mocked — these numbers measure only the decorator overhead (option rewriting, embedding text override), not LLM inference.
- Real-world HyDE cost is dominated by the LLM call to generate the hypothetical document (~50–500 ms p99 depending on model and prompt length).
- CPU overhead is negligible compared to the LLM call; the benchmark confirms the decorator adds minimal overhead on top of the generator call.
- When HyDE generation fails, the decorator falls back to the original query embedding at no extra cost.

---

## Redundancy Filter

Post-retrieval cosine-similarity filtering. Embedder is mocked (zero I/O latency) to isolate the CPU-only filter loop over 384-dimensional random vectors with threshold = 0.95.

| TopK | Mean | Allocated |
|------|-----:|----------:|
| 5 | 14.8 μs | 9.20 KB |
| 20 | 71.9 μs | 35.36 KB |

**Notes:**
- Cost scales quadratically with TopK — each new candidate is compared against all already-accepted chunks.
- In production, the filter loop is negligible compared to the re-embedding API call (typically 10–50 ms for a batch of 5–20 texts).
- Use `RedundancyThreshold = 0.95f` (default) for typical prose; lower to 0.85 for highly redundant corpora.

---

## Cross-Encoder Reranking

CPU-only overhead of the `RerankingRetriever` decorator. The reranker is mocked (returns pre-computed scores) to isolate the sort/trim LINQ path. Embedder and vector store are also mocked.

| TopK | Method | Mean | Allocated |
|------|--------|-----:|----------:|
| 5 | No reranking (baseline) | 865 ns | 1,360 B |
| 5 | With reranking | 967 ns | 1,944 B |
| 20 | No reranking (baseline) | 849 ns | 1,360 B |
| 20 | With reranking | 986 ns | 1,944 B |

**Notes:**
- Both baselines gained 456 B since 2026-07-31 — see [The retrieval baseline costs 456 B more than it did](#the-retrieval-baseline-costs-456-b-more-than-it-did). The decorator's own cost is 584 B against 520 B before.
- The reranker is mocked — these numbers measure only the decorator overhead (sorting, trimming, LINQ), not model inference.
- Real-world reranking cost is dominated by the cross-encoder model (~10–100 ms per query depending on model size and hardware).
- CPU overhead is negligible compared to model inference; the benchmark confirms the decorator adds minimal overhead on top of the reranker call.
- Over-fetch via `CandidateCount` (default: TopK × 3) means the inner retriever returns more candidates, adding a small increase in data transfer.

---

## Cohere Reranker

Measures the serialization, HTTP call, and deserialization path through the Cohere reranker adapter with a stubbed HTTP response. Zero real network I/O.

| Documents | Mean | Allocated |
|----------:|-----:|----------:|
| 10 | 151.6 μs | 49.37 KB |
| 50 | 282.2 μs | 120.43 KB |
| 100 | 463.5 μs | 215.83 KB |
| 500 | 1,555 μs | 953.35 KB |
| 1,000 | 3,064 μs | 1,880.07 KB |

**This path traded allocation for speed.** Times fell 31–68% while allocation rose a consistent
21–26% at every document count. A uniform allocation increase across five sizes is a real change,
not session variance. The likely cause is the `ZeroAlloc.Rest` 1.3.1 bump (`41adec98`, #134), which
is what this adapter serialises through — but that was not verified by bisect and the attribution
is a suggestion, not a finding.

Note also that the previous 500 and 1,000 document rows (4,338 and 4,470 μs) were nearly identical
despite twice the work, which the current figures are not. That looks like the older pair, rather
than this one, was the anomaly.

---

## Parent-Document Retrieval

CPU-only overhead of the `ParentDocumentRetriever` decorator. The inner retriever is mocked (returns 5 pre-built child results, each with a `_parentKey` metadata entry) and the parent store is `InMemoryParentChunkStore` pre-populated with 5 parent entries (doc1:0 through doc1:4). Zero I/O — these numbers measure only dictionary lookup, deduplication, and result assembly.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| NoParentDocument_Baseline | 854 ns | 1,360 B |
| WithParentDocument (5 children → 5 parents) | 1,684 ns | 3,544 B |

**Notes:**
- The baseline gained 456 B since 2026-07-31 — see [The retrieval baseline costs 456 B more than it did](#the-retrieval-baseline-costs-456-b-more-than-it-did). The decorator's own cost is 2,184 B against 1,984 B before.
- The inner retriever and parent store are mocked (no I/O). Real-world cost is dominated by the vector store query and, if using a remote parent store, the parent text fetch.
- When `UseParentDocument = false`, the decorator passes through immediately — zero overhead on top of the inner retriever call.
- Deduplication (multiple children sharing one parent) reduces result count; over-fetch (`TopK × 3`) compensates so the final list still reaches the requested `TopK`.
- Both vector store query and in-process dictionary lookups are negligible compared to embedding API latency (~10–50 ms) and vector store network latency (~10–100 ms p99).

---

## Telemetry Overhead

`ActivitySource.StartActivity("ragnet.ingest")` overhead under two conditions: no listener attached (the null-return fast path) and a listener registered with `AllData` sampling (full `Activity` allocation path). Validates the "zero overhead when no listener" guarantee provided by the .NET `ActivitySource` API.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| NoListener (baseline) | 2.7 ns | 0 B |
| WithListener | 120 ns | 416 B |

**Notes:**
- When no `ActivityListener` is registered for `Rag.NET`, `StartActivity` returns `null` immediately — no allocation, no object construction.
- When a listener is attached (e.g. an OpenTelemetry SDK exporter), a full `Activity` object is allocated and populated. The cost is ~120 ns and 416 B per span, which is negligible compared to real I/O operations.
- Production deployments without an OTel collector configured pay zero cost for instrumentation calls.
- Run in Release mode to avoid JIT noise: `dotnet run -c Release --project benchmarks/Rag.NET.Benchmarks -- --filter "*TelemetryOverhead*"`.

---

## Search Result Caching

CPU-only overhead of the `EmbeddingCacheRetriever` and `ResultCacheRetriever` decorators backed by `HybridCache`. Both the embedder and vector store are mocked (zero I/O) to isolate the cache lookup and serialization overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| CacheMiss_NoCaching (baseline) | 878 ns | 1,451 B |
| CacheHit_EmbeddingOnly | 1,290 ns | 1,504 B |
| CacheHit_ResultCache | 1,153 ns | 2,008 B |

**Notes:**
- All three rows allocate 29–48% more than on 2026-07-31. This section sits on the shared retrieval path and inherits its regression — see [The retrieval baseline costs 456 B more than it did](#the-retrieval-baseline-costs-456-b-more-than-it-did). The *relative* benefit of a cache hit is unchanged.
- The embedding-only cache hit (~1.3 μs) skips `IEmbeddingGenerator` but still queries the vector store. The full result cache hit (~1.2 μs) skips the entire pipeline. Both are negligible compared to what they replace: embedding API calls (~10–50 ms) and vector store queries (~10–100 ms).
- The baseline uses `UseCacheResult = false, UseCacheEmbedding = false` with mocked (zero-latency) providers, so it represents the absolute minimum retrieval cost. In production, cache hits eliminate the two most expensive operations in the pipeline.
- `HybridCache` provides L1 in-process cache by default. Add an `IDistributedCache` (Redis, SQL Server) for L2 cross-instance caching.
- Default TTLs: embedding cache = 30 minutes, result cache = 5 minutes. Configure via `UseCaching(o => { o.EmbeddingTtl = ...; o.ResultTtl = ...; })`.

---

## Metadata Serializer

Serialization/deserialization of `DocumentMetadata` via reflection vs. source-generated JSON. Measures round-trip cost for a typical metadata payload.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Serialize (reflection) | 341 ns | 376 B |
| Serialize (source-gen) | 339 ns | 376 B |
| Deserialize (reflection) | 659 ns | 1,352 B |
| Deserialize (source-gen) | 671 ns | 1,352 B |

**This section got slower, and the allocation says it is real.** Serialisation allocates 15% more
(328 → 376 B) and deserialisation 41% more (960 → 1,352 B), with times up 15–45% — against a page
whose median row moved −22%. Allocation does not drift between sessions, so this is a code change,
and it runs against the prevailing direction of every other section here.

Not bisected and not attributed. #120 (`a89f779e`, typed metadata end to end) changed
`DocumentMetadata` inside this window and is the obvious thing to look at first, but it was not
confirmed.

Note also that source-gen no longer serialises measurably slower than reflection, and both
directions now allocate identically across the two modes — which is what you would expect if the
payload itself grew rather than either serialiser changing.

---

## Resilience (FallbackChatClient)

CPU-only cost of the `FallbackChatClient` decorator. The primary client is mocked (returns immediately) and the fallback path is never triggered in the `NoFallback` case; the `WithFallback` case exercises the full try/catch/retry path using a stub that always fails primary and succeeds on fallback.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| GetResponseAsync_NoFallback | 15 ns | 144 B |
| GetResponseAsync_WithFallback | 2,105 ns | 968 B |

Both allocations are unchanged from 2026-07-31. The `WithFallback` time roughly halved (3,936 →
2,105 ns); with allocation identical and the exception-handling path unchanged, this is not read as
an optimisation.

`WithFallback` is the one benchmark on this page that reports an exception to BenchmarkDotNet's
diagnoser. That is by design — `TransientFailingChatClient` returns a failed task so the fallback
path is exercised — not a failed run.

---

## Memory (Persistent)

CPU-only overhead of the `PersistentMemoryBehavior` decorator wrapping a retrieval pipeline. Both the vector store and the external memory store are mocked (no I/O).

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Ask_WithoutMemory | 1,137 ns | 2.92 KB |
| Ask_WithPersistentMemory | 1,143 ns | 2.92 KB |

Both rows allocate 15% more than on 2026-07-31 (2.53 → 2.92 KB) while the times are flat. The
decorator still costs nothing measurable over the pipeline it wraps, which is what this section is
for; the shared increase is the retrieval path, not the memory behaviour.

---

## Mind Map Extraction

Cost of extracting a mind-map hierarchy from ingested chunks. `Extract_InMemoryOnly` uses an in-process graph store; `Extract_WithGraphStore` uses an async graph store mock (measures dispatch overhead).

| Method | Depth | Mean | Allocated |
|--------|------:|-----:|----------:|
| Extract_InMemoryOnly | 1 | 836 ns | 3.62 KB |
| Extract_InMemoryOnly | 2 | 2,745 ns | 6.40 KB |
| Extract_InMemoryOnly | 3 | 8,510 ns | 16.91 KB |
| Extract_WithGraphStore | 1 | 71.6 μs | 10.90 KB |
| Extract_WithGraphStore | 2 | 186.1 μs | 34.16 KB |
| Extract_WithGraphStore | 3 | 456.0 μs | 106.10 KB |

`Extract_InMemoryOnly` at depth 3 allocates 18% more than on 2026-07-31 (14.36 → 16.91 KB) and
depth 2 9% more, with the graph-store rows flat or slightly down. Real but small, unattributed, and
not investigated.

---

## RAPTOR

Hierarchical summarization and blended retrieval. `Ingestion_WithRaptor` measures the full UMAP + GMM + LLM summarisation path; `Retrieval_*` methods measure the query-time blending/filtering overhead only (no summarisation).

| Method | Chunks | Mean | Allocated |
|--------|-------:|-----:|----------:|
| Ingestion_WithoutRaptor | 10 | 65 ns | 664 B |
| Ingestion_WithRaptor | 10 | 2.7 ms | 292 KB |
| Ingestion_WithoutRaptor | 50 | 79 ns | 984 B |
| Ingestion_WithRaptor | 50 | 18.1 ms | 1,231 KB |
| Ingestion_WithoutRaptor | 200 | 124 ns | 2,152 B |
| Ingestion_WithRaptor | 200 | 155.4 ms | 12,657 KB |
| Retrieval_Blend | 10 | 35 ns | 408 B |
| Retrieval_Boost | 10 | 276 ns | 1,408 B |
| Retrieval_Filter | 10 | 258 ns | 632 B |
| Retrieval_Blend | 50 | 36 ns | 408 B |
| Retrieval_Boost | 50 | 1,223 ns | 3,608 B |
| Retrieval_Filter | 50 | 686 ns | 920 B |
| Retrieval_Blend | 200 | 36 ns | 408 B |
| Retrieval_Boost | 200 | 5,402 ns | 11,808 B |
| Retrieval_Filter | 200 | 2,784 ns | 1,720 B |

**This section is the page's calibration.** `Ingestion_WithRaptor` allocates 291.93 KB, 1,231.35 KB
and 12,656.73 KB at the three chunk counts — matching the 2026-07-31 figures to within 0.7%, and
the larger two to within 0.03% — while running 24–30% faster. Effectively identical work, a quarter
to a third less time. Whatever produced that is not code, and it is the same effect visible across
the rest of the page.

**Notes:**
- UMAP + GMM (cluster selection) dominates ingestion cost; LLM summarisation calls are mocked.
- Retrieval overhead is sub-microsecond for `Blend` and grows linearly with chunk count for `Boost` (scores all chunks) and `Filter`.

---

## GraphRAG

Community detection, PageRank, entity extraction, and graph-aware retrieval. Baseline (`Ingestion_WithoutGraphRag`) is a no-op ingestion step.

Re-measured **2026-08-14** against the Leiden implemented by #180 (`55e735d5`). The previous
figures measured the pre-refinement algorithm and are not comparable row-for-row; the notes below
give the one row where the difference is unambiguous.

| Method | Nodes | Mean | Allocated |
|--------|------:|-----:|----------:|
| Leiden_Detect | 50 | 436 μs | 238 KB |
| PageRank_Compute | 50 | 53 μs | 13 KB |
| Ingestion_WithoutGraphRag | 50 | 2.9 μs | 744 B |
| Ingestion_WithGraphEntityExtraction | 50 | 471 μs | 183 KB |
| Retrieval_LocalSearch | 50 | 68 μs | 19 KB |
| Retrieval_GlobalSearch | 50 | 15 μs | 6 KB |
| Leiden_Detect | 200 | 1,933 μs | 1,210 KB |
| PageRank_Compute | 200 | 186 μs | 51 KB |
| Ingestion_WithoutGraphRag | 200 | 3.4 μs | 744 B |
| Ingestion_WithGraphEntityExtraction | 200 | 446 μs | 183 KB |
| Retrieval_LocalSearch | 200 | 133 μs | 49 KB |
| Retrieval_GlobalSearch | 200 | 21 μs | 18 KB |
| Leiden_Detect | 1,000 | 11,853 μs | 8,771 KB |
| PageRank_Compute | 1,000 | 621 μs | 250 KB |
| Ingestion_WithoutGraphRag | 1,000 | 3.5 μs | 744 B |
| Ingestion_WithGraphEntityExtraction | 1,000 | 466 μs | 183 KB |
| Retrieval_LocalSearch | 1,000 | 439 μs | 196 KB |
| Retrieval_GlobalSearch | 1,000 | 44 μs | 76 KB |

**Notes:**
- **`Leiden_Detect` at 1,000 nodes more than doubled: 5,457 μs → 11,853 μs.** This is the cost of
  #180 — the refinement's three well-connectedness constraints, plus the redraw when a refinement
  merges nothing. It buys the connected communities the old implementation did not guarantee. The
  effect is super-linear in node count and is not visible at 50 or 200 nodes, where the run-to-run
  band on this machine (±10%, measured) covers the whole difference. Budget for it: community
  detection runs offline during ingestion, not on the query path.
- `Ingestion_WithGraphEntityExtraction` allocates 40% more than before (131 → 183 KB) at every node
  count, with no matching change in the extraction path here. Allocation is deterministic and does
  not drift between sessions, so this is a real change — but it was not bisected to a commit and is
  not attributed to one.
- `Ingestion_WithoutGraphRag` is now recorded at all three node counts; previously only the 50-node
  row was published.
- `Ingestion_WithGraphEntityExtraction` cost is dominated by LLM extraction calls (mocked here); real-world cost is 100–500 ms per document.
- `Retrieval_GlobalSearch` generates community summaries via LLM (mocked); real-world cost is 50–200 ms.

---

## Provider Ingestion

End-to-end `IngestFromProviderAsync` overhead — file enumeration, parsing, chunking, embedding, and vector store writes. Embedder and vector store are mocked; `WithDelay` variants use a 15 ms simulated I/O delay per file to model real API latency.

| Method | Files | Mean | Allocated |
|--------|------:|-----:|----------:|
| IngestFromProviderAsync_NoStore | 20 | 1.8 ms | 40.71 KB |
| IngestFromProviderAsync_WarmStore_AllSkipped (ETag hit) | 20 | 7.5 ms | 63.56 KB |
| IngestFromProviderAsync_Sequential_WithDelay | 20 | 319.6 ms | 53.53 KB |
| IngestFromProviderAsync_Parallel4_WithDelay | 20 | 79.7 ms | 53.35 KB |
| IngestFromProviderAsync_ColdStore_AllNew | 20 | 128.6 ms | 190.23 KB |

The two `WithDelay` rows are pinned by their 15 ms simulated I/O and moved 3%, which is the best
evidence on this page that the harness itself is sound. The three CPU-bound rows fell 29–45%.

**Notes:**
- `Parallel4_WithDelay` is ~4× faster than `Sequential_WithDelay` with 4 workers and 15 ms I/O — matches theoretical parallelism for I/O-bound work.
- `WarmStore_AllSkipped` shows the ETag deduplication path: files are enumerated and ETags checked, but no parsing/embedding occurs. The overhead is the ETag lookup loop.

---

## Data Connectors

Benchmarks measure `GetFilesAsync()` enumeration throughput with mocked HTTP/IMAP backends (no network I/O).

### Shared Ingestion (20 items, IterationSetup)

| Connector | Mean | Allocated |
|-----------|-----:|----------:|
| Slack | 31.4 μs | 12.91 KB |
| ZendeskArticles | 144.9 μs | 41.64 KB |
| Confluence | 160.6 μs | 42.99 KB |
| GitLab | 105.3 μs | 48.73 KB |
| Bitbucket | 175.6 μs | 67.36 KB |
| Jira | 243.1 μs | 87.72 KB |
| Gmail | 221.5 μs | 219.70 KB |
| Notion | 342.6 μs | 109.59 KB |
| ZendeskTickets | 342.6 μs | 145.27 KB |
| Airtable | 119.0 μs | 59.87 KB |
| Asana | 1,644.6 μs | 392.36 KB |
| Teams | 1,003.6 μs | 587.73 KB |

Every row in this table runs at `InvocationCount=1, UnrollFactor=1`. Declaring `[IterationSetup]`
makes BenchmarkDotNet do that for the whole class, including the connectors whose setup body does
nothing — so no row here amortises fixed per-iteration overhead the way the rest of the page does.
Treat these as relative ordering between connectors, not as absolute per-item costs, and do not
compare them against sections measured at `UnrollFactor=16`.

### Connector-Specific

| Benchmark | Items | Mean | Allocated |
|-----------|------:|-----:|----------:|
| Confluence — FullTraversal | 20 | 21.5 μs | 42.89 KB |
| Confluence — DeltaTraversal | 20 | 22.1 μs | 43.50 KB |
| Confluence — LargeHtmlBodies | 5 | 156.2 μs | 476.35 KB |
| Jira — FullTraversal | 20 | 36.0 μs | 87.72 KB |
| Jira — DeltaTraversal | 20 | 35.4 μs | 88.40 KB |
| Jira — IssueWithManyComments | 5 (10 comments) | 34.4 μs | 94.85 KB |
| Notion — FullTraversal | 20 | 50.2 μs | 108.81 KB |
| Notion — ManyBlocksPerPage | 5 (50 blocks) | 131.1 μs | 417.28 KB |
| Asana — FullTraversal | 20 | 189.4 μs | 391.89 KB |
| Asana — ManySubtasks | 5 (20 subtasks) | 23.3 μs | 46.25 KB |
| Slack — SingleDayBatch | 20 | 5.3 μs | 12.91 KB |
| Slack — MultiDayBatch | 20 (5 days) | 7.0 μs | 21.56 KB |
| Slack — WithThreadReplies | 10 (3 replies) | 9.7 μs | 30.79 KB |
| Teams — SingleDayBatch | 20 | 370.4 μs | 580.50 KB |
| Teams — MultiDayBatch | 20 (5 days) | 378.8 μs | 589.90 KB |
| Teams — HtmlStripping | 20 | 400.8 μs | 596.85 KB |
| Gmail — FullTraversal | 20 | 217.2 μs | 219.70 KB |
| Gmail — TextBodyOnly | 5 | 137.4 μs | 117.32 KB |
| Gmail — HtmlBodyOnly | 5 | 692.8 μs | 466.72 KB |
| GitLab — FullTraversal | 20 | 105.1 μs | 48.73 KB |
| GitLab — DeltaTraversal | 20 | 103.9 μs | 51.88 KB |
| Bitbucket — FullTraversal | 20 | 27.7 μs | 67.36 KB |
| Bitbucket — DeltaTraversal | 20 | 26.6 μs | 71.37 KB |
| Zendesk — TicketsFullTraversal | 20 (2 comments) | 54.5 μs | 144.33 KB |
| Zendesk — ArticlesFullTraversal | 20 | 16.7 μs | 41.54 KB |
| Zendesk — ArticlesHtmlStripping | 5 (~10 KB HTML) | 54.4 μs | 760.88 KB |
| Airtable — FullTraversal | 20 | 123.9 μs | 59.87 KB |
| Airtable — WithAttachments | 10 (2 attachments) | 206.1 μs | 93.31 KB |
| Airtable — DeltaWithFilter | 20 | 121.0 μs | 60.00 KB |

**The three Airtable rows moved by 3–5×, and it is not clear why.** They are much the largest
movement on the page: 26.0 → 123.9 μs, 42.2 → 206.1 μs, 30.2 → 121.0 μs.

This is not session variance — two independent runs 24 hours apart agree to within 8% — and it is
not a harness change, since `AirtableBenchmarks` has declared `[IterationSetup]` since 2026-04-13.

What the new figures do is make Airtable *self-consistent*. `Airtable — FullTraversal` and the
`Airtable` row in Shared Ingestion enumerate the same 20 items through the same provider, and now
report 123.9 μs / 59.87 KB against 119.0 μs / 59.87 KB — the same work, the same allocation to the
byte. In the 2026-07-31 table those two rows read 26.0 μs and 140.5 μs, a 5.4× disagreement that
nothing in the code explains. Airtable is also measured at `InvocationCount=1`, where GitLab and
Gmail — the other two NSubstitute connectors — have always sat above 100 μs.

So the likelier reading is that the old Airtable figures were the anomaly rather than these. That
is a reading, not a finding: it was not bisected, and it should be settled by measurement rather
than by argument before anyone relies on either number.

**Notes:**
- All measurements use mocked HTTP/IMAP backends — no network I/O.
- `Shared Ingestion` uses `[IterationSetup]` for connectors backed by NSubstitute mocks (Gmail, GitLab, Airtable) to prevent call-record accumulation. Times are per-iteration overhead including mock recreation cost.
- Teams allocates significantly more than other connectors because it parses nested HTML activity feeds and resolves display names per message.
- Asana `FullTraversal` is slower than `ManySubtasks` because 20 tasks require 20 separate subtask API calls; `ManySubtasks` uses 5 tasks with 20 subtasks each.
- Gmail `HtmlBodyOnly` is 5× slower than `TextBodyOnly` due to AngleSharp HTML stripping of 5 KB bodies.
