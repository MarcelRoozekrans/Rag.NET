# MapReduce was not bad at this corpus — it had a defect, and the sweep's reading of it was wrong

**Date:** 2026-08-31
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Run:** 400 queries × 3 arms, `graph-answers-results/pilot-20260831T072556Z.jsonl`, 3,594 s,
18 tests / 0 failed / 0 skipped, 2,761 new cache entries (~$0.36).
**Corrects:** `docs/plans/2026-08-30-engine-granularity-findings.md` and the `mapreduce` entry in
`MultiHopRagAnswerReproduction`.

## The result

| arm | paper | raw | strict | contract met |
| --- | --- | --- | --- | --- |
| `dense` | 0.3484 | 0.2635 | 0.3201 | 398 / 400 |
| `chatengine` | 0.6147 | 0.2238 | 0.5637 | 394 / 400 |
| **`mapreduce`** | **0.6487** | 0.1275 | 0.5977 | **400 / 400** |

`dense` and `chatengine` are **identical to the previous subset**, having replayed wholly from cache
— their prompts did not change. 2,761 new entries against a predicted 2,800 confirms only
`mapreduce` generated.

| `mapreduce` | before the fix | after |
| --- | --- | --- |
| paper | 0.1898 | **0.6487** |
| contract met | worst of any arm | **400 / 400** |
| answers containing "not found" | the majority | **1 of 353** |

**`mapreduce − chatengine` = +0.0340.** It went from apparently the worst engine by a wide margin to
slightly ahead of the single-shot control.

## What was wrong, and it was recorded wrongly

The 2026-08-30 record said `mapreduce`'s −0.4333 was **"an apparatus failure rather than a property
of the engine"**, that **"MapReduce cannot be measured by an apparatus that shares one instruction
across arms"**, and that its per-chunk calls **"are extracting facts rather than answering the
question, so an instruction phrased 'answer the question' is false of a single chunk"**.

**That reasoning was mistaken.** It was elaborate, it fit the evidence available, and it was wrong.

The whole deficit was **one defect**: MapReduce drops `not found` partials by an **exact** string
match before the reduce, and a caller system prompt that changes the shape of a reply defeats that
match. Under the extraction contract, refusals came back as
`Not found. The answer to the question is "not found".` — not equal to `not found`, so they survived
into the reduce, which then treated them as contradicting the one correct partial and discarded it.

Fix the protocol so the sentinel survives, and the engine is competitive. No granularity problem, no
"different jobs at different steps", and — retired with it — **no evidence that MapReduce is bad at
multi-hop questions.** It handles them fine.

## How the wrong reading survived two runs

The full sweep and both earlier subsets all reported the same low number, consistently, which read as
corroboration. It was not: **the same defect reproducing.** Consistency across runs measures
reproducibility, not correctness.

What broke it open was reading a **transcript** rather than aggregate scores — logging every map call
against a real model, roughly 20 calls. That showed a map returning `The answer to the question is
"Microsoft".` and the reduce discarding it in favour of three unfiltered refusals. No amount of
staring at per-arm accuracy would have produced that.

**The first diagnostic attempt refuted the hypothesis** — a single-hop fixture produced bare
`not found` refusals, the filter caught them, and the engine answered correctly. The defect needs
several maps to refuse *and* the phrasing to be reshaped. A cheap experiment that says "your theory
is wrong" is worth as much as one that confirms it.

## Consequences

**The DoD clause becomes closable.** All three named engines — MapReduce, Refine and FLARE — are now
measurable. MapReduce was the only blocker.

**`mapreduce` is still not pinned here.** 400 queries is a validation run; the pin needs the full
2,556. Direction has survived pilot-to-scale before in this phase, magnitude never has.

**`refine` needs re-examination, and its pinned figure carries more doubt than its entry admits.**
It scored −0.1055 against the control and was pinned with a caveat that some of the deficit *may* be
structural rather than mechanism. `refine` rewrites sequentially over chunks — a per-chunk shape —
and MapReduce has just demonstrated that a per-chunk shape can hide a defect that costs 0.46. The
caveat should be read as a live question, not a hedge.

## The rule this adds

**A number that reproduces is not a number that is right.** Three runs agreed on `mapreduce`'s
figure and all three were measuring the same defect. When a result is surprising, the cheapest
decisive move is to read what the model actually sent and received — not to run it again at higher
precision.

---

## The full measurement, 2026-08-31 — and it is a null result

**Run:** `graph-answers-results/full-20260831T083906Z.jsonl`, 15,336 records, 1,817 s (**30 min**),
18 tests / 0 failed / 0 skipped, 14,867 new cache entries (~$2).

**Both controls held**, and both replayed from cache so neither cost anything:

| arm | measured | pinned |
| --- | --- | --- |
| `dense` | 0.3499 / 0.2603 / 0.3242 | 0.3499 / 0.2603 / 0.3242 |
| `chatengine` | 0.6341 / 0.2262 / 0.5933 | 0.6341 |

Nothing drifted underneath the measurement.

**`mapreduce`: 0.6483 paper** (raw 0.1157, strict 0.6137), contract met on **2,553 of 2,556** — up
from 2,333 before the fix, and from a pre-fix accuracy of 0.2009.

### `mapreduce − chatengine` = +0.0142, p = 0.2955 — not significant

462 wins against 430, across 892 discordant pairs. **The map/reduce mechanism buys nothing
measurable over a single call on this corpus.**

That is the finding. Not a defect, not a win — a null result, and this milestone's bar treats a
feature measured and found unremarkable as a completion, exactly as 5.2 was.

### How close this came to being published as a win

The 400-query validation subset put the same difference at **+0.0340**, which reads as a real gain.
At full scale it is **+0.0142 at p=0.2955**.

Earlier pilot-to-scale misses in this phase moved a *magnitude*. This one moves the *conclusion*: a
subset can carry a direction and **cannot carry a significance**. Had the pin been taken from the
subset — which was affordable, fast, and had just been vindicated — the record would now claim
MapReduce beats a single-shot call.

**Third timing miss, too.** ~6.4 hours was projected from the 400-query run's 60 minutes; it took 30.
Every extrapolated timing estimate in this phase has been wrong, in both directions.

## The DoD clause closes

All three named engines now have a pinned figure against a properly-instructed control:

| engine | vs `chatengine` | verdict |
| --- | --- | --- |
| MapReduce | +0.0142, p=0.2955 | no measurable difference |
| Refine | −0.1055, `p<0.0001` | significantly worse |
| FLARE | +0.1162, `p<0.0001` (confounded); `flare − flarefixed` +0.0075, p=0.0135 | lookahead helps, slightly |

`UnmeasuredEngineArms` is now **empty** — every arm in `AnswerArm.All` carries a figure. Reaching
that took three separate failures of
`SelectArms_DefaultSelection_ContainsOnlyArmsWithARecordedFigure`, each naming the arm and the list
to update, so the default replay set moved deliberately rather than drifting.

**`refine` remains the open question.** Its −0.1055 was pinned with a caveat that some deficit may be
structural rather than mechanism, and MapReduce has now shown that a per-chunk shape can hide a
defect worth 0.45. That caveat should be read as a live question, and a transcript is the cheap way
to settle it — transcripts resolved the last two of these where aggregate scores misled at three
sample sizes.
