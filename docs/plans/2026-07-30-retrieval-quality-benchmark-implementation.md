# Retrieval Quality Benchmark Harness Implementation Plan (Phase 3.7)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Prove SciFact nDCG@10 lands within 0.625–0.665 of the published ~0.645 for `all-MiniLM-L6-v2`, measured through Rag.NET's real embed → store → retrieve path.

**Architecture:** Part A fills a gap the design assumed away — there is no local dense `IEmbeddingGenerator` in this repository, so one is added to `Rag.NET.Embeddings.Onnx`. Part B builds the harness: native IR metrics verified against hand-computed values before use, chunk-to-document aggregation, BEIR loaders, and one env-gated parity test.

**Tech Stack:** .NET 10, ONNX Runtime, `Microsoft.Extensions.AI` abstractions, xUnit v3.

**Design:** `docs/plans/2026-07-30-retrieval-quality-benchmark-design.md`. Read §3 (why metrics are verified before use) and §4 (the chunk-to-document trap) before writing anything.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matches file), MA0006 (`string.Equals` not `==`), MA0008, MA0009, MA0132, MA0140, ZA0601 (no `GroupBy`/`OrderBy`/`ToList` inside a loop — **this will bite in the metrics**), ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ001/HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060. **No new `#pragma` or `SuppressMessage`.**
- All logging through `LoggerMessage` source-gen.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One commit per task.
- **Never `git add -A` or `git add .`** — explicit paths. `.claude/worktrees/` is untracked; leave it.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.

Baselines: `Rag.NET.Tests` **1325**, `Rag.NET.Parsers.Email.Tests` **76**, `Rag.NET.Chunking.Templates.Tests` **51**, `Rag.NET.RepoConventions.Tests` **9**. Any drop is a regression.

**Two conventions will fail loudly if forgotten.** Every new project must be added to `Rag.NET.slnx` (`<Project Path="..." />` in the `/src/` and `/tests/` blocks) — `RepoConventions` asserts `EverySourceProjectIsInTheSolution` and `EveryTestProjectIsInTheSolution`. And a test project that reads a `RAGNET_*` variable **must** declare `<RequiresSecrets>true</RequiresSecrets>`; the conventions suite checks that in both directions, so a missing declaration and a stale one fail equally.

**Timestamp trap:** build without `--no-build` and confirm from the log that projects recompiled before trusting a `--no-build` result.

---

# Part A — the missing dense embedder

## Task 1: `OnnxEmbeddingGenerator`

**Files:**
- Create: `src/Rag.NET.Embeddings.Onnx/OnnxEmbeddingGenerator.cs`, `OnnxEmbeddingOptions.cs`
- Modify: `src/Rag.NET.Embeddings.Onnx/RagBuilderExtensions.cs`
- Create: `tests/Rag.NET.Embeddings.Onnx.Tests/` if it does not exist — **check first**; if it does, extend it

The design assumed a local dense embedder existed. It does not: `OnnxTokenEmbeddingGenerator` implements `ITokenEmbeddingGenerator` (token vectors, for late chunking) and `OnnxSpladeEncoder` is sparse. There is currently **no way to run Rag.NET with a local, free, offline dense embedder at all**, which is why this lands in the library rather than in the benchmark.

Implement `IEmbeddingGenerator<string, Embedding<float>>` from `Microsoft.Extensions.AI`. Reuse `OnnxTokenEmbeddingGenerator`'s tokenizer and session plumbing rather than duplicating it — read that file first and decide whether to compose it or extract the shared parts, and say which you chose and why.

**Mean pooling and L2 normalisation are the load-bearing details.** Mean-pool over the token axis **excluding padding**, then L2-normalise. Padding inclusion is the classic error here and it shifts every downstream number: a test must pin that two texts of different lengths, one padded, produce the vector the unpadded one would.

Register through `RagBuilderExtensions` following the existing sibling methods' shape. Sanity-check what `EmbeddingModelIdentity` expects — the ingestion path records model identity, and a generator that reports nothing there will surface later as a re-indexing bug rather than here.

