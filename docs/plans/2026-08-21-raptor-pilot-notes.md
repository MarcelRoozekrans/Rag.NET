# RAPTOR pilot — validation gate and derived sweep cost

Task 4 of [`2026-08-21-raptor-real-protocol-implementation.md`](./2026-08-21-raptor-real-protocol-implementation.md).

> **On the filename.** The plan names this file `2026-08-21-raptor-pilot-notes.md` and that name is
> kept so the plan's own reference resolves. The pilot actually ran on **2026-08-24** — Tasks 4-6
> were blocked on #345 until it merged 2026-08-22, and then on locating the provisioned corpus.

## Step 1 — preconditions

Checked before spending anything. The plan's instruction is explicit: **if any is false, stop and
report**; do not substitute a different model or a smaller corpus.

| Precondition | Required | Observed | |
|---|---|---|---|
| MultiHop-RAG corpus provisioned | 609 articles | `corpus.jsonl` = 609 lines | ✅ |
| Query set | 2,556 queries | `queries.jsonl` = 2,556 lines | ✅ |
| Embedding cache warm | not a cold embed (>1 h) | 256 shards, ~857k files, 1.29 GB | ✅ |
| Extraction cache | 35,176 entries | 35,176 files over 256 shards | ✅ |
| `OPENROUTER_API_KEY` | set — **not** `OPENAI_API_KEY` | set; `OPENAI_API_KEY` unset | ✅ |
| Model | `openai/gpt-4o-mini` | `BeirGraphRagAnswerTests` routes to `https://openrouter.ai/api/v1` | ✅ |
| 6.2.4 over-fetch fix on `main` | `raptorboost` needs a working `Boost` | `RaptorRetrievalOptions.CandidateMultiplier` = 3.0, merged #344 | ✅ |

### The corpus was nearly declared missing

Worth recording, because it cost a previous session a wrong conclusion. The harness reads
`RAGNET_BEIR_CACHE`, `RAGNET_ONNX_EMBED_MODEL` and `RAGNET_ONNX_EMBED_VOCAB`, and **none of the
three is set in any shell on this machine**. A prior task report checked exactly that and recorded
the corpus as unprovisioned. It was not: the data existed, in the scratchpad directory of a
months-old session, under `%TEMP%`.

**With the variables unset the harness does not fail loudly — it skips**, and a zero-passed run
exits 1, which reads as a failure rather than as a skip. That is the same silence
`RAGNET_BEIR_LONG_RUNS` produces, and it is how ~$9 of LLM caches and an hour of embedding came
within one Disk Cleanup of being rebuilt for no reason.

The cache has been moved out of `%TEMP%` to a stable path for this run. Its location is
deliberately **not recorded here** — `docs/reference/retrieval-quality.md` states the standing
policy that the cache is never committed, and the path is machine-specific.

## A defect in the plan's own commands

**Tasks 4 and 5 both specify `dotnet test --filter "FullyQualifiedName~BeirGraphRagAnswerTests"`.
That filter does nothing here, and the run it produces is not the run the plan describes.**

`Rag.NET.Benchmarks.Quality.IntegrationTests` sets `TestingPlatformDotnetTestSupport` with
`xunit.v3`, so `dotnet test` routes through xunit's in-process runner. The VSTest filter is not
translated — it is **discarded**, with a warning that scrolls past in the build output:

```
warning MTP0001: VSTest-specific properties are set but will be ignored when using
Microsoft.Testing.Platform. The following properties are set: VSTestTestCaseFilter
```

The run then executes **the entire project — 25 test classes instead of 1** — while
`RAGNET_BEIR_LONG_RUNS=1` and `RAGNET_GRAPHRAG_ANSWERS_GENERATE=1` are set, which is precisely the
combination that unlocks every expensive test in it. Observed directly: a run started 11:12:47 was
writing `arguana-semantic-kernel.trec` and `scifact-semantic-kernel.trec` — library-comparison
retrieval runs that have nothing to do with RAPTOR.

**It was caught by cost shape, not by an error.** Nothing failed. The tell was that the processes
were nearly idle — 10–21 CPU-seconds and ~100 MB working set after 23 minutes — where a RAPTOR
tree over 17,648 leaves should be saturating a core and holding gigabytes.

**The correct incantation is the runner's own option**, and the test executable is invoked directly
so no translation layer can drop it again:

