# The answer-engine sweep, on a corrected apparatus: two clean findings

**Date:** 2026-08-30
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Run:** `graph-answers-results/full-20260830T202509Z.jsonl`, 15,336 records (2,556 queries × 6 arms),
19,711 s — **5.5 hours** — 18 tests / 0 failed / 0 skipped, 153,307 new cache entries.
**Follows:** the granularity split in `docs/plans/2026-08-30-engine-granularity-findings.md`.

## Gate 0 held on all three rules

`dense` reproduced its pinned figures **exactly** over the 2,255 judged queries:

| rule | measured | pinned |
| --- | --- | --- |
| paper | **0.3499** | 0.3499 |
| raw | **0.2603** | 0.2603 |
| strict | **0.3242** | 0.3242 |

The corpora did not diverge, and `PromptTemplate` is byte-identical for the third time running. The
figures below mean something.

## Results

| arm | paper | raw | strict | vs `chatengine` | nulls abstained | contract met |
| --- | --- | --- | --- | --- | --- | --- |
| `flare` | 0.7503 | 0.2075 | 0.6284 | +0.1162 | 0 / 301 | 2546 / 2556 |
| `flarefixed` | 0.7428 | 0.2080 | 0.6213 | +0.1086 | 1 / 301 | 2543 / 2556 |
| `chatengine` | 0.6341 | 0.2262 | 0.5933 | — control | 0 / 301 | 2526 / 2556 |
| `refine` | 0.5286 | 0.1800 | 0.4652 | −0.1055 | 0 / 301 | 2520 / 2556 |
| `mapreduce` | 0.2009 | 0.1539 | 0.1681 | −0.4333 | 0 / 301 | 2333 / 2556 |
| `dense` | 0.3499 | 0.2603 | 0.3242 | *(not comparable)* | 146 / 301 | 2536 / 2556 |

McNemar over the paired judged queries, paper rule:

| comparison | wins | losses | p |
| --- | --- | --- | --- |
| `flare` vs `chatengine` | 379 | 117 | `<0.0001` |
| `flarefixed` vs `chatengine` | 364 | 119 | `<0.0001` |
| `refine` vs `chatengine` | 132 | 370 | `<0.0001` |
| `mapreduce` vs `chatengine` | 37 | 1014 | `<0.0001` |
| **`flare` vs `flarefixed`** | **31** | **14** | **0.0135** |

## The two clean findings

### 1. Sequential refinement is significantly worse than answering once

**`refine − chatengine` = −0.1055, `p<0.0001`, losing on 370 queries and winning on 132.**

This comparison is uncontaminated. `refine` and `chatengine` receive an identical system prompt, an
identical single call path and no extra passes; the only difference is the mechanism. Rewriting an
answer sequentially across six chunks **loses to answering once** on this corpus.

Read as a completion rather than a defect — *"a feature measured and found wanting is a completion,
as 5.2 was"* is this milestone's stated bar.

**One caveat kept attached to it:** `refine` rewrites per chunk, so it is a weaker instance of the
granularity problem that makes `mapreduce` unmeasurable, and some of the deficit may be that rather
than the mechanism. It is pinned regardless, because unlike `mapreduce` it meets the extraction
contract on 98.6% of queries and produces real answers.

### 2. FLARE's lookahead helps, and by under one percentage point

**`flare − flarefixed` = +0.0075, p=0.0135, 31 wins to 14.**

This is the cleanest measurement in the set and the only direct one of FLARE's actual mechanism: the
two arms are the identical engine differing only in whether the mid-generation lookahead may fire.
It is significant, and it is small. Both facts belong in any statement of it.

## What is not clean, and is labelled so

**The FLARE arms' ~+0.11 over `chatengine` is confounded.** They receive a post-loop formatting call
applying the extraction contract to the assembled answer, which no other arm gets. Part of that
margin is the second pass, and the apparatus cannot separate it. `flare − flarefixed` is unaffected,
because both arms receive it.

**`chatengine − dense` = +0.2843 is not an engine result.** `dense` answers under an additional
abstention rule and declines 1,394 of 2,255 answerable queries; the engines carry no such rule and
decline none. That gap is one sentence of prompt.

**`mapreduce`'s −0.4333 is an apparatus failure**, confirmed at scale — worst contract compliance of
any arm at 2,333/2,556. Not pinned. See the granularity findings.

## What the 400-query subset predicted

| arm | subset | full sweep | moved |
| --- | --- | --- | --- |
| `flare` | +0.1417 | +0.1162 | −0.026 |
| `flarefixed` | +0.1332 | +0.1086 | −0.025 |
| `refine` | −0.0680 | −0.1055 | −0.038 |
| `mapreduce` | −0.4249 | −0.4333 | −0.008 |

**Every sign held.** Magnitudes moved 0.008–0.038. This is the fourth pilot-versus-scale data point
in this phase and **the first where the direction survived** — after RAPTOR's +0.0000 becoming
−0.0146, the answer-engine pilot's uninterpretable 0-of-9 contract, and a wall-clock estimate wrong
by 2×. A 400-query subset costing ~$3 predicted a ~$20 sweep's direction correctly on all four arms;
it did not predict the magnitudes, and should not be trusted to.

## Pinned

`chatengine` 0.6341, `refine` 0.5286, `flarefixed` 0.7428, `flare` 0.7503 — in
`MultiHopRagAnswerReproduction`, each with its caveats in the entry rather than in a footnote. All
four rejoin the default replay set automatically, which is the data-driven selection working;
`SelectArms_DefaultSelection_ContainsOnlyArmsWithARecordedFigure` failed on the pin and named the
list to update, so the movement was deliberate rather than silent.

**`mapreduce` is not pinned.** It ran; its figure measures a known-broken setup.

## The DoD clause is still not met

It names **"the three answer engines"** — MapReduce, Refine and FLARE. Refine and FLARE now have
pinned figures with a control. MapReduce does not, and cannot until `MapReduceAnswerEngine` can apply
caller instructions to its reduce step rather than to every map — product work in
`Rag.NET.AnswerEngines`, not a harness change.

**Two of three engines measured is partial completion, and the clause must not be ticked on it.**
