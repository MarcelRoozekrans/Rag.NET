# The answer-engine sweep — protocol

**Date:** 2026-08-29
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Closes:** the DoD clause *"the three answer engines through the 5.2.2 harness against
MultiHop-RAG's gold answers"* — which building the arms did not, and a nine-query pilot did not.

## Why this needs a protocol rather than just a command

The arms are built (#416), the contract defect is fixed (#419), and the pilot ran. What remains is
one paid run. It gets a written protocol anyway, because the closest precedent — RAPTOR's Task 5 —
found **three defects in its own plan**, including a cost model that omitted tree construction
entirely and a `dotnet test --filter` that was silently discarded while the run executed unrelated
sweeps for hours. Neither was visible without writing the protocol down first.

## Preconditions — if any is false, stop and report

- `~/.cache/ragnet-beir/env.sh` sourced. The machine **is** provisioned: corpus, `model.onnx`,
  `vocab.txt`, embedding shards. Three sessions have read a skip as "unprovisioned" and written it
  into the record; check the variables are exported before concluding anything.
- `OPENROUTER_API_KEY` set. Model is `openai/gpt-4o-mini`; the harness routes through OpenRouter.
- `main` at or after `50221812` (#419). Without it FLARE runs away and the sweep dies, twice over.
- No orphaned runners: `tasklist | grep -i "Rag.NET"` is empty.
- Release build, 0 warnings.

## The run

```bash
source ~/.cache/ragnet-beir/env.sh
RAGNET_BEIR_LONG_RUNS=1 \
RAGNET_GRAPHRAG_ANSWERS_GENERATE=1 \
RAGNET_GRAPHRAG_ANSWERS_ARMS=dense,chatengine,mapreduce,refine,flarefixed,flare \
./tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe \
  -class '*BeirGraphRagAnswerTests*'
```

**`RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES` is deliberately absent — its absence is what makes this the
full sweep.** Setting it produces another pilot.

**Never `dotnet test --filter`.** `TestingPlatformDotnetTestSupport` with xunit.v3 discards the
VSTest filter and runs all 25 classes with every expensive gate unlocked. Verify the filter narrows
first with `-list methods -class '*BeirGraphRagAnswerTests*'`, and **read the class names, not the
count** — the count has changed three times (5, then 8, now 15).

**`dense` is included and costs nothing.** Its answers are cached: nothing in #418 or #419 touched
`PromptTemplate`, so its cache keys are unchanged. It is in the run to serve as Gate 0.

**Killing a run:** by assembly name `Rag.NET.Benchmarks.Quality.IntegrationTests.exe`, never
`dotnet` or `testhost`. Two "stopped" runs once survived that mistake and were found 90 minutes
later at 5.6 CPU-hours each, starving their replacement.

## Gates

**Gate 0 — `dense` reproduces its pin.** `dense` is pinned at **0.3499 paper / 0.2603 raw / 0.3242
strict** over the 2,255 judged queries. It must reproduce to four decimals. If it does not, the
corpora diverged and **no engine figure in the run means anything** — stop, and do not publish.
This gate is free, because `dense` replays from cache.

**Gates 1-3 run per query, already asserted in the test** — context identity against `dense`,
per-arm call shape, and lookahead observed firing in `flare`. At 2,556 queries they assert
~15,000 times rather than the pilot's 54. A failure fails the run.

**Gate 4 — the extraction contract at scale.** The pilot measured `dense` 9/9 and every engine 8 or
9 of 9, up from 0 of 9. **This is reported, never asserted** (`UsedTheAnswerSentence` is recorded
into the results, and xunit prints nothing for a passing test), so it must be read from the results
file. A large drop at scale invalidates the accuracy figures the same way 0-of-9 did, and would mean
the pilot's 8-or-9 was a small-sample artefact.

## Reading the result

**A PASS does not carry the numbers.** Read them from
`~/.cache/ragnet-beir/graph-answers-results/full-<timestamp>.jsonl` — one record per (query, arm)
with `paper`, `raw`, `strict`, `usedSentence` and the prediction.

Report, per arm: contract met (`usedSentence`), accuracy on all three rules over the **2,255 judged
queries** — the denominator every other pin in this project uses — and the 301 nulls scored
separately as abstention.

**Significance:** the engine comparisons are paired over the same queries, so McNemar as Task 5 used
it. `<engine> − chatengine` is the mechanism effect; `chatengine − dense` is the prompt effect
alone. That separation is the entire reason `chatengine` exists as a control, and it is what Milestone
5.2 lacked when it cost three weeks and a revised published finding.

## Cost and time

- **~36 calls per query across six arms ≈ 92,000 calls, on the order of $5–10.** Measured, not
  derived: the pilot observed FLARE at ~11 calls per query against a ceiling of 33.
- **Time ~3–4 hours**, extrapolated from the pilot's ~10.9 cache-entries/second. **This is a thin
  basis** — nine queries, and the pilot's own notes warn that a rate observed on a small sample
  misled the RAPTOR plan by a factor of eight in the other direction. Treat it as an order of
  magnitude, not an estimate.
- Cache entries are a **lower bound** on calls: identical prompts collapse to one entry.

## What gets pinned afterwards

`MultiHopRagAnswerReproduction` holds the five engine arms with **empty figure arrays**
(`ChatEngine` line 244, `MapReduce` 256, `Refine` 267, `Flare` 278, `FlareFixed` 290), deliberately
outside the default replay set because they cost real API calls. Pinning their figures is
data-driven: an arm rejoins the default set the moment it has one, with no code change.

Then, per Phase 6.0's guards: `docs/reference/features.md`'s answer-engine rows point at what
exercises them, and the packages' `VerifiedBy` is reconsidered.

## What this run must not do

**Publish an accuracy headline before Gate 0 holds.** And **no per-type headline from a thin
stratum** — Task 5's pilot had 11 temporal questions scoring 0.0000 in every arm.

**Nothing gets changed on the strength of these numbers in this run.** Measured is the bar; a
feature measured and found wanting is a completion, as 5.2 was. If an engine loses to `chatengine`,
that is the finding, not a bug to fix.

## Stop conditions

- Gate 0 fails → stop, publish nothing, investigate corpus divergence.
- Any gate assertion fails → stop and report; do not re-run hoping.
- The run dies twice in the same place → stop. Two identical failures already established, on
  2026-08-29, that a third run is not evidence.
