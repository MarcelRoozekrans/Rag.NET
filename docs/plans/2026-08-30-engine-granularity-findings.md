# Splitting the contract by granularity made four arms comparable and proved MapReduce is not

**Date:** 2026-08-30
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Run:** 400 queries × 6 arms, `graph-answers-results/pilot-20260830T093516Z.jsonl`, 5,665 s,
18 tests / 0 failed / 0 skipped, ~$3. 26,323 new cache entries.
**Follows:** `docs/plans/2026-08-30-engine-contract-subset-findings.md`

## The change

The contract is split three ways by **the granularity each rule is safe at**, rather than shared
whole or withheld whole:

| rule | who receives it | why |
| --- | --- | --- |
| **Grounding** — "using only the context below" | every arm, FLARE's loop included | constrains where content comes from; true of a sentence as of an answer |
| **Abstention** — "answer exactly: Insufficient information" | `dense` only | a statement about *the answer*; false of a part |
| **Extraction** — "end your reply with exactly this sentence" | every arm; for FLARE **only after assembly** | terminal — in FLARE's loop it produced the 86,091-byte runaway |

## `PromptTemplate` is byte-identical — proven, not asserted

`dense` returned **0.3484 / 0.2635 / 0.3201** with 222/353 judged and 21/47 null abstentions:
**identical to the previous subset, digit for digit**, having replayed entirely from cache. Its 2,556
cached answers, its pinned 0.3499 and Gate 0 are all intact. That is the empirical proof the
composition stayed byte-neutral, which no amount of reading the concatenation could give.

## The results

| arm | paper | raw | strict | abstains (judged) | abstains (nulls) | contract met |
| --- | --- | --- | --- | --- | --- | --- |
| `dense` | 0.3484 | 0.2635 | 0.3201 | 222 / 353 | 21 / 47 | 398 / 400 |
| `chatengine` | 0.6147 | 0.2238 | 0.5637 | 0 / 353 | 0 / 47 | 394 / 400 |
| `mapreduce` | **0.1898** | 0.1331 | 0.1558 | 0 / 353 | 0 / 47 | 368 / 400 |
| `refine` | 0.5467 | 0.1926 | 0.4731 | 0 / 353 | 0 / 47 | 394 / 400 |
| `flarefixed` | 0.7479 | 0.2153 | 0.6091 | 1 / 353 | 0 / 47 | 395 / 400 |
| `flare` | 0.7564 | 0.2125 | 0.6204 | 0 / 353 | 0 / 47 | 396 / 400 |

**The comparison of record is `<engine> − chatengine`** — every instruction held fixed, only the
mechanism varying:

| arm | vs `chatengine` |
| --- | --- |
| `flare` | +0.1417 |
| `flarefixed` | +0.1332 |
| `refine` | −0.0680 |
| `mapreduce` | −0.4249 |

**Not published, not pinned.** A 400-query subset is a validation run, and this thread has now
demonstrated three separate times that a small sample does not survive scale.

## MapReduce is not measurable under this apparatus, and the reason is structural

It recovered from 0.0142 to 0.1898 but stayed far below the control, with the **worst** contract
compliance of any arm (368/400). Its failures read:

> *"The TechCrunch article on Twitch's subscription revenue split policy does not provide any
> information that indicates…"*
> *"not found"*

**Grounding is also not portable to per-chunk maps.** "Answer the question using only the context
below", applied to *one* chunk of six, correctly elicits "this chunk does not answer it" — and the
reduce aggregates a pile of those into "not found". Removing abstention was necessary and nowhere
near sufficient.

**The deeper reason: MapReduce's per-chunk calls are not answering the question at all — they are
extracting facts.** No instruction phrased *"answer the question"* is correct for them, because
answering is not what that call does. This is not fixable by choosing which rules to share: the map
step and the final step are doing **different jobs**, and any single shared instruction is wrong for
one of them.

**Third instance of the granularity class**, and the one that establishes it is not about a
particular rule:

| instruction | engine | applied at | result |
| --- | --- | --- | --- |
| terminal (extraction) | FLARE | per sentence | 86,091-byte runaway (#419) |
| abstention | MapReduce | per chunk | 0.0142 — every map abstains |
| grounding | MapReduce | per chunk | 0.1898 — every map reports "not found" |

FLARE escaped only through bespoke handling — grounding in the loop, extraction after — which works
because FLARE's sentence calls genuinely *are* answering. MapReduce and Refine would need the same
per-engine synthesis seam, which is **product work in `Rag.NET.AnswerEngines`**, not a harness
change.

## A residual asymmetry, stated rather than left implicit

FLARE now receives a **dedicated post-loop formatting call that no other arm gets**. Some part of its
+0.14 may be that second pass rather than the mechanism. The current apparatus cannot separate the
two, and the figure should not be read as though it could.

## What this means for the DoD clause

The clause names **"the three answer engines"** — MapReduce, Refine and FLARE. So even a clean full
sweep on this basis **does not close it**, because MapReduce cannot be measured here without the
synthesis seam. What the sweep can deliver is a valid comparison for `refine`, `flare` and
`flarefixed` against a properly-instructed `chatengine` control, plus MapReduce documented at scale
as not-measurable with the evidence for why.

That is partial completion, and it is worth having — but it is partial, and the clause should not be
marked met on the strength of it.