Tests: deterministic unit tests for pooling and normalisation on synthetic tensors (no model file needed), plus one env-gated smoke test on the pattern in `tests/Rag.NET.Chunking.IntegrationTests/LateChunkingIntegrationTests.cs:28-34` — `Assert.SkipWhen` on `RAGNET_ONNX_EMBED_MODEL` / `RAGNET_ONNX_EMBED_VOCAB`. If the test project reads those variables, declare `RequiresSecrets`.

**Commit:** `feat(onnx): a local dense embedding generator`

---

# Part B — the harness

## Task 2: IR metrics, verified against arithmetic you can check by hand

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/Rag.NET.Benchmarks.Quality.csproj`, `IrMetrics.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/` + `IrMetricsTests.cs`
- Modify: `Rag.NET.slnx`

**Write the tests first, and use values you can verify with a calculator.** A subtly wrong nDCG can land inside the ±0.02 parity band, and then every dataset added later inherits it while the headline number looks like evidence. These four cases pin the shape exactly:

| Relevant docs at ranks | nDCG@10 | Why |
|---|---|---|
| one, rank 1 | `1.0` | perfect |
| one, rank 2 | `1 / log2(3)` ≈ **0.63093** | single-gain discount |
| two, ranks 1 and 2 | `1.0` | both retrieved in ideal order |
| two, ranks 1 and 3 | `(1 + 1/log2(4)) / (1 + 1/log2(3))` ≈ **0.91972** | IDCG has two gains, DCG's second is discounted further |

Pin these to 5 decimal places. If your implementation gives a round number where the table gives 0.63093, you have IDCG over `k` rather than over `min(|relevant|, k)`.

**The three traps, each its own test:**

1. **IDCG over `min(|relevant|, k)`, not over `k`.** One relevant document and `k = 10` gives IDCG = 1, not a sum over ten positions. This is the common case in SciFact.
2. **Only queries present in qrels are evaluated.** A query absent from qrels is *excluded*, not scored zero. Scoring it zero dilutes the mean and reads as a retrieval failure.
3. **Graded relevance works**, even though SciFact is binary — gain must be `2^rel - 1` (or the convention you document), so FiQA and TREC-COVID are not silently misranked later. Pin with a graded fixture.

Also implement `Recall@k` and `MRR`, each with hand-checkable fixtures. Recall@k denominator is `|relevant|`, **not** `min(|relevant|, k)` — the opposite of IDCG, which is why they get separate tests.

**ZA0601 forbids `OrderBy`/`ToList` inside a loop.** The metrics rank per query in a loop; expect to restructure rather than suppress.

**Commit:** `feat(quality): IR metrics pinned to hand-computed values`

---

## Task 3: chunk-to-document aggregation

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/DocumentRanking.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/DocumentRankingTests.cs`

BEIR is evaluated at **document** level; Rag.NET retrieves **chunks**. The order is fixed:

1. map each retrieved chunk to its parent document
2. **max-pool** to one score per document
3. dedupe
4. *then* take the top *k*

**The fixture must make the two orders disagree.** Build a case where one document contributes several chunks with mixed scores, such that pool-then-cut and cut-then-pool produce different top-*k* lists — then assert the pool-then-cut answer. A fixture that passes under both orderings is watching nothing, which is the failure mode this task exists to prevent.

Write that test first and **verify it fails against a deliberately cut-then-pool implementation** before writing the correct one. Report what you observed.

**Commit:** `feat(quality): chunk-to-document max-pooling before top-k`

---

