# Retrieval Ablation Table Implementation Plan (Phase 3.15)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Publish a 12-cell ablation table — dense, +BM25 hybrid, +HyDE, +reranker across SciFact, FiQA and ArguAna — plus FiQA's never-run real leg.

**Architecture:** `BeirHarness.MeasureAsync` already indexes, retrieves and evaluates. The four rows differ **only in retrieval**, so the seam goes there and every row reuses one index per dataset. HyDE's hypotheticals are generated once by a local tool and cached; the table run never calls an LLM.

**Tech Stack:** .NET 10, ONNX Runtime, xUnit v3, `Microsoft.Extensions.AI`.

**Design:** `docs/plans/2026-08-01-retrieval-ablation-table-design.md`. §4–§5 of `docs/plans/2026-07-31-beir-expansion-ablation-design.md` define what each row **is** and are **not reopened**.

---

## The cost fact that shapes this plan

Indexing is per **dataset**, not per **cell**. All four rows query the same `InMemoryVectorStore` over the same units. Twelve cells cost three corpus embeddings plus query-side work — not twelve.

**If you find yourself re-embedding a corpus per row, stop.** You have put the seam in the wrong place, and the phase has gone from roughly an hour to most of a day.

---

## Two numbers that are not yours to move

**The parity anchors: SciFact 0.64593, ArguAna 0.50432, FiQA 0.37086.** The dense row *is* the parity run. If any of them moves, the refactor in Task 1 changed retrieval behaviour and that outranks every new number in this phase.

**`BeirReproduction` pins measured figures at ±0.005 and will go red if you move them.** That is the mechanism working. Do not widen a tolerance to make a test pass — if a pin goes red, either you changed behaviour or you have a real finding.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file), MA0006, MA0008, MA0009, MA0132, MA0140, ZA0601 (no `GroupBy`/`OrderBy`/`ToList` in a loop), ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ001/HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060. **No new `#pragma` or `SuppressMessage`.**
- xUnit v3, `TestContext.Current.CancellationToken`, `Assert.SkipWhen` for gated cases, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A` or `git add .`** — explicit paths. **No dataset, model, embedding or hypothetical file may be committed.** Check `git status` before every commit.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.
- **Timestamp trap:** build without `--no-build` and confirm from the log that projects recompiled. A restored file keeps its mtime, MSBuild skips it, and you test a stale binary. Five agents in this milestone have been caught by this.

**Baselines:** `Rag.NET.Tests` **1342**, `Rag.NET.Benchmarks.Quality.Tests` **110**, `Rag.NET.Benchmarks.Quality.IntegrationTests` **21**, `RepoConventions` **9**.

**Environment** (already provisioned): `RAGNET_BEIR_CACHE`, `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` all under `C:/Users/MARCEL~1/AppData/Local/Temp/claude/c--Projects-Prive-Rag-NET/2310a96c-be17-4a93-9256-e2770c41c90d/scratchpad/bench`. `RAGNET_BEIR_LONG_RUNS=1` un-gates expensive cases. **Never run FiQA's real leg until Task 8** — it is ~1.5–2 hours.

---

## Task 1: put the seam in retrieval, and prove nothing moved

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AblationRow.cs`

`MeasureAsync` currently hard-codes dense retrieval. Introduce a retrieval strategy — an `AblationRow` — that takes the query, the embedder, the cache and the store and returns a ranked list of chunk hits. Dense becomes the default implementation of that seam, doing exactly what the code does today.

**Refactor only. No new row, no behaviour change.**

Keep indexing outside the seam. The row receives an already-populated store.

**Acceptance: SciFact still measures 0.64593 and ArguAna still 0.50432**, both separators. Run the parity tests and report the measured values — not merely that they are in band. ±0.02 is wide enough to hide a real change, which is exactly why `BeirReproduction` exists at ±0.005.

**Commit:** `refactor(quality): retrieval becomes a named ablation row`

---

