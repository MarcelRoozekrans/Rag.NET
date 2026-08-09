# The Library Cost Comparison — Implementation Plan (Phase 5.1, part 1)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make every entrant measure its own indexing and per-query latency, in its own runtime, and emit it as data next to its run file — so that a cost table becomes *possible* to publish honestly.

**Architecture:** A timings sidecar mirroring the run-file boundary exactly: each entrant writes one, the .NET side reads them all and computes every percentile itself, in one place.

**Tech Stack:** .NET 10, xUnit v3, Python 3.12 + uv.

**Design:** `docs/plans/2026-08-09-library-cost-comparison-design.md`

---

## Scope — read this before starting

**This plan deliberately stops before publishing a cost table.**

The design's §6 asks for an explicit choice between a cross-ecosystem latency table and
per-ecosystem tables labelled non-comparable. **That choice has not been made.** The design says in
as many words that it must not be inherited from a design document, so this plan does not infer it.

Everything here is required **identically under both options**: both need each entrant self-timed
in-process, both need percentiles computed once in one place, and both need the guard that no
figure was timed across the boundary. Only the final table's *shape* differs.

**Do not add a docs page, a README table, or a published figure of any kind.** If you finish the
tasks and the measurement works, stop and report. Producing the table is the next phase, after the
decision.

---

## Context

### What exists

- `benchmarks/library-comparison-python/run_entrant.py` runs one Python entrant over one dataset
  and writes a TREC run file. Five entrants: Rag.NET, Semantic Kernel (.NET); LangChain,
  LlamaIndex, Haystack (Python).
- `src/Rag.NET.Benchmarks.Quality/TrecRunFile.cs` is the boundary — `Write` and `Read`, with every
  malformed line throwing and naming file and line. **Read it before writing the sidecar.** Its
  conventions are the ones to copy: invariant culture, `\n` endings, ordinal key order,
  byte-identical output on any machine, and a thrown exception wherever a silent skip would produce
  a quietly different number.
- The .NET consumers are `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/` —
  `BeirPythonEntrantsTests.cs`, `BeirSemanticKernelDefaultsTests.cs`,
  `BeirComparisonControlTests.cs`.

### Two measurements taken while writing this plan

**1. `run_entrant.py`'s existing `elapsed` is not reusable.** It is one `time.monotonic()` span
that covers `entrant.build`, the whole query loop, the run-file write, *and* the self-line re-read
of the file's bytes. It conflates indexing with query latency and includes file I/O, and it is
`print`ed rather than emitted as data. **Do not extend it — replace it.** Leave the human-readable
line in place if you like, but it must be derived from the new numbers rather than measured
separately, or the two will drift.

**2. Indexing runs through a warm `VectorCache`.** `embed_many` is
`cache.get_or_embed(texts, embedder.embed)`. With the cache warm — which the design requires for
comparability — indexing wall-clock measures **index construction with embedding already paid
for**. That is a defensible thing to measure, and it is the only thing measurable consistently
across entrants that share one pinned embedder. **But it is not "the cost of indexing"**, and the
sidecar must carry the cache hit/miss counts so a reader can tell. A run with cold-cache misses is
measuring something else entirely and must be visibly different in the data, not just in a
sentence someone might write later.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`, `TreatWarningsAsErrors=false`.**
  MA0051 (≤60-line methods), MA0048, MA0061, **MA0006 (`string.Equals`, not `==` — only surfaces
  under `-c Release`)**, ERP022, EPC12/13, ZA0601.
- xUnit v3; `TestContext.Current.CancellationToken`; no sleeps.
- **Never pipe `Rag.NET.Benchmarks.Quality.Tests` output through `head`/`tail`/`grep`.**
- **Never `git add -A` or `.`** — explicit paths only. **Never stage `.lucent/chunks.json`,
  `.lucent/embeddings.bin`, any dataset, model, embedding or hypothetical-cache file, or any
  `.nupkg`.** A timings sidecar from a real run is a measurement artefact — **do not commit one**;
  tests write their own to a temp path.
- `git status` before committing — a file watcher edits `.csproj`/`.slnx` concurrently.
- Conventional commits with bodies, subject under 100 characters, trailer
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

**Baselines:** `Rag.NET.Tests` **1183**, `RepoConventions` **49, 0 skipped** (re-measured on main 2026-08-09; the old "48 + 1 skip" counted `Rag.NET.Security.AspNetCore`'s `VerifiedBy: none`, which Phase 4.5 resolved), `PackageValidation`
**22**, `Rag.NET.slnx` **146 projects**, packable set **69**.

---

## Task 1: `TimingsSidecar` — the format, with tests first

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/EntrantTimings.cs`
- Create: `src/Rag.NET.Benchmarks.Quality/TimingsSidecar.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/TimingsSidecarTests.cs`

**Shape.** One sidecar per run file, named `<run-file>.timings.json` — derived from the run file's
path so the pairing cannot be got wrong by a caller. JSON, not TREC: this is our format, read only
by us, and a shape with named fields will not be silently misread the way a positional one is.

It carries, at minimum:

- the run tag (**must match the run file's** — Task 3 checks this),
- indexing wall-clock in seconds,
- **raw per-query latencies in milliseconds, one per judged query, keyed by query id** — not
  percentiles. Percentiles are computed in Task 2, once, for every entrant.
- the embedding-cache hit and miss counts (see Context measurement 2),
- the unit count and max-units-per-document already printed by the entrant.

**Write the failing tests first.** `TrecRunFileTests.cs` is the model for coverage and for the
"every malformed input throws, naming the file" standard. At minimum:

- round trip preserves every per-query latency exactly;
- invariant-culture numbers — **write a test that fails on a comma decimal separator**, the way
  `TrecRunFile.ParseScore` documents. This is the defect that has already been found once here.
- a sidecar whose query-id set differs from its run file's is an error (Task 3, but the reader
  should expose the ids to make it checkable);
