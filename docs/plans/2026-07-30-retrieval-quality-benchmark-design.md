# Retrieval Quality Benchmark Harness — Design (Phase 3.7)

**Date:** 2026-07-30
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.7
**Covers:** the Phase 3.7 roadmap entry — measure retrieval quality against a public benchmark with a
published reference number, so correctness is *demonstrable* rather than asserted

Distinct from `EvaluationDatasetBuilder` (Phase 3.2), which synthesises QA pairs from *your* corpus
and can only show that a change moved a number, never that the number is right. Distinct also from
`benchmarks/Rag.NET.Benchmarks` and `docs/reference/benchmarks.md`, which measure **speed**. This
measures **quality**, and the names are kept apart deliberately.

## 1. Scope: SciFact, one number

~5k documents, runs in seconds, and abstracts short enough that chunk-to-document aggregation is
easy to validate. One number matching a published reference is worth more than five unvalidated
ones, because a harness defect is inherited by every dataset added after it.

Everything else — FiQA, ArguAna as a negative control, EnronQA, the ablation table — waits until
parity holds.

## 2. Parity target: dense retrieval through the real pipeline

The published figure is **nDCG@10 ≈ 0.645** for `all-MiniLM-L6-v2` on SciFact.

Measured through the actual path — embed, store, retrieve — rather than through a component built
for the benchmark. That distinction is why the alternative was rejected: matching Anserini's BM25
figure (≈ 0.665) would have required Porter stemming, an English stopword list and `k1=0.9, b=0.4`,
none of which `InMemoryBm25Index` has, so the harness would have been measuring a benchmark-only
analyzer rather than the library.

Five settings are pinned by tests because **each one shifts the number**:

- BEIR's `title + "\n" + text` concatenation
- mean pooling
- L2 normalisation
- cosine similarity
- in-memory storage, for determinism

## 3. The metrics get verified before they get used

This is the part most likely to go wrong quietly. **A subtly incorrect nDCG can still land inside a
±0.02 band**, and then every dataset added afterwards inherits it while the headline number looks
like evidence.

So nDCG@k, Recall@k and MRR are each pinned against **hand-computed values on tiny fixtures**,
including at least one worked example with a known published result, *before* SciFact is loaded.
Native implementations — no `pytrec_eval` dependency.

Three traps to pin explicitly:

- **IDCG is computed over `min(|relevant|, k)`, not over `k`.** Queries with fewer than *k* relevant
  documents are the common case in SciFact, and getting this wrong shifts every score.
- **Only queries present in qrels are evaluated.** SciFact's test split holds ~300. Scoring queries
  with no qrels entry as zero would dilute the result and read as a retrieval problem.
- **SciFact relevance is binary**, but the gain function must handle graded qrels correctly, because
  FiQA and TREC-COVID come later and a binary-only implementation would silently misrank them.

## 4. The chunk-to-document trap

BEIR is evaluated at **document** level: qrels map `query_id → doc_id` and nDCG@10 ranks documents.
Rag.NET chunks. Ranking *chunks* computes a different quantity that merely resembles nDCG@10.

The order is fixed and tested:

1. map each retrieved chunk to its parent document
2. **max-pool** to one score per document
3. dedupe
4. *then* take the top *k*

Taking top-*k* first gives a different answer. Pinned with a fixture where one document contributes
several chunks, so pool-then-cut and cut-then-pool disagree — a test that passes under both orders
would be watching nothing.

This bites unevenly, which is what makes it dangerous. SciFact abstracts and ArguAna arguments are
mostly single-chunk, so those numbers look plausible either way; FiQA and TREC-COVID have long
documents where the discrepancy is real. A table that is right in the cheap places and wrong in the
expensive ones is worse than no table.

## 5. Parity band: 0.625 ≤ nDCG@10 ≤ 0.665

±0.02, two-sided.

Wide enough to absorb legitimate variation — our chunker may split a long abstract, WordPiece
truncation at 256 tokens differs from whatever the reference run used, and published figures vary
slightly between MTEB and the BEIR paper. Narrow enough that a real defect cannot hide: the
chunk-to-document bug shifts SciFact by considerably more than 0.02.

**Two-sided deliberately.** Scoring materially *above* the published figure is not good news — it
means a leak, most likely qrels reaching the ranker.

## 6. Shape and gating

`src/Rag.NET.Benchmarks.Quality` — loaders, metrics, aggregation.
`tests/Rag.NET.Benchmarks.Quality.Tests` — metric unit tests, plus the env-gated parity test.

**Not under `benchmarks/`**, despite the existing speed benchmarks living there. `RepoConventions`
scans only `src/` and `tests/`, so a project under `benchmarks/` gets neither a tier declaration
guard nor a solution-membership guard — and the entire point of this phase is a number that fails
loudly when it drifts. A console runner for generating docs tables can come later, once there is
more than one dataset to tabulate.

Gated on `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` (both existing precedents) and a new
`RAGNET_BEIR_CACHE` for the dataset cache; skipped otherwise.

Because the project reads `RAGNET_*`, it **must** declare `<RequiresSecrets>true</RequiresSecrets>` —
`RepoConventions` enforces that in both directions, so omitting it fails as loudly as declaring it
falsely. That places the parity run in `nightly.yml`'s env-gated job rather than default CI, which is
what the roadmap asks for: corpus scale here is an embedding-cost problem rather than a disk one.

Datasets download on demand into the cache and are **never vendored**. SciFact's licence is recorded
next to its loader, because BEIR licences differ per dataset.

## 7. Recorded, not fixed: our BM25 is not comparable to published BM25

`InMemoryBm25Index` lowercases and splits. Anserini — which produced BEIR's published BM25 figures —
applies Porter stemming and an English stopword list, and BEIR runs it at `k1=0.9, b=0.4` where ours
hard-codes Lucene's `k1=1.5, b=0.75`. Tokenisation dominates BM25 scores.

Irrelevant to this phase, which is dense-only. It matters for the ablation table the roadmap plans —
baseline dense → **+BM25 hybrid** → +HyDE → +reranker — where that row would be incomparable to any
published BM25 reference **while looking like validation of our BM25**. Recorded as a debt with the
numbers so whoever builds the table knows before they publish it.

## Out of scope

- **Every dataset except SciFact.** FiQA, ArguAna, TREC-COVID, EnronQA all wait for parity.
- **The ablation table.** Needs more than one dataset and the BM25 caveat above resolved.
- **Comparative tables against other libraries.** Legitimate, but only credible with genuinely
  equivalent configuration — same embedding model, chunk size, top-k — which is separate work and
  the part such tables are usually attacked on.
- **A console runner.** Nothing to tabulate yet.
