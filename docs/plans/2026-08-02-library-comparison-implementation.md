# Library Comparison at Defaults Implementation Plan (Phase 3.14)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Publish a defaults-versus-defaults retrieval-quality comparison of Rag.NET against other RAG libraries on the same corpora and the same pinned embedder, with every configuration and version published and the harness re-runnable.

**Architecture:** Every entrant emits a **run file** — query id, document id, rank, in TREC format — and nothing else. All run files are scored by the one `IrMetrics` that produced this repository's published BEIR figures, so no entrant computes a metric and no difference can come from evaluation code.

**Tech Stack:** .NET 10, xUnit v3, Semantic Kernel, Kernel Memory; Stage 2 adds a pinned Python subprocess.

**Design:** `docs/plans/2026-08-02-library-comparison-design.md`. Read §1, §2 and §6 before writing code.

---

## The acceptance gate, stated before anything else

**Rag.NET's own row must reproduce the figures the BEIR harness already publishes**, through the same run-file path as every other entrant:

| Dataset | must reproduce |
|---|---:|
| SciFact | **0.64593** |
| FiQA | **0.37086** |
| ArguAna | **0.50432** |

**If the control row does not reproduce these, the harness is wrong and no other row can be trusted.** Stop and report rather than continuing to add comparators. This check comes before any comparison is read, and it is the whole reason the control exists.

The control must go through the **real run-file boundary** — emit a run file, read it back, score it. A control that shortcuts straight to `IrMetrics` validates nothing about the path the comparators use.

---

## What this phase is not allowed to do