```bash
EXE=tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe
"$EXE" -class '*BeirGraphRagAnswerTests*'      # 5 methods, verified with -list methods
```

The pilot script now **refuses to run** if that filter stops selecting between 1 and 8 methods, so a
whole-project sweep cannot be launched by accident.

This is the second time this plan's commands have been found to misconfigure their own run; the
first was a missing `RAGNET_BEIR_LONG_RUNS=1`, recorded during the local-search work. **Task 5's
command carries the same `--filter` defect and must be corrected before the sweep.**

### Cost of the aborted runs

**No money.** `graph-answers`, `graph-reports` and `hypotheticals` were last written at
11:08:31–11:09:13, which is the cache relocation finishing. Nothing was written to any of them
afterwards, and no TCP connection to OpenRouter was ever established. The work done was retrieval
against the local ONNX model, whose output lands in `runs/`.

### Both aborted runs left orphans, and the kill did not catch them

**The kill matched `dotnet` and `testhost`. The runner process is named
`Rag.NET.Benchmarks.Quality.IntegrationTests.exe`, so neither pattern matched it.** Both aborted
runs therefore survived being "stopped", and were found still running **90 and 92 minutes later**:

| PID | Started | CPU at discovery | Working set |
|---|---|---|---|
| 32532 | 11:11:25 | 20,068 s (5.6 h) | 6,174 MB |
| 35136 | 11:13:16 | 20,204 s (5.6 h) | 6,240 MB |

They were executing the unfiltered 25-class sweep the whole time — still writing `runs/` entries
(`fiqa-semantic-kernel`, `scifact-ragnet-control`) at 11:49–11:50, well after both were believed
dead.

**They did not spend money, but they did contaminate the pilot.** The real pilot ran alongside them
for 58 minutes and accumulated only 139 CPU-seconds in that span. **No wall-clock figure from this
pilot is quotable.** The gate is an accuracy difference and the Step 4 deliverables are counts, so
both survive; the timing does not, and no coefficient derived from it should be recorded anywhere.

**When killing a run here, match on the runner executable name, not on `dotnet`.** The process is
named after the test assembly.

## The pilot was stopped, and the plan needs re-costing before it runs again

> **Superseded — this section records the aborted 2026-08-24 attempt, not the outcome.** Task 4
> completed later the same day (Step 2 below) and the gate **held at +0.0000** on all three scoring
> rules (Step 3). The status line immediately below was true when written and is kept for the
> account of what went wrong; read Steps 2-5 for where Task 4 actually landed.

**Status at the time: Task 4 did not complete. The validation gate had not been evaluated.** The run
was stopped deliberately after 5 hours at the operator's decision, with the machine confirmed quiet
afterwards.

### What it was actually doing

The run was healthy throughout — CPU saturated, no OOM, no context-length error, both the LLM
summary cache and the embedding cache being written continuously. It was building RAPTOR trees.

**It was not close to finishing.** 4,739 LLM calls in 5 hours, at a rate that rose from 12.4/min to
a dead-steady 21.4/min and never inflected. Extrapolated, the remaining work looked like **20–25k
calls, 15–20 further hours, order $10–20** — an extrapolation from a linear rate, not a measurement,
because the harness emits nothing while running and there is no way to see how many of the 609
documents were done.

### Why: the cost model in Tasks 4 and 5 counts the wrong thing

Task 4 estimates its cost as *"roughly 50 queries × 4 new arms, most of which will hit the existing
answer cache."* **That counts answer generations — about 250 calls. It does not count tree
construction at all, and tree construction is where essentially all of the cost is.**

The `raptor` arm is the **per-document** control: the pre-6.2.3 behaviour, required for the
`raptorcorpus − raptor` difference that prices the breaking change. Evaluating it means building
**609 separate trees**, every level of every one of them an LLM summarisation. That has never been
done at corpus scale — 6.2.3 changed the default to corpus scope, and Tasks 4-6 had never run.

With `TargetClusterSize` at its default 100 and a document averaging 28 chunks
(17,648 / 609), `sizeFloor = ceil(28/100) = 1`, so BIC chooses freely up to `BicMaxK = 10` and a
document's tree descends roughly 28 → 8 → 3 → stop. A handful of calls per document, times 609
documents, times the levels — and the answers are a rounding error beside it.

### This is not #333, and the tree loop is not the problem

Checked directly, because a steady non-converging call rate is exactly #333's signature and the
consequence there was unbounded spend in a published package.

