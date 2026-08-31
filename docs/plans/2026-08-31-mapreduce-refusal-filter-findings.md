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
