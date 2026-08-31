# Giving the engines all three instructions fixed two arms, broke a third, and missed a fourth

**Date:** 2026-08-30
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Run:** 400 queries × 6 arms, `graph-answers-results/pilot-20260830T061303Z.jsonl`, 2,400 records,
2,150 s (36 min), 17 tests / 0 failed / 0 skipped. **~$3**, against ~$20 for the full sweep.
**Follows:** `docs/plans/2026-08-30-answer-engine-sweep-findings.md`

## Why a subset ran at all

The full sweep on 2026-08-30 cost 6.5 hours and roughly $20 and produced a defect rather than a
comparison. Rather than re-run the full sweep against the fix, a 400-query subset was run first,
because the thing being checked was **categorical** — do abstentions appear where there were
previously zero — and a categorical check does not need 2,556 queries.

**That decision paid for itself immediately.** The subset found three problems, two of them new. At
full-sweep prices the same information would have cost roughly seven times as much.

## What the fix achieved

`AnswerContract` now names all three instructions `PromptTemplate` carries — grounding, abstention,
extraction — and `EngineAnswerOptions` passes the whole of it rather than only the third.

**For `chatengine`, `mapreduce` and `refine` the fix did exactly what it was meant to.** Abstentions
appeared where there had been none: **0 of 301 across every engine before, now 13-26 of 47** on the
null stratum. And the control anomaly collapsed — `chatengine − dense` went from **+0.4204 to
−0.1104** on the paper rule.

**Contract compliance is now excellent across every arm**: 393-400 of 400. Both #419's fix and this
one hold.

## The numbers

Over the 353 judged queries per arm (2,118 judged records / 6 arms) and 47 nulls:

| arm | paper | raw | strict | abstains (judged) | abstains (nulls) | contract met |
| --- | --- | --- | --- | --- | --- | --- |
| `dense` | 0.3484 | 0.2635 | 0.3201 | 222 / 353 | 21 / 47 | 398 / 400 |
| `chatengine` | 0.2380 | 0.1898 | 0.2295 | 230 / 353 | 13 / 47 | 399 / 400 |
| `mapreduce` | **0.0142** | 0.0142 | 0.0142 | 161 / 353 | 26 / 47 | 400 / 400 |
| `refine` | 0.2521 | 0.1388 | 0.2096 | 237 / 353 | 19 / 47 | 400 / 400 |
| `flarefixed` | 0.6572 | 0.2323 | 0.5269 | **3 / 353** | **0 / 47** | 393 / 400 |
| `flare` | 0.6601 | 0.2351 | 0.5297 | **3 / 353** | **0 / 47** | 394 / 400 |

`dense` at 0.3484 against its pinned 0.3499 is the expected sampling difference at 353 queries
rather than 2,255; it is not a Gate 0 failure, which is asserted over the full set.

**None of these are published as engine results, and none is pinned.** The arms are still not under
equivalent treatment — see below.

## Problem 1: `mapreduce` collapsed to 0.0142, and the cause is structural

Its predictions are literally `"not found"`, while meeting the extraction contract 400 of 400 — it
is formatting correctly and answering nothing.

#418's own note records that the system prompt reaches *"MapReduce's per-chunk maps and Refine's
rewrites"*. So the abstention rule is now applied **per chunk**. An individual chunk legitimately
lacks the answer even when the six together contain it, so every map abstains and the reduce
concludes nothing was found.

**This is the same defect shape as the FLARE runaway (#419): a whole-answer instruction applied at
fragment level.** There, a *terminal* instruction ("end your reply with…") made a fragment generator
close its answer on every call. Here, an *abstention* instruction ("if the context does not contain
enough information…") makes a per-chunk mapper abstain on every chunk. Same error, different
instruction, different engine.

`refine` at 0.2521 is depressed for the same reason in weaker form — it rewrites iteratively over
chunks, so the rule bites on each rewrite.

## Problem 2: the FLARE arms never received the contract at all

3 of 353 and **0 of 47** — unchanged from before the fix.

This is a gap in #419's own Task 4 design. It hands FLARE `FlareLoopOptions = new()` for the sentence
loop — deliberately, because a terminal instruction per fragment is what caused the runaway — and
then applies only `MultiHopRagAnswerJudge.AnswerInstruction` in the post-loop formatting call.
Grounding and abstention therefore reach FLARE **nowhere**, so its 0.66 is still the old
"always guess" figure and still not comparable to anything.

## The finding underneath all three

**There is no single instruction string that means the same thing to a single-shot engine and to an
engine that decomposes its context.**

The apparatus assumed one shared prompt makes the arms comparable. That assumption holds for
`dense` and `chatengine`, which each make one call over the whole context. It fails for `mapreduce`
(per-chunk maps), `refine` (iterative rewrites) and `flare` (per-sentence generation), because a rule
written about *the answer* gets applied to *a part*.

This is the **fifth** occurrence of this shape in Phase 6.2.1, and the first time it has been named
as a class rather than fixed as an instance:

| instance | instruction | engine | effect |
| --- | --- | --- | --- |
| #418 → #419 | terminal ("end your reply with…") | FLARE | 86,091-byte runaway, never emitted `<DONE>` |
| 2026-08-30 sweep | abstention absent | all engines | 0 of 301 abstentions; +0.42 control anomaly |
| this subset | abstention present | MapReduce | 0.0142 — abstains per chunk |
| this subset | abstention present | Refine | 0.2521 — same, weaker |
| this subset | contract absent | FLARE | 0 of 47 — still not comparable |

**The rule to carry:** before sharing an instruction across arms, ask **at what granularity each arm
will apply it**. An instruction about the final answer is only safe for an engine that produces the
final answer in one call.

## What this does not do

**Phase 6.2.1's answer-engine DoD clause remains unmet**, and the full sweep should not be funded
until the arms are under equivalent treatment. Spending ~$20 now would measure three differently
broken arms at higher precision.

## The options, for the record

1. **Apply grounding and abstention only at each engine's final synthesis step**, leaving fragment
   calls under fragment-appropriate instructions. Correct, and the engines do not currently expose
   that seam — real work in `Rag.NET.AnswerEngines`, on product surface rather than in the harness.
2. **Drop abstention from the shared contract** — keep grounding and extraction — and score
   abstention separately as its own metric rather than folding it into an accuracy average. Smallest
   change that makes the comparison mean something; it slightly redefines what the clause measures.
3. **Compare engines only against `chatengine`**, accepting that engine-vs-`dense` mixes in prompt
   effects. Cheapest, but `mapreduce`'s per-chunk problem survives it untouched.