## Task 2: the hypothetical cache

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/HypotheticalCache.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/HypotheticalCacheTests.cs`

Model it on `EmbeddingCache`, which already solves the same problem for vectors — read it first and follow its shape.

**The key covers four things: the model identity, the prompt template, the query, and the hypothesis index.** Use `EmbeddingCache`'s length-prefixed construction; it exists specifically so `("ab","c")` cannot collide with `("a","bc")`. Editing the prompt must **miss**, not silently reuse hypotheticals generated from a different instruction.

**Refuse-on-miss is a mode, and it is the important one.** The table run opens the cache in a mode where a miss **throws**, naming the missing key. Silent regeneration would blend two generations into one table with nothing to say so. The generation tool (Task 3) opens it in the mode that fills.

Tests, all offline with a fake generator:
- a hit returns the stored text
- a different model id, same query → miss
- **a different prompt template, same query and model → miss**
- a different hypothesis index → miss
- a truncated or corrupt entry is treated as a miss rather than returned
- **in refuse-on-miss mode, a miss throws and the message names the key**

That last pair matters most. A half-written cache file after an interrupted generation must not become silent wrong data.

**Commit:** `feat(quality): a content-addressed hypothetical cache`

---

## Task 3: the one-time generation tool

**Files:**
- Create: a console entry point under `benchmarks/` or the quality project — your call, but state it
- Modify: `Directory.Packages.props` if a chat-client package is needed

**No shipped package may gain an LLM provider dependency.** This tool exists to fill a cache once; it is benchmark infrastructure. If adding a package, add it to the tool's project only, and say so in your report.

Reads queries from the BEIR datasets, generates `HypothesisCount = 3` hypotheticals per evaluated query via `IChatClient`, writes them to the cache. **2,354 evaluated queries → 7,062 generations** (SciFact 300, FiQA 648, ArguAna 1,406).

Requirements:
- **Resumable.** 7,062 calls will be interrupted. Already-cached keys are skipped, so a re-run continues rather than restarting.
- The model id comes from configuration and is **recorded in the cache key**.
- Temperature 0, or the provider's nearest equivalent, and record what was used.
- Report progress and a final count.

**Do not run it yet** — Task 5 is where the generated cache gets used, and you should confirm the plumbing on a handful of queries first. Generate 10 queries' worth, verify they land in the cache and are re-read identically, then stop and report before spending the full run.

**Commit:** `feat(quality): a one-time hypothetical generation tool`

---

## Task 4: the +BM25 hybrid row

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AblationRow.cs`
- Modify: the integration tests

`InMemoryBm25Index` combined with dense results via RRF. `IHybridSearchable` is implemented only by the Azure AI Search and Weaviate stores, so the in-memory path composes the two manually.

**The row is labelled incomparable to any published BM25, in the table itself.** Anserini stems and applies stopwords at `k1=0.9, b=0.4`; ours lowercases and splits at `k1=1.5, b=0.75`. Not two settings of one retriever.

**Assert BM25 actually contributed** (design §7). If `InMemoryBm25Index` returned nothing, RRF would degrade to the dense ranking and this row would silently equal the row above it — a passing test showing a number that means nothing. Assert that some query's final ranking differs from its dense ranking, and that BM25 returned a non-empty result for a meaningful fraction of queries.

**Commit:** `feat(quality): the BM25 hybrid ablation row`

---

## Task 5: the +HyDE row

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AblationRow.cs`

Reads hypotheticals from the cache in **refuse-on-miss** mode, embeds them, averages (L2-normalised) as `LlmHypotheticalDocumentGenerator` does at `HypothesisCount = 3`, and searches with that vector.

**No LLM call happens here.** If the cache lacks a key the run fails, naming it and the command that generates it.

Once the plumbing is verified on the 10 queries from Task 3, run the **full generation** — report the total and the wall time — then measure this row.

**Commit:** `feat(quality): the HyDE ablation row, from cached hypotheticals`

---

## Task 6: the +reranker row

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AblationRow.cs`
- Modify: `.github/workflows/nightly.yml`

`OnnxReranker` over the dense row's top-k. `OnnxRerankerOptions` already takes `ModelPath` and `VocabPath`, so no library code is needed — this is a provisioning task.

**The model is `cross-encoder/ms-marco-MiniLM-L-6-v2` in an ONNX export. The revision and SHA-256 are NOT in this plan.** Look them up, pin the revision, verify the digest, and **record both in the descriptor with the source you took them from**. This is the same refusal 3.12's plan made about published nDCG figures: a wrong pin is worse than no pin. If you cannot find a trustworthy pinned export, say so and stop rather than pulling `main`.

Provision it the way the embedder already is — pinned revision, SHA-256 verified, cached, added to the nightly.

