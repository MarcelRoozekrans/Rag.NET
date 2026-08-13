# Overnight run plan — 2026-08-13

**Operator:** unattended. **Budget:** hard cap **$100** on OpenRouter, enforced in code, not by judgement.
**Starting point:** `main` at `06de0ab0`, build 0/0, all suites green.

Written before starting because the last two nights each lost hours to something that would have
been obvious on paper: a descriptor that silently joined a theory, and a settle probe that measured
the machine it was running on.

## The constraint that orders everything

Two kinds of work, and they cannot overlap:

| | needs | why |
|---|---|---|
| **CPU-bound** — BenchmarkDotNet, #174 | a **quiet** machine | a few percent of contention is the size of the effects being measured |
| **API-bound** — extraction, reports | anything | I/O on OpenRouter; CPU is nearly idle |

So all quiet work runs first, uninterrupted, and the paid API work runs after it. Nothing is
scheduled concurrently with a benchmark. **The agent does not poll a running benchmark** — on
2026-08-12 the probing itself raised the apparent idle floor from ~1% to ~10%.

## Sequence

| # | Task | Est. | Needs quiet | Cost | Gate |
|---|---|---|---|---|---|
| 2 | **Full BenchmarkDotNet suite**, 113 methods / 37 files | **72 m** measured | **yes** | $0 | #187 merged, so MapReduce now resolves |
| 3 | Analyse vs `benchmarks.md`; regression check | ~30 m | no | $0 | |
| 4 | **PR A** — GraphRAG rows invalidated by #180 | — | no | $0 | |
| 5 | **PR B** — the other 19 sections | — | no | $0 | after A; same file |
| 1 | **Pilot** — 20 fresh articles, calibrate the real rate | ~12 m | no | ~$0.50 | — |
| 7 | **#173 extraction** — 609 articles | **~6 h** derived | no | ~$12 est | after 6 confirms the rate |
| 8 | **#172** — real community reports | 20–40 m derived | no | ~$1–2 | after 7 |
| 9 | **#173 comparative measurement** | 1–2 h derived | **yes** ideally | $0 | after 8 |
| 10 | **#174** — cold re-measure of the Real leg | ~15 m | **yes** | $0 | only if time remains |

Steps 2 and 9 both want quiet and sit at opposite ends of the night. If 9 arrives while the machine
is busy, **defer it** rather than take a contended number — that is the whole content of #174.

## Estimates, and how much to trust them

Only two figures here are measured: **72 min** for the benchmark suite, and **2,065 s** for
60-article extraction. Everything else is scaled from those.

**My scaling has been wrong by 3.4x twice this week** — the TREC-COVID leg (predicted 6 h 20 m,
took 1 h 50 m) and "roughly 600 chunks" (actual 2,044). So step 6 exists specifically to replace the
extraction estimate with a measurement before committing six hours to it. If the pilot disagrees
with the estimate by more than ~2x, **stop and re-plan rather than proceeding on a broken model.**

Step 8's duration is not derivable at all — nothing has ever generated a real community report at
scale here. Treat its estimate as a placeholder.

## Budget — bounded by the corpus, not by code

**No code change tonight.** The operator declined a tool-enforced cap, and on reflection it is not
what bounds this run anyway.

**The work is finite and known.** 609 articles chunk to ~20,750 chunks; extraction is one call per
chunk plus a gleaning pass, so ~41,500 requests, ~$12 at gpt-4o-mini rates. Community reports add
perhaps $13. The run cannot exceed the corpus — there is no loop that could run away.

So $100 is roughly **7x** the expected total. For it to bind, the cost model would have to be wrong
by nearly an order of magnitude, and step 1 exists to catch exactly that before six hours are
committed.

**The control is therefore the pilot, not a limit.** 20 fresh articles measure the real rate. If the
extrapolated total exceeds ~$30 — twice the estimate — **do not start the full run**; write down
what the pilot measured and leave the decision to the operator.

Spend is not directly observable without usage recording (#200, deliberately not done tonight), so
the report will state request counts and the derived cost, labelled as derived.

## On failure

Fix and retry, per the operator's instruction — but bounded:

- **Retry at most twice** for the same failure. A third identical failure means the diagnosis is
  wrong; stop and write down what was seen.
- **Never weaken an assertion, widen a tolerance, or delete a test to make something pass.** If a
  benchmark or test fails, that is a finding. Three separate assertions in this repo were written
  around broken behaviour rather than through it, and all three hid real defects.
- **Do not adjust a number to match an expectation.** If a count moves, report it.
- Kill strays before re-measuring. Match on the **assembly name** — xUnit v3 runs tests in a process
  named after the assembly, so a filter on `dotnet` reports a clean machine while the real consumer
  holds 2 GB.

## PRs

Separate per item where the diff allows. PR A and PR B both touch `benchmarks.md`, so **A lands
first and B rebases** — not two PRs against the same lines. #200 is independent.

If a change turns out to be inseparable, one PR is acceptable, but say why in the body rather than
letting it look like carelessness.

## What is deliberately not in scope tonight

- **The #184 bootstrapping design.** Needs a design pass, not an overnight slot.
- **Milestone 6 issues** (#189–#199) beyond what the runs touch.
- **Anything that evicts the shared embedding cache** except step 10, and only if it is reached —
  it costs every other BEIR dataset its vectors.