- a negative or non-finite latency throws;
- an empty latency map throws — an entrant that timed zero queries lost its query set, exactly as
  `TrecRunFile.Write` rejects an empty run set.

Run them, watch them fail, then implement.

**Commit.**

---

## Task 2: Percentiles, computed once

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/IrMetrics.cs` **— no. Create:**
  `src/Rag.NET.Benchmarks.Quality/LatencyStatistics.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/LatencyStatisticsTests.cs`

`IrMetrics` is retrieval-quality metrics and every published quality figure comes from it; latency
is a different concern and does not belong in it.

**p50 and p99 have more than one defensible definition.** Pick one — nearest-rank on the sorted
samples is the easiest to state and to hand-check — and **write the definition into the XML doc
comment**, including what it does at small sample counts where p99 and the maximum coincide.

**Unit-test against hand-computed values**, per `IrMetricsTests`' convention: a known small array
where the answer can be read off by eye, plus the boundaries (one sample; two samples; a sample
count where the percentile lands exactly on an index versus between two).

The reason this is its own task: an entrant reporting its own p99 would let five definitions of
"p99" into one table. One implementation, fed raw samples, is the whole point.

**Commit.**

---

## Task 3: The pairing is enforced, not assumed

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/TimingsSidecar.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.Tests/TimingsSidecarTests.cs`

A run file and a sidecar that disagree are worse than a missing sidecar, because they look fine.
Add a check — given a run file and its sidecar, they must agree on **the run tag** and on **the
exact set of query ids**.

**A missing sidecar must fail the way a missing run file already does — `refuse-on-miss`, not
skip.** Find that existing rule in the integration tests and inherit it rather than reinventing it.
This repository has shipped inert green tests before; a cost measurement that skipped when the
sidecar was absent would read as a pass.

**Commit.**

---

## Task 4: The Python entrant times itself

**Files:**
- Modify: `benchmarks/library-comparison-python/run_entrant.py`
- Create: `benchmarks/library-comparison-python/timings.py`

Mirror `trec_run.py`'s role: `timings.py` writes the sidecar, and nothing else in Python computes a
statistic.

- **Indexing**: `time.perf_counter()` around `entrant.build` **only** — not around dataset loading,
  embedder construction, or the run-file write.
- **Per query**: around the `retrieve(query.text, depth)` call **only**. Not around
  `top_documents` — pooling is harness protocol, not the library's retrieval, and it is
  deliberately identical across entrants.
- **`time.perf_counter()`, not `time.monotonic()`** — perf_counter is the higher-resolution clock
  and per-query latencies are small.
- **Never around the process.** No .NET stopwatch ever wraps Python; that is the measurement the
  design exists to avoid.

Emit the cache hit/miss counts the entrant already tracks. Rewrite the existing `elapsed` print
line to derive from the new numbers rather than measure separately.

**Verify by running it**, if the BEIR cache is available in this environment. If it is not, say so
plainly rather than claiming the entrant works — and check whether `RAGNET_BEIR_CACHE` is set
before assuming either way.

**Commit.**

---

## Task 5: The .NET entrants time themselves

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirSemanticKernelDefaultsTests.cs`
- Modify: the Rag.NET entrant's equivalent — **find it**; `BeirComparisonControlTests.cs` is the
  likely home but confirm by reading rather than assuming.

Same boundaries as Task 4, in-process: around index construction, and around each retrieval call,
excluding pooling. Write the sidecar beside the run file with the same `TimingsSidecar`.

The two ecosystems must bracket **the same operations**. If a .NET entrant's structure makes the
equivalent bracket genuinely impossible, **that entrant emits no sidecar and therefore no latency
row** — the design's rule, and a missing row is honest where a differently-measured one is not.
Report it rather than approximating.

**Commit.**

---

## Task 6: The guard on the design's central claim

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/BoundaryTimingGuardTests.cs`

The design's §2.1 claim — *no figure is produced by timing across the boundary* — deserves a check,
not a paragraph. A source-level guard over `benchmarks/` and the integration tests: **no
`Stopwatch`, `time.perf_counter` or `time.monotonic` span may enclose a subprocess launch**
(`Process.Start`, `subprocess.`, `uv run`).

`tests/Rag.NET.RepoConventions.Tests/` has source-scanning guards already — **copy their file
discovery and their exclusion conventions** rather than writing a third way to walk the tree.

**Watch it go red.** Add a timing span around a `Process.Start` in a scratch copy, confirm the
guard names it, then revert. A guard never seen failing is not known to work.

**Commit.**

---

## Task 7: Documentation and ROADMAP

- Record the two Context measurements as findings: the old `elapsed` conflated indexing with query
  time, and **indexing is measured with the embedding cache warm**.
- **Do not tick 5.1 as complete.** It is not — the publication half is gated on the §6 decision.
  Record precisely what landed and what remains, so the decision is the only thing missing.

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release --no-incremental
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
```

Then pack and run `PackageValidation` — it reads packed artefacts, and it is the only suite that
checks the packed artefact rather than the source. **Phase 4.6 shipped red because it was omitted.**

```powershell
$v = dotnet dotnet-gitversion /output json /showvariable SemVer C:\Projects\Prive\Rag.NET
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$v"
```

```bash
dotnet test tests/Rag.NET.PackageValidation.Tests
```

**State every count with arithmetic**, against the baselines above. If `Rag.NET.slnx` leaves 146
projects or the packable set leaves 69, something is wrong — investigate rather than updating the
constant.

**The deliverable is every entrant self-timed and emitting data, percentiles computed in one place,
and a guard that no figure crossed the boundary. Not a published table.**