`SelectClusterCount` computes `k = Min(ComputeRawClusterCount(...), count - 1)` and returns `null`
when `k <= 1`. **Every level therefore shrinks strictly, so the loop provably terminates.** The
`k >= count` degenerate guard is still present and still unreachable, as its own comment says. The
clustering is behaving correctly; there is simply a great deal of legitimate work.

### Recommended sequencing when this is re-planned

The gate is the cheap part and it has never been run. **`raptorfiltered − dense` needs only
corpus-scope arms**, and the corpus tree is already built and cached. Validating the setup with
`dense,raptorcorpus,raptorfiltered,raptorboost` costs little and proves the corpora did not diverge
— which is the one thing that must hold before any other figure means anything. The per-document
`raptor` build is a separate, schedulable ~15–20 hour job, and committing to it *before* the gate
has held is spending on a run whose validity is unestablished.

## Step 2 — the pilot run

**Completed 2026-08-24 23:49**, after the gate-only run at 17:59 established the setup was sound.
All five arms, 50 queries, 250 scored answers, 5 tests passing with 0 failed and 0 skipped.

**1 h 56 m.** The estimate carried above — 15–20 hours remaining, worst case ~22 — was **far too
pessimistic**, and the reason is worth keeping: it extrapolated a linear call rate without
accounting for the 4,739 calls already cached from the aborted attempts, which had covered most of
the per-document tree build. The algorithmic worst case (`BicMaxK = 10` and a strict decrease per
level give at most `10+9+…+2 = 54` calls per document) is a true bound but a very loose one; BIC
does not descend one-at-a-time in practice.

Results: `graph-answers-results/pilot-20260824T214907Z.jsonl`.

## Step 3 — the validation gate: **HELD**

```
raptorfiltered − dense (paper)  = +0.0000
raptorfiltered − dense (raw)    = +0.0000
raptorfiltered − dense (strict) = +0.0000
```

**Zero on all three scoring rules.** The plan set the bar at #274's precedent, which reproduced to
four decimals on two rules; this matches exactly on three. The corpora did not diverge, so the
figures below measure RAPTOR rather than a setup fault. The tolerance was not touched.

Confirmed twice: the gate-only run at 17:59 (four corpus-scope arms) and the full five-arm run at
23:49 both give +0.0000.

### The arms

| arm | n | paper | raw | strict |
|---|---:|---:|---:|---:|
| `dense` | 50 | 0.3200 | 0.2200 | 0.3000 |
| `raptorcorpus` | 50 | 0.3000 | 0.2200 | 0.2800 |
| `raptor` | 50 | 0.3000 | 0.2400 | 0.3000 |
| `raptorfiltered` | 50 | 0.3200 | 0.2200 | 0.3000 |
| `raptorboost` | 50 | 0.3200 | 0.2000 | 0.2800 |

### The four differences the plan asks for (paper rule)

| difference | value | what it prices |
|---|---:|---|
| `raptorcorpus − raptor` | **+0.0000** | what 6.2.3's breaking change bought |
| `raptorcorpus − raptorfiltered` | **−0.0200** | what the summaries do to the answer |
| `raptorboost − raptorcorpus` | **+0.0200** | what a working `Boost` buys |
| `raptorfiltered − dense` | **+0.0000** | the gate |

**None of this is a finding and none of it may be quoted.** n=50 against pins built on 2,255, and
the type mix is skewed — 11 temporal questions scoring 0.0000 in *every* arm, and 6 nulls. At this
size a ±0.0200 difference is **one question**. The previous pilot on this harness had its
provisional readings marked not-to-be-quoted for exactly this reason.

What is worth carrying into Task 5 as a hypothesis to test, not as a result:

- **`raptorcorpus − raptor = +0.0000` is the one to watch.** 6.2.3 shipped corpus-level clustering
  as a *breaking change* on the argument that a per-document tree is not the paper's mechanism. At
  this scale it shows no answer-level difference at all. If that survives to 2,255 queries it is a
  real finding, and a legitimate completion under Milestone 6's measured-not-good bar — but it is
  also exactly the kind of null that n=50 is worst at detecting.
- **The summaries look like they displace rather than help** (`−0.0200`), which is #247's mechanism
  again, and **`Boost` looks like it buys back about what they cost** (`+0.0200`).
