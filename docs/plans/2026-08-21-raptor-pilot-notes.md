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

**Status: Task 4 did not complete. The validation gate has never been evaluated.** The run was
stopped deliberately after 5 hours at the operator's decision, with the machine confirmed quiet
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

_Not run to completion. See above._

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

| Counter | Expected | Observed |
|---|---|---|
| `LeafCount` | **17,648** — anything else means documents were skipped or double-counted | _pending_ |
| `SummaryCount` | positive — the rebuild must have produced a tree | _pending_ |
| `SummariserCalls` | one rebuild's worth, not one per document | _pending_ |
| `CorpusRebuildCount` | 1 (logged only) | _pending_ |