- **Do not change any Rag.NET default**, however tempting the table makes it. That is explicitly its own phase. A defaults table where one row was tuned is not a defaults table.
- **Do not tune any entrant**, ours included.
- **Do not report latency across the .NET/Python boundary** (design §3). Quality is unaffected by how a process was launched; latency is not, and the run file deliberately carries no timing.
- **Do not estimate a number you did not measure.** If Stage 2 proves unaffordable, Stage 1 ships and Stage 2 is recorded as unrun **with what it would have cost** — the pattern Phase 3.12 used for FiQA's real leg, which 3.15 later measured at 1 h 4 m against a derived 1.5–2 h.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matching the file), MA0006, MA0008, MA0009, MA0023, MA0132, MA0140, ZA0601, ZA0501, EPS05/EPS06, EPC12/EPC13, **ERP022**, HLQ001, HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060, MA0025. **No `#pragma` or `SuppressMessage`.**
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- **`Rag.NET.Benchmarks.Quality.Tests` runs with `--logger trx`, output never piped through `head`/`tail`/`grep`** — it has an undiagnosed flake whose name has been lost three times, twice to output piping. If it fails, the trx is the deliverable.
- A new test project must declare its tier (`<RequiresDocker>` etc.) or `TestProjectTierTests` fails. New **shipped** packages need `<VerifiedBy>`; test projects do not.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A`** — explicit paths. **No dataset, model or embedding file may be committed.**
- **Timestamp trap:** build without `--no-build`, confirm the recompile line.

**Baselines:** `Rag.NET.Tests` **1342**, `Rag.NET.Evaluation.Tests` **382**, `Rag.NET.Benchmarks.Quality.Tests` **129**, `Rag.NET.RepoConventions.Tests` **30** (29 + 1 by-design skip).

**Environment** (provisioned): `RAGNET_BEIR_CACHE`, `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` under `C:/Users/MARCEL~1/AppData/Local/Temp/claude/c--Projects-Prive-Rag-NET/2310a96c-be17-4a93-9256-e2770c41c90d/scratchpad/bench`. **Never run FiQA's real leg** (~1 h).

---

# Stage 1 — .NET

## Task 1: the run-file boundary

**Files:**
- Create: a run-file writer and reader in `src/Rag.NET.Benchmarks.Quality/`
- Create: tests

TREC run format, one line per ranked document:

```
<queryId> Q0 <docId> <rank> <score> <runTag>
```

Rank is 1-based. `Q0` is a literal the format requires. The score column exists for `trec_eval`'s benefit; **ranking is authoritative, not score** — say so in the docs, because an entrant whose scores are not comparable to another's is fine and expected.

Round-trip to `IReadOnlyDictionary<string, IReadOnlyList<string>>`, which is exactly what `IrMetrics.Evaluate` consumes.

**Tests:** round-trip preserves order; ties do not reorder; a malformed line fails loudly rather than being skipped (a silently dropped line is a silently different number); an empty run for a query is representable and distinct from a missing query.

**Commit:** `feat(quality): TREC run files as the comparison boundary`

---

## Task 2: the control row — the acceptance gate

**Files:**
- Create: the Rag.NET entrant and its measurement

Run Rag.NET's parity protocol, emit a run file, read it back, score it with `IrMetrics`, and **assert it reproduces 0.64593 / 0.37086 / 0.50432.**

**Go through the real boundary.** Writing to disk and reading back is the point — it proves the format, the ordering and the reader on a row whose answer is already known.

Pin the figures with `BeirReproduction` (±0.005), as every other measured figure in this repository is pinned.

**If a figure does not reproduce, stop.** Report the discrepancy and its size. Do not proceed to comparators, and do not adjust the tolerance — a control that needs a wider band is a control that failed.

FiQA's parity leg is ~1 h 11 m; SciFact and ArguAna are minutes. **Run SciFact and ArguAna, and gate FiQA behind `RAGNET_BEIR_LONG_RUNS`** like every other expensive case, adding its cost to `BeirRunBudget` — which throws on an unmeasured dataset/protocol pair, so a new pair must be entered deliberately.

**Commit:** `test(quality): the control row reproduces the published figures through a run file`

---

## Task 3: read each library's actual defaults, and cite them

**Files:**
- Create: a defaults table document or data file

**Before writing any comparator**, record for Semantic Kernel and Kernel Memory, from their **own source or documentation at the pinned version**:

- default chunker, chunk size, overlap
- default top-k
- default retrieval mode (dense, hybrid, other)
- whether it reranks by default
- its default embedder — **published even though unused**, because "this library would otherwise have used X" is what a reader needs to interpret the row

**Cite a file and version for every value.** A reader must be able to check the reading rather than trust it.

**Where a library has no default** — it will not run without a choice — record "no default; chose X because the corpus requires something" rather than quietly picking a value. That absence is itself a finding about the library.

**This task comes before the comparators deliberately.** Writing the entrant first means discovering defaults by whatever made the code compile, which is how a table ends up measuring the harness author's assumptions.

**Commit:** `docs(quality): the defaults every entrant is measured at`

---

## Task 4: Semantic Kernel at its defaults

**Files:**
- Create: the entrant, emitting a run file

Index the corpus and retrieve for each judged query using **Semantic Kernel's own defaults** for everything except the embedder, which is the pinned `all-MiniLM-L6-v2`.

**Wiring the pinned embedder is the fiddly part.** Each library has its own embedding abstraction; the model must be the same ONNX model at the same revision, or the row measures embedders. **State how you verified it is the same model** — ideally by checking a vector for a known string against one from `OnnxEmbeddingGenerator`.

Emit a run file. Score it with the same `IrMetrics` as the control.

**Report the number without editorialising.** If Semantic Kernel beats Rag.NET, that is the result.

**Commit:** `test(quality): Semantic Kernel at its defaults`

---

## Task 5: Kernel Memory — dropped, and why

**Decided 2026-08-02, after Task 3 read its defaults and before any entrant was written.**

Kernel Memory is **archived**. Its NuGet packages are marked legacy and no longer maintained, and
the repository's own README calls it "an archived research project"; `0.98.250508.3` (2025-05-09) is
the final release.

**Publishing a number against a project its own authors archived invites the fair objection that we
picked something that could not answer back.** The finding — that the .NET ecosystem's other
RAG-ingestion library is end-of-life — is worth more than the row would have been, and it is
recorded **without a number attached**.

Task 3 also established what the row would have shown, and it stays recorded because it is a real
interoperability fact rather than a criticism: Kernel Memory's own `ChunkTooBigForEmbeddings` guard
refuses its 1000-token default chunk size against a 256-token embedder, so the row could only have
run at 256/100 — forced by Kernel Memory's validation, not by us.

**No entrant is written. The finding goes in the published table's prose, and in the phase close.**

---

## Task 6: publish Stage 1

**Files:**
- Modify: `docs/reference/retrieval-quality.md` or a new comparison page

The table, with **every entrant's exact version and full configuration beside its number**, and a header stating plainly that **the embedder is pinned and matched while everything else is default** (design §2). A reader must not think this is defaults end-to-end.

Publish the run files so a reader can re-score with `trec_eval`.

State what the table does not measure (design §7): not ingestion throughput, memory or cost; not production suitability; not any library's ceiling; a dated measurement of pinned versions; and where differences are smaller than the spread Phase 3.15 observed between protocols, **say they are not separable** rather than ranking noise.

**Commit:** `docs(quality): library comparison at defaults, .NET entrants`

---

# Stage 2 — Python

**Stage 2 may be reported unrun.** If the harness proves unaffordable, record what it would have cost and ship Stage 1. That is a legitimate outcome, not a failure — but **estimating the numbers is not**.

## Task 7: the Python harness

A pinned environment (lockfile committed) whose only job is to emit run files in the same TREC format. **No Python code computes a metric** — that is the whole point of the boundary and what makes this stage affordable.

Feed it the same corpus and the same ONNX model. **Verify the Python-side embedder produces the same vector for a known string as `OnnxEmbeddingGenerator` does**, and report the comparison — if the vectors differ, every Python row is measuring a different model and the stage is invalid.

**Commit:** `feat(quality): a pinned Python harness that emits run files`

## Task 8: LangChain, LlamaIndex and Haystack at their defaults

Task 3's discipline applies: read and cite each library's defaults **before** writing its entrant.

**Commit:** one per library.

## Task 9: publish Stage 2, or record it unrun

---

## Task 10: close Phase 3.14 and Milestone 3

**Files:** `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Flip 3.14 to complete in **both files in the same commit**.

**This closes Milestone 3 — 16 of 16.** Check the milestone's Definition of Done against reality rather than against the checkboxes, the way the 2026-08-02 audit did: the DoD requires `features.md` detail, matrix and code to agree, and the audit found that criterion **failing** with two claims still in `KnownFalseClaims`. Do not tick a box this phase did not make true. If Milestone 3 cannot honestly close, say what remains and leave it open.

Record what the phase found, including anything the design or this plan got wrong.

**Commit:** `docs(planning): close phase 3.14`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. **The control row reproduces 0.64593 / 0.37086 / 0.50432.** Non-negotiable.
3. Baselines hold.
4. No new `#pragma`, `SuppressMessage`, or `KnownFalseClaims` entry.
5. `git status` clean — no dataset, model or embedding file tracked.

**Report:** every commit hash, verbatim build and test output, **the control row's measured figures**, every entrant's number with its version and configuration, how you verified each entrant used the same embedder, whether Stage 2 ran or is recorded unrun with its cost, and everything this plan got wrong.

That last item is not a formality. Every phase in this milestone has had a plan asserting something the code did not do — 3.16's plan specified a mathematically impossible assertion, 3.15's design was wrong about which side truncation starved, 4.0's plan mis-stated the gating-site count, and 3.8's plan omitted the replay bridge its own design depended on. All four were caught by an agent checking the claim against the code rather than trusting it.
