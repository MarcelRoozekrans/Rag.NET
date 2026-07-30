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

> **Corrected during implementation. BEIR concatenates title and text with a space, not a newline.**
> The bullet list below asserts `title + "\n" + text`, and Task 4 of the implementation plan repeats
> it. Upstream `beir/retrieval/models/sentence_bert.py` declares `sep: str = " "` and builds each
> corpus sentence as `(doc["title"] + sep + doc["text"]).strip()`. The published ≈ 0.645 was produced
> with the space.
>
> Both separators were measured through the shipped harness rather than argued about: **space =
> 0.64593, newline = 0.64907**, a difference of **0.00314**. Both land inside the band, so the parity
> test would have passed either way — which is exactly why this had to be checked against upstream
> rather than inferred from a green run. The space is the default (`BeirLoader.DefaultTitleTextSeparator`)
> and is passed explicitly at the call site, so a later change to the default cannot move the number
> without someone editing the test.
>
> **Corrected during implementation. There was no local dense embedder to build on.** This section
> and §5 both take for granted that Rag.NET could embed text locally. It could not:
> `OnnxTokenEmbeddingGenerator` implements `ITokenEmbeddingGenerator` (token vectors, for late
> chunking) and `OnnxSpladeEncoder` is sparse. There was **no way to run Rag.NET with a local, free,
> offline dense embedder at all**, which is why the phase had to add `OnnxEmbeddingGenerator` to
> `Rag.NET.Embeddings.Onnx` before it could measure anything. It lands in the library rather than in
> the harness because the gap is the library's, not the benchmark's. Three of its details are
> load-bearing for the number below: mean pooling **excludes padding**, `[CLS]` and `[SEP]` **are**
> included in the mean as sentence-transformers includes them, and text is **truncated** at 256
> tokens to match `all-MiniLM-L6-v2`'s `max_seq_length` rather than windowed and stitched.

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

> **Corrected during implementation. §4 and §5 cannot both be true, and §5 is the one that is wrong.**
> The justification below says the band is narrow enough that "a real defect cannot hide: the
> chunk-to-document bug shifts SciFact by considerably more than 0.02". §4 says SciFact abstracts are
> "mostly single-chunk, so those numbers look plausible either way". Those are contradictory claims
> about the same dataset, and §4 is the correct one — it is the reason SciFact was chosen in the
> first place (§1: "abstracts short enough that chunk-to-document aggregation is easy to validate").
>
> The shipped harness makes it starker than "mostly": `SciFactParityTests` indexes **one chunk per
> document**, because that is what BEIR's published figures embed — each corpus entry as one sequence
> truncated at `max_seq_length`. Chunking would measure a configuration 0.645 did not come from. So
> on this dataset max-pooling is a literal no-op, and pool-then-cut and cut-then-pool return the
> identical ranking.
>
> **Therefore, on SciFact, the band does not guard the aggregation order at all.**
> `DocumentRankingTests`' fixture — four chunks from one document among seven, where the two orders
> disagree — is the *only* thing that does. That is an argument **for** the fixture, not against the
> band: the band still guards pooling, normalisation, the separator, the IDCG cap, the exclusion rule
> and whether the whole corpus was indexed, all of which do move this number. But the overstated
> justification must not survive into the documentation, because a band credited with catching a
> defect it cannot catch is the same shape as the vacuous guards this milestone keeps finding.
>
> The failure message in `SciFactParityTests` names the chunk-to-document step first among the things
> to look at on a red run. That stays: it is advice for a *future* dataset run through the same
> harness, where it will bite — not a claim about what this number is watching today.

±0.02, two-sided.

Wide enough to absorb legitimate variation — our chunker may split a long abstract, WordPiece
truncation at 256 tokens differs from whatever the reference run used, and published figures vary
slightly between MTEB and the BEIR paper. Narrow enough that a real defect cannot hide: the
chunk-to-document bug shifts SciFact by considerably more than 0.02.

**Two-sided deliberately.** Scoring materially *above* the published figure is not good news — it
means a leak, most likely qrels reaching the ranker.

## 6. Shape and gating

> **Corrected during implementation. `RequiresSecrets` on the `src` project is inert, and the
> paragraph asserting otherwise is wrong in both halves.** Below: "Because the project reads
> `RAGNET_*`, it **must** declare `<RequiresSecrets>true</RequiresSecrets>` — `RepoConventions`
> enforces that in both directions". `RepoConventions` scans **`tests/*/`** only, and `nightly.yml`
> globs `tests/*/*.csproj`. On a `src` project the property is read by nothing: it is neither
> required, nor enforced, nor does it place anything in any job. Declaring it there would have been a
> decoration that looked like a gate.
>
> The second half is wrong too, and more expensively. `RequiresSecrets` is declared **per project**,
> not per test, so putting the parity test in `tests/Rag.NET.Benchmarks.Quality.Tests` would have
> carried all **70** metric, loader and cache unit tests out of `ci.yml`'s fast gating tier and into
> `nightly.yml`'s advisory job — where a wrong nDCG stops failing pull requests. The whole point of
> the phase is a number that fails loudly when it drifts, and this section's own reasoning about
> `benchmarks/` says exactly that two paragraphs earlier.
>
> **What shipped instead**, as two halves of one decision:
> - The `RAGNET_BEIR_CACHE` read stays in `src/`, on
>   `BeirDatasetCache.ResolveCacheDirectoryFromEnvironment`. The loader and metric tests then read no
>   `RAGNET_*` variable at all — they are handed an explicit temporary directory — so they stay in
>   the gating tier legitimately rather than by omission.
> - The parity test lives in its own project,
>   `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`, which declares `RequiresSecrets` truthfully.
>
> Result: the arithmetic gates on every push, and the one test that needs an 86 MB model, a
> downloaded corpus and several minutes of CPU runs nightly.

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
