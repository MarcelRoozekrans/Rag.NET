# The answer-engine sweep ran, and its accuracy figures are not an engine comparison

**Date:** 2026-08-30
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Protocol:** `docs/plans/2026-08-29-answer-engine-sweep-protocol.md`
**Run:** `~/.cache/ragnet-beir/graph-answers-results/full-20260830T011545Z.jsonl`, 15,336 records
(2,556 queries × 6 arms), 23,484 s — **6.5 hours** — 15 tests, 0 failed, 0 skipped, exit 0.

## What held

**Gate 0 held exactly.** `dense` reproduced its pinned **0.3499 paper / 0.2603 raw / 0.3242 strict**
over the 2,255 judged queries, to four decimals. The corpora did not diverge, so the run is sound as
a measurement and every number below is real.

**Gates 1-3 held at scale** — roughly 15,000 assertions rather than the pilot's 54: context identity
against `dense` chunk-for-chunk, per-arm call shape, and lookahead observed firing in `flare`.

**No runaway.** #419's fixes hold at 2,556 queries; nothing resembling the 86,091-byte generation
that killed two runs on 2026-08-29.

**The 6.5 hours is itself a finding.** The protocol estimated 3-4 hours by extrapolating the
nine-query pilot's rate and flagged that basis as thin. It was wrong by roughly 2×, in the same
manner and for the same reason that RAPTOR's plan was wrong by a factor of eight. **A rate observed
on a pilot does not survive extrapolation to a sweep**, and this is now the second time that has
been demonstrated here rather than argued.

## What the figures say, and why they do not mean what they appear to

| arm | paper | raw | strict | contract met |
| --- | --- | --- | --- | --- |
| `dense` | 0.3499 | 0.2603 | 0.3242 | 2536 / 2556 |
| `chatengine` | 0.7703 | 0.2062 | 0.6998 | 2492 / 2556 |
| `mapreduce` | 0.3073 | 0.2133 | 0.2461 | 2193 / 2556 |
| `refine` | 0.5694 | 0.1685 | 0.4931 | 2514 / 2556 |
| `flarefixed` | 0.6497 | 0.2120 | 0.5335 | 2514 / 2556 |
| `flare` | 0.6519 | 0.2137 | 0.5392 | 2523 / 2556 |

`chatengine` is the **control** — it shares `dense`'s retrieval verbatim and differs only in the
generation path — so `chatengine − dense` should isolate the prompt effect and be small. It is
**+0.4204 on the paper rule and −0.0541 on raw**: enormous, and signed in opposite directions by
rule. A control does not move like that. That is the tell, and reading the answers rather than the
scores is what explains it.

## The cause: `PromptTemplate` carries three instructions and the engines were given one

```csharp
private const string PromptTemplate =
    "Answer the question using only the context below. If the context does not contain enough " +
    "information to answer, answer exactly: Insufficient information\n" +
    MultiHopRagAnswerJudge.AnswerInstruction + "\n\n" +
    "Question: {question}\n\nContext:\n{context}";
```

1. **Grounding** — *"using only the context below"*.
2. **Abstention** — *"answer exactly: Insufficient information"*.
3. **The extraction contract** — `AnswerInstruction`.

`EngineAnswerOptions` passes **only (3)**:

```csharp
private static readonly RagOptions EngineAnswerOptions = new()
{
    SystemPrompt = MultiHopRagAnswerJudge.AnswerInstruction,
};
```

#418 found that the engines were missing the extraction contract and restored it. It restored one of
three.

### The measurement, which is not subtle

Over the **2,255 judged** queries, with context asserted identical chunk-for-chunk:

| arm | abstains on answerable | placeholder echo (`...`) | trailing period |
| --- | --- | --- | --- |
| `dense` | **1,394 (61.8%)** | 26 | 513 |
| `chatengine` | 5 | 5 | 2,162 |
| `mapreduce` | 1 | 6 | 1,401 |
| `refine` | 16 | 24 | 2,152 |
| `flarefixed` | 16 | 17 | 2,180 |
| `flare` | 17 | 16 | 2,185 |

Over the **301 null** (unanswerable) queries, scored separately as abstention:

| arm | correctly abstained | rate |
| --- | --- | --- |
| `dense` | 146 / 301 | **48.5%** |
| `chatengine` | **0 / 301** | 0.0% |
| `mapreduce` | **0 / 301** | 0.0% |
| `refine` | **0 / 301** | 0.0% |
| `flarefixed` | **0 / 301** | 0.0% |
| `flare` | **0 / 301** | 0.0% |

**Every engine arm answers every single unanswerable question.** Not approximately — zero abstentions
across 301 queries, five times over. `dense`'s 48.5% is the figure already in the record, reproduced
here independently.

So `chatengine`'s +0.42 on the paper rule is **`dense` declining to answer 62% of answerable
questions because it was instructed to, against engines that were never instructed to.** It is a
measurement of one sentence of prompt, not of any engine mechanism.

The `raw`-rule inversion has a matching mundane cause: the engines end ~2,150 of 2,255 answers with
a period and `dense` ends 513, and the raw rule counts punctuation.

**No accuracy figure in the table above may be published as an answer-engine result**, and none is
pinned in `MultiHopRagAnswerReproduction`. Pinning them would enshrine the invalid comparison as
this project's published finding, which is precisely what cost Milestone 5.2 three weeks and a
revised result.

## The lesson, stated so it is checkable next time

This is the **fourth** time in Phase 6.2.1 that a fix left an adjacent gap: #390 → #396 → #400 in
6.2.12, and now #418 → this.

#418's own recorded lesson was *"when one arm is exempted from a shared apparatus, check what the
apparatus was doing for it."* **That check was performed and came back incomplete.** The apparatus
was doing three things; the check found one and stopped, because the extraction contract was the one
the symptom pointed at.

The sharper rule: **when a shared apparatus is an inline blob of prose, enumerate every instruction
in it before deciding which the exempted arm needs.** A prompt is not one thing. `PromptTemplate` is
four sentences carrying three separable contracts, and the fact that it is a single `const string` is
what made it look like one decision.

**A guard is what makes this durable, and it cannot be written yet** — a test asserting the engine
arms answer under the same instruction set as `PromptTemplate` would fail today, which is the point.
It belongs to the re-run, not to this record.

## Deferred: the re-run

The fix is to give the engine arms the full contract — grounding, abstention, and extraction — so
every arm answers under one instruction set and the comparison is about the mechanism.

**Deferred by the operator on 2026-08-30**, because it changes every engine prompt and therefore
every engine cache key, orphaning this run's entries: another ~$5-10 and ~6.5 hours. The diagnosis is
recorded and costs nothing to hold.

**Phase 6.2.1's answer-engine DoD clause remains unmet.** Building the arms did not meet it, the
pilot did not, and this sweep does not — it produced a defect rather than a comparison. What it did
buy: Gate 0 confirms the harness is sound, the arms run clean at full scale, and the confound is now
named and measured rather than suspected.