## Task 4: BEIR loaders and the dataset cache

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/BeirDataset.cs`, `BeirLoader.cs`, `BeirDatasetCache.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/BeirLoaderTests.cs`

BEIR's on-disk shape: `corpus.jsonl` (`_id`, `title`, `text`), `queries.jsonl` (`_id`, `text`), `qrels/test.tsv` (`query-id`, `corpus-id`, `score`, with a header row).

- **`title + "\n" + text` concatenation** is BEIR's convention and shifts the number. Pin it.
- The TSV **has a header** — skipping it or not is a silent off-by-one on the first query.
- Download on demand into `RAGNET_BEIR_CACHE`, **never vendored into the repo**. Verify the archive before trusting it; a truncated download must fail loudly rather than yield a short corpus that scores badly and looks like a retrieval bug.
- **Record SciFact's licence** next to the loader. Look it up rather than assuming — BEIR licences differ per dataset, and the roadmap requires this.

Loader tests use tiny hand-written fixtures committed as test resources — a three-document corpus, two queries, a qrels file with a header. **No network in these tests.** The network path is exercised only by Task 5.

**Commit:** `feat(quality): BEIR corpus, queries and qrels loaders`

---

## Task 5: the SciFact parity test

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/SciFactParityTests.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.Tests/*.csproj` — `<RequiresSecrets>true</RequiresSecrets>`

Gate on `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` and `RAGNET_BEIR_CACHE` via `Assert.SkipWhen`, message naming all three. Follow `LateChunkingIntegrationTests.cs:28-34`.

Wire the real path: `OnnxEmbeddingGenerator` from Task 1, in-memory vector store for determinism, `title + "\n" + text` corpus text, cosine over normalised vectors, retrieve, aggregate via Task 3, score via Task 2.

**Assert `0.625 ≤ nDCG@10 ≤ 0.665`.** Two-sided: below means retrieval or aggregation is wrong, above means a leak — most likely qrels reaching the ranker. **The failure message must print the computed value**, or a red run tells whoever sees it nothing.

Report the number you actually measured, whether the gate was satisfiable in your environment, and — if it was not — say so plainly rather than implying the test ran.

**Commit:** `test(quality): SciFact nDCG@10 parity within 0.02 of published`

---

## Task 6: documentation

**Files:**
- Create: `docs/reference/retrieval-quality.md`
- Modify: `docs/planning/ROADMAP.md`, `sidebars.ts`

Document what the number means and, more importantly, what it does not: one dataset, one embedding model, one configuration. Keep it clearly apart from `docs/reference/benchmarks.md`, which measures speed — the roadmap asks for the names to stay distinct.

Add the page to `sidebars.ts`. Seven guide pages are already unreachable from the sidebar (a recorded debt); do not make it eight.

**ROADMAP:** flip Phase 3.7 to `[status: complete]` with a `**Completed:**` paragraph. Record that the phase had to add `OnnxEmbeddingGenerator` because no local dense embedder existed — the design assumed one did.

**Add the BM25 debt entry** with its numbers: `InMemoryBm25Index` lowercases and splits, while Anserini (source of BEIR's published BM25 figures) applies Porter stemming and an English stopword list at `k1=0.9, b=0.4` against our hard-coded `k1=1.5, b=0.75`. Irrelevant to this dense-only phase; it matters for the planned `+BM25 hybrid` ablation row, which would be incomparable to any published BM25 reference **while looking like validation of ours**. Schedule it.

Do **not** flip `MILESTONE.md` — that follows the whole-phase review.

**Commit:** `docs: retrieval quality benchmark and the BM25 comparability debt`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. All prior baselines hold; the two new suites report their counts.
3. Both new projects in `Rag.NET.slnx`; `RepoConventions` still **9**.
4. No new `#pragma` or `SuppressMessage`.
5. No dataset files committed — `git status` clean, and nothing under the cache path is tracked.

**Report:** every commit hash, verbatim build and test output, the four hand-computed nDCG values your implementation produced, what happened when you ran Task 3's fixture against a cut-then-pool implementation, the measured SciFact nDCG@10 (or plainly that the gate was unsatisfiable), and everything this plan got wrong. That last item is not a formality — every phase in this milestone has had a plan asserting something the code did not do, and this one already lost a premise before implementation started.
