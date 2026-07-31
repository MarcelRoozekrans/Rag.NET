# BEIR Expansion Implementation Plan (Phase 3.12)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add FiQA and ArguAna to the retrieval-quality harness under a two-run protocol — one matching BEIR's own setup and comparable to published figures, one using Rag.NET's real chunking — backed by an embeddings cache so the runs are affordable.

**Architecture:** Generalise the existing SciFact-shaped parity test over `BeirDatasetDescriptor`, add a content-addressed embeddings cache, then add the two datasets. The ablation table is **Phase 3.15** and is not built here.

**Tech Stack:** .NET 10, ONNX Runtime (`all-MiniLM-L6-v2`), xUnit v3.

**Design:** `docs/plans/2026-07-31-beir-expansion-ablation-design.md`. Read §0 first — the roadmap entry that scheduled this phase contains a contradiction, and §0 is its resolution. §4–§5 describe the ablation table and are **out of scope**; the scope-split note at the top says why they remain in that document.

---

## The number I will not give you

**The published nDCG@10 for FiQA and ArguAna must be looked up, not taken from this plan.** I do not have them to a reliability that should set a parity band. A wrong published figure gives a band that is either impossible to hit or impossible to miss, and both are worse than no test.

Look them up for **`all-MiniLM-L6-v2` specifically** — the figure is per-model — from MTEB's leaderboard or the BEIR paper, **record which source and which value in the descriptor**, and note that MTEB and the BEIR paper sometimes differ slightly. If the two disagree by more than the ±0.02 band, say so and stop rather than picking one.

SciFact's own descriptor already models this: counts "from the downloaded archive, not from a paper", with BEIR's README cited where it agrees.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matches file), MA0006, MA0008, MA0009, MA0132, MA0140, ZA0601 (no `GroupBy`/`OrderBy`/`ToList` in a loop), ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ001/HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060. **No new `#pragma` or `SuppressMessage`.**
- All logging through `LoggerMessage` source-gen (`BeirLog.cs` exists).
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A` or `git add .`** — explicit paths. **No dataset, model or embedding file may be committed.**
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.

Baselines, with the model set and nothing skipped: `Rag.NET.Benchmarks.Quality.Tests` **70**, `Rag.NET.Benchmarks.Quality.IntegrationTests` **2**, `Rag.NET.Embeddings.Onnx.Tests` **149**, `Rag.NET.Chunking.IntegrationTests` **4**, `Rag.NET.Tests` **1325**, `Rag.NET.Parsers.Archive.Tests` **52**, `Rag.NET.Parsers.Email.Tests` **76**, `Rag.NET.Chunking.Templates.Tests` **51**, `RepoConventions` **9**.

**The environment is provisioned** at `C:/Users/MARCEL~1/AppData/Local/Temp/claude/c--Projects-Prive-Rag-NET/2310a96c-be17-4a93-9256-e2770c41c90d/scratchpad/bench/` — `model.onnx`, `vocab.txt`, `scifact/`. Set `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB`, `RAGNET_BEIR_CACHE`. **Run the gated tests for real.**

**SciFact = 0.64593 is the regression gate for this whole phase.** Every task that touches the harness must leave it unmoved. If it moves, the generalisation changed behaviour and that matters more than the new datasets.

**Timestamp trap:** build without `--no-build` and confirm from the log that projects recompiled.

---

## Task 1: generalise the parity test over the descriptor

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/BeirDatasetDescriptor.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/SciFactParityTests.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.Tests/` — descriptor tests

`SciFactParityTests` hard-codes `PublishedNdcgAt10 = 0.645`, `LowerBound = 0.625`, `UpperBound = 0.665`. Move the published figure and the band onto `BeirDatasetDescriptor`, so a dataset carries its own target and the test becomes a `[Theory]` over datasets rather than a file per dataset.

**Refactor only. No new dataset yet, no behaviour change.**

**Acceptance: SciFact still measures 0.64593.** Run the parity test and confirm the number, not merely that it is in band — a generalisation that silently changed the corpus text or the top-k would still land inside ±0.02. Report the measured value.

**Commit:** `refactor(quality): a dataset carries its own parity target`