**State the token limits rather than leaving them to be inferred**: the reranker's `MaxLength` defaults to 512 while the embedder truncates at 256. Different models, different jobs — but say which each uses so a reader does not conclude one is a bug.

**Assert the reranker actually reordered** (design §7). A cross-encoder returning input order produces a row identical to the one above it.

**Commit:** `feat(quality): the cross-encoder reranker ablation row`

---

## Task 7: run the table, and check it against its own predictions

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirRunBudget.cs`
- Modify: `docs/reference/retrieval-quality.md`

`BeirRunBudget` **throws on an unmeasured dataset/protocol pair** — by design, so a new case cannot silently default into or out of the nightly. **Every one of the twelve cells is a new pair.** Each entry says whether its cost is **measured, derived or estimated**.

Run all twelve. Then check the result against the predictions design §2 made **before** any of this was measured:

| Dataset | predicted HyDE effect |
|---|---|
| FiQA | clear lift — the positive control |
| ArguAna | no lift, plausibly negative — the negative control |
| SciFact | modest lift, smaller than FiQA's |

**Report which predictions held and which did not.** A failed prediction is a finding, not something to smooth over in the prose afterwards — 3.16 set that standard when it had to be able to report that ArguAna had *not* recovered.

**Two results that would invalidate the table rather than populate it:**

- **Every row lifts on every dataset.** The negative control has failed and the table is measuring something other than what it claims.
- **FiQA shows no lift.** The phase then **cannot** conclude that HyDE does not help — the alternatives are a weak model or a bad prompt, and a run flat everywhere cannot separate them. Say so plainly instead of publishing a table whose anchor row is uninterpretable.

**Commit:** `docs(quality): the retrieval ablation table, twelve cells`

---

## Task 8: FiQA's real leg — run it yourself, in the foreground

**Files:**
- Modify: `docs/reference/retrieval-quality.md`, `BeirReproduction`

**Do not background this and do not run it inside a subagent that will exit.** Five agents in this milestone have stalled waiting on background measurements that died with them. Foreground, long timeout.

Derived at ~1.5–2 h: 121,236 chunk embeddings plus 6,648 query embeddings at the ~27 embeddings/s observed across the two packed real legs. **Derived, not measured — this run is the measurement**, and it replaces the estimate rather than confirming it. Report the actual wall time.

**State the 38 empty entries alongside the number.** 38 of FiQA's 57,638 corpus entries have an empty title and empty text, and one (`117276`) is judged relevant, so the real leg indexes 38 fewer documents than the parity leg. Already surfaced as `UnindexedDocumentCount`. A recall figure computed against a corpus missing a relevant document needs the reader to know.

Pin the measured figure in `BeirReproduction` at ±0.005, as the other real legs are.

**Commit:** `docs(quality): FiQA under real chunking, measured at last`

---

## Task 9: close the phase

**Files:**
- Modify: `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Flip 3.15 to complete in **both files in the same commit** — 3.10 and 3.7 both shipped with `MILESTONE.md` left at `[pending]`.

Record: the twelve cells, which predictions held, the measured FiQA real figure replacing the derived estimate, and the BM25 comparability decision as **resolved** (published as an internal comparison) rather than still open.

Close the roadmap's carried items that this phase completes, and **move them to the Closed section rather than annotating them in place** — the house convention, learned in 3.16.

**Commit:** `docs(planning): close phase 3.15`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. **Parity unmoved: SciFact 0.64593, ArguAna 0.50432, FiQA 0.37086.** Non-negotiable.
3. Every baseline holds, with the new higher counts stated.
4. No new `#pragma` or `SuppressMessage`.
5. `git status` clean — no dataset, model, embedding or hypothetical file tracked.
6. No test gated on an API key. 3.12 found three guards that could never be satisfied on a runner and reported green while proving nothing.

**Report:** every commit hash, verbatim build and test output, the twelve measured cells, **which of the three predictions held and which did not**, FiQA's real number with its wall time and the 38 unindexed documents, the cross-encoder revision and digest **with the source you took them from**, and everything this plan got wrong.

That last item is not a formality. Every phase in this milestone has had a plan asserting something the code did not do — 3.16's plan specified a mathematically impossible assertion, and its design was wrong about `DeterministicChunkId`. Both were caught by an agent checking the claim against the code instead of trusting it.