- Per-type, `raptor` leads on inference (0.6250 against dense's 0.5625) while trailing on
  comparison (0.1176) and nulls (0.5000). At n=16 and n=6 that is noise until it is not.

### Per-type (paper rule)

| type | n | `dense` | `raptorcorpus` | `raptor` | `raptorfiltered` | `raptorboost` |
|---|---:|---:|---:|---:|---:|---:|
| comparison | 17 | 0.1765 | 0.1765 | 0.1176 | 0.1765 | 0.2353 |
| inference | 16 | 0.5625 | 0.5000 | 0.6250 | 0.5625 | 0.5000 |
| null | 6 | 0.6667 | 0.6667 | 0.5000 | 0.6667 | 0.6667 |
| temporal | 11 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 |

**Every arm scores 0.0000 on all 11 temporal questions.** Dense's pinned full-scale figure is
0.0326, so this is consistent with the small sample rather than surprising — but a whole type
scoring zero across five arms is worth confirming at full scale rather than assuming.

## Step 3 — the validation gate

**`raptorfiltered − dense` must be ≈ 0.** Both arms see the same article chunks; `raptorfiltered`
merely removes the summaries. A difference means the two corpora diverged, and **no other figure in
the table would mean anything**. #274's equivalent check reproduced to four decimals on both
scoring rules.

If the gate fails: stop, report, and **do not widen the tolerance to make it pass.**

_Pending._

## Step 4 — the tree the run actually built

`CorpusRebuildCount` is logged, **not gated on** — `RaptorRun` sets it to 1 beside its one
`RebuildAsync` call by construction, so a check against it can never fire. The counters that can
actually move are the ones below.

**These were nearly lost.** `LogRaptorRunCounters` writes them to `ITestOutputHelper`, and this
project runs xunit v3 through Microsoft.Testing.Platform, which does not surface a **passing**
test's output — so on the run that succeeded, the deliverables were invisible. They are now also
written to a `*.counters.json` sidecar beside the answers dump, the way the per-query rows already
survived. Measured 2026-08-25 from `pilot-20260825T054049Z.counters.json`.

| Counter | Expected | corpus | per-document |
|---|---|---:|---:|
| `LeafCount` | **17,648** — anything else means documents skipped or double-counted | **17,648** ✅ | **17,648** ✅ |
| `SummaryCount` | positive — the rebuild must have produced a tree | 187 ✅ | 7,187 ✅ |
| `SummariserCalls` | one rebuild's worth, not one per document | 187 ✅ | 7,187 |
| `CorpusRebuildCount` | 1, logged only — cannot gate | 1 | 0 |

### The corpus tree reproduces #364's measurement

| level | chunks | clusters | largest | imbalance |
|---:|---:|---:|---:|---:|
| 1 | 17,648 | 177 | **549** | **5.51x** |
| 2 | 177 | 5 | 47 | 1.33 |
| 3 | 5 | 3 | 3 | 1.80 |
| 4 | 3 | 2 | 2 | 1.33 |
| 5 | 2 | — | — | terminates |

Level 1 is identical to the figure recorded in #364 — 177 clusters, largest 549, 5.51x — so the
tree is the same one, rebuilt from cache. It terminates cleanly when 2 chunks yield k ≤ 1.

### The per-document arm, and how badly it was estimated

**7,187 calls over 2,287 levels: 609 trees averaging 3.75 levels and 11.8 summariser calls each.**

The estimate in this document's earlier sections — "worst case 54 calls per document, ~22 hours" —
was a true algorithmic bound (`BicMaxK = 10` plus a strict per-level decrease) and **useless in
practice**: the real figure is a fifth of it. BIC does not descend one level at a time. Recorded
because the bound was used to advise on whether an overnight run would finish, and it would have
been wrong by a factor of five in the cautious direction.

## Step 5 — Task 5's cost, derived rather than extrapolated

This is what Step 4's counters exist for.

| | |
|---|---|
| Tree construction | **0 new calls** — both trees cached, `SummariserCalls` already paid |
| Task 5 scope | 2,556 queries × 4 arms = 10,224 |
| Less the pilot's overlap | ~200 |
| **New generations** | **~10,000** |

At the ~21/min observed yesterday that is roughly **8 hours**.

**Stated as an estimate, not a throughput measurement.** That rate was observed during *tree
summarisation*, whose prompts are large; answer generation has a different prompt shape and may run
at a different rate. Task 5 is also a different cost shape from Task 4 — pure answer generation, with
no cached head start to absorb an overrun the way yesterday's 4,739 calls did.
