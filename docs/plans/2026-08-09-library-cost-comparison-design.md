# Library Performance Comparison — Design (Phase 5.1)

**Date:** 2026-08-09
**Milestone:** 5 — Evaluation Depth
**Status:** design — **the confound resolution in §2 needs approval before any number is measured**

## 0. What this is, and the order it must happen in

Phase 3.14 compared retrieval **quality** across five stacks at their defaults and published rows
nobody can attack on configuration. Nobody has published what those stacks **cost**. That is this
phase — indexing throughput, query latency, and the .NET-only memory figures.

The roadmap records a hazard against it before any number exists: **mixing in-process .NET and
subprocess Python measurement is a latency confound**, and 3.14 *deliberately withheld*
cross-ecosystem latency for exactly that reason. So the phase "must state how it handles what 3.14
refused to publish, **before publishing rather than after**."

This document is that statement. **No measurement runs until §2 is agreed** — publishing first and
explaining afterwards is the failure mode, and the ordering is the whole point.

## 1. What already exists, measured rather than assumed

3.14's infrastructure is genuinely reusable:

- **`uv` 0.10.0** installed, and **Python 3.14.5** — matching the interpreter `uv.lock` pins.
- The harness at `benchmarks/library-comparison-python/` with entrants for LangChain, LlamaIndex
  and Haystack, plus `pinned_embedder.py`, `vector_cache.py` and `trec_run.py`.
- `BeirRunBudget`, `BeirDatasetCache`, and the pinned `all-MiniLM-L6-v2` ONNX export fetched by
  the nightly at a pinned revision and SHA-256.
- `docs/reference/library-comparison-defaults.md`, written *before* the entrants.

**What is not present on this machine:** `RAGNET_BEIR_CACHE` is unset and no cache exists, and the
ONNX model variables are unset. Both are public downloads needing no account — unlike Phase 6.1's
live services — so the phase is runnable here once fetched.

## 2. The confound, and why the existing architecture already dissolves it

**The critical finding: there is no subprocess in the measured path today.**

Python entrants are run **out of band** — `uv run python run_entrant.py <dataset> <entrant>` — and
emit a **TREC run file and nothing else**. No Python code computes a metric. The .NET test reads
that file back and scores it with the one `IrMetrics` behind every published figure.

So 3.14's boundary is **a file, not a pipe**. Process startup, interpreter boot and serialization
sit *outside* the artefact, which is why quality was comparable and publishable at all.

**Cost measurement inherits that seam if — and only if — each ecosystem times itself on its own
side of it.**

### 2.1 The proposal

Each entrant measures itself **in-process, in its own runtime**, and emits a **timings sidecar**
next to its run file: indexing wall-clock, and per-query latencies from which p50/p99 are computed
by the same .NET code for every entrant.

- **Python rows**: `time.perf_counter()` inside `run_entrant.py`, around indexing and around each
  query — never around the process.
- **.NET rows**: the equivalent in-process, already natural.
- **Neither number ever crosses the boundary while being measured.** No .NET stopwatch ever wraps
  a Python process; that is precisely the measurement 3.14 refused to publish.

The .NET side reads both sidecars and publishes them together, exactly as it already reads both
run files and scores them together. **Percentiles are computed once, in one place, from raw
per-query samples** — an entrant reporting its own p99 would let five different definitions of
"p99" into one table.

### 2.2 What this does *not* make comparable

Stating the limits is half the deliverable.

- **Interpreter and runtime startup are excluded by construction**, so the table answers "how fast
  is the work" and **not** "how fast is the tool from cold". Those are different questions and only
  the first is confound-free here.
- **Allocations per query, Native AOT startup and RSS are .NET-only concepts.** There is no
  meaningful Python row for allocations-per-query; publishing an empty or hand-waved cell would be
  worse than omitting the column. **Those figures are published as a .NET-internal table**, labelled
  the way the `+BM25 hybrid` row is labelled internal — not as a cross-ecosystem comparison.
- **Machine and cold-cache effects.** Every row must come from one machine in one session with the
  vector caches warm, and the page must say so.

**If §2.1 cannot be implemented cleanly for any entrant, that entrant publishes no latency row.**
A missing row is honest; a row measured differently from its neighbours is not, and it would look
identical.

## 3. Why this is scheduled after 5.4 and before 5.2/5.3

5.4 added Precision@k and MAP. This phase publishes no new quality metric, so it does not depend on
them — but it does share the harness, and doing cost while the run-file architecture is fresh is
cheaper than rediscovering it later.

## 4. Testing

- **The sidecar's shape is pinned** — a run file without its timings sidecar must fail the way a
  missing run file already does (`refuse-on-miss`), not skip. An opted-in measurement that quietly
  skipped would read as a pass; that rule already exists here and is inherited rather than reinvented.
- **Percentile computation is unit-tested against hand-computed values**, per `IrMetricsTests`'
  convention. p50 and p99 have more than one defensible definition; the one chosen is pinned so a
  future change to it is visible.
- **A guard that no published latency figure was produced by timing across the boundary.** The
  design's central claim deserves a check, not just a paragraph.

## 5. Out of scope

- **Cold-start comparison.** §2.2 — it is a different question and cannot be answered
  confound-free by this architecture.
- **Adding entrants.** The five comparators are 3.14's; changing the field changes what the quality
  rows mean too.
- **Publishing to the docs site.** The measurement lands first; whether and how it appears on the
  site is a separate decision, and `docs.yml` now builds that site on every PR.

## 6. The decision this needs

**§2.1 — self-timed sidecars, boundary excluded — versus per-ecosystem tables labelled
non-comparable.** The roadmap allows either. This design recommends the sidecar, because the
architecture already puts a file at the boundary and the change is small; but it publishes a
cross-ecosystem latency table, which is exactly what 3.14 declined to do, so it should be an
explicit choice rather than a default inherited from a design document.