---

## Task 2: the embeddings cache

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/EmbeddingCache.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/EmbeddingCacheTests.cs`

Without this, FiQA costs roughly an order of magnitude more than SciFact's ~355 s **per run**, and the phase has two runs per dataset.

**The key must include the model identity, not only the text.** A cache keyed on text alone returns vectors from a different model after a model change, and every downstream number is then quietly wrong while every test passes — the exact failure this milestone keeps finding. Key on the model revision or `ModelId` **and** a hash of the exact text embedded.

Cache under `RAGNET_BEIR_CACHE`, never in the repo. **Verify nothing under the cache is tracked** before committing.

Tests, all offline with a fake embedder: a hit returns the stored vector; a miss embeds and stores; **a different model id with identical text is a miss**; a corrupt or truncated entry is treated as a miss rather than returned. That last one matters — a half-written cache file after an interrupted run must not become silent wrong data.

**Commit:** `feat(quality): a content-addressed embedding cache keyed on model and text`

---

## Task 3: FiQA and ArguAna descriptors

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/BeirDatasetDescriptor.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.Tests/`

Add both, following SciFact's shape exactly. For each: archive URL, **MD5 from BEIR's README table**, licence read from **upstream** rather than a mirror, document/query/test-query counts **from the downloaded archive**, and the published nDCG@10 with its source cited.

SciFact's licence work is the standard to match: two licences, upstream treated as authoritative, and the Hugging Face mirror's disagreement recorded rather than resolved. Do the same for these two — **do not assume BEIR's licences are uniform**, because SciFact's were not even internally uniform.

Assert the counts by loading each archive, the way SciFact's are asserted.

**Commit:** `feat(quality): FiQA and ArguAna dataset descriptors`

---

## Task 4: the two-run protocol

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/`

Two runs per dataset:

| Run | Protocol | Compared against |
|---|---|---|
| **parity** | truncate at 256, one chunk per document | the **published** figure |
| **real** | Rag.NET's actual chunking, max-pool to documents | **our own parity run** |

**The real run must never be compared to a published figure.** That would be the error this phase exists to avoid — a number produced under one protocol judged against a reference produced under another. Its assertion is a relationship to our own parity measurement, and the test's own remarks must say why.

**This is the first thing that has ever exercised max-pooling against a corpus.** SciFact indexes one chunk per document, so `DocumentRankingTests`' fixture has been the only guard. On FiQA the real run should produce a *different* number from the parity run — if the two are identical, either the chunker is not chunking or the aggregation is not aggregating, and that is a finding, not a pass.

Assert that difference explicitly rather than only asserting a band.

**Commit:** `test(quality): parity and real runs for FiQA and ArguAna`

---

## Task 5: documentation

**Files:**
- Modify: `docs/reference/retrieval-quality.md`, `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Record every measured number with the protocol that produced it — a table where parity and real numbers sit in adjacent columns and the reader can see which is comparable to published and which is not.

**Correct §0's contradiction in the roadmap entry**, which claims FiQA exercises max-pooling without noting that only the chunked run does.

Flip 3.12 to complete in **both** planning files in the same commit — 3.10 and 3.7 both shipped with `MILESTONE.md` left at `[pending]`, and 3.13 fixed that by doing them together.

**Schedule the two phases this design created**: **3.15** (ablation table — carries §4–§5 of the design) and **3.14** (library comparison, defaults rather than matched configuration, with the reasoning already recorded).

**Commit:** `docs: FiQA and ArguAna, measured under both protocols`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. **SciFact still 0.64593.** Non-negotiable.
3. Every baseline holds; the new runs report their numbers.
4. No dataset, model or embedding file tracked — `git status` clean.
5. No new `#pragma` or `SuppressMessage`.

**Report:** every commit hash, verbatim build and test output, **the published figures you found and the source you took them from**, the four measured numbers (FiQA and ArguAna, parity and real), whether the real runs differed from their parity runs, and everything this plan got wrong. That last item is not a formality — every phase in this milestone has had a plan asserting something the code did not do, and this one deliberately declines to assert two numbers it is not sure of.
