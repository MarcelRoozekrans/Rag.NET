# FLARE contract re-run pilot — result

Task 5 of [`2026-08-29-flare-contract-and-cached-options-design.md`](./2026-08-29-flare-contract-and-cached-options-design.md).

## What this settles

The 2026-08-29 design recorded two identical pilot failures — four `TaskCanceledException`s each,
every one inside `FlareAnswerEngine.GenerateSentenceAsync` — that left #418's own question
unanswered: does giving every engine arm the judge's extraction contract actually make them meet it?
Five commits (`d8b86bba`..`1d9f4f2b`) fixed the runaway and the disarmed `MaxOutputTokens` guard that
let it run to 86,091 bytes instead of stopping at 150 tokens. This re-run pilot is what answers the
question #418 was meant to settle.

## The run

- The poisoned cache entry
  (`graph-answers/37/37a756f63d8154c1fe75252766d08f51a404fbd77171bda19b93ddb41fb6aeb4.gex`, 86,091
  bytes, the contract sentence repeated 256 times) was deleted and confirmed absent before the run.
- **Total 15, Errors 0, Failed 0, Skipped 0, 172.968 s.** `Skipped: 0` is load-bearing —
  `Accuracy_AgainstTheGoldAnswers_ThreeArms` is the expensive test in this fixture and it ran rather
  than skipping.
- 469 new answer-cache entries (171 → 640 written since 2026-08-28), so this run generated rather
  than replaying the previous pilot's cache.
- No `TaskCanceledException` anywhere in the log. The runaway that killed both 2026-08-29 attempts
  did not recur.

## What the PASS proves, and what it does not

Every assertion inside `Accuracy_AgainstTheGoldAnswers_ThreeArms` held: context identity against
`dense`, call shape per arm (including the FLARE arms' extra post-loop contract call, under the
widened bounds), and the lookahead-firing gate. That is real evidence Fixes 2 and 3 — options
forwarding and the post-loop contract call — hold structurally.

**Fix 1 is not exercised by this run.** The harness hands every FLARE arm `FlareLoopOptions = new()`
— no system prompt at all — so there is never a caller instruction for FLARE to compose with its
fragment protocol. This pilot validates the harness's *avoidance* of the terminal-instruction
conflict, not FLARE's own *composition* fix; Fix 1 stands on its unit test,
`ACallerSystemPrompt_DoesNotDisplaceTheFragmentProtocol`, alone.

**It is not evidence the extraction contract is met.** `MultiHopRagAnswerJudge.UsedTheAnswerSentence`
is recorded and reported by the test, never asserted (`BeirGraphRagAnswerTests.cs:2896`), and xunit
prints nothing for a passing test — the same caveat this phase already recorded on 2026-08-28 for a
different number. **A passing test proves the harness ran cleanly. It does not, by itself, prove the
thing the harness exists to measure.** The per-arm contract figure below had to be recovered from the
run's own results file, `~/.cache/ragnet-beir/graph-answers-results/pilot-20260829T171523Z.jsonl` (54
records = 9 queries × 6 arms, each carrying `usedSentence`), rather than read off the console or
inferred from the green result.

## Extraction contract met, per arm — the headline

| arm | 2026-08-29 (this run) | 2026-08-28 (before the fix) |
| --- | --- | --- |
| `dense` | 9 of 9 | 9 of 9 |
| `chatengine` | 8 of 9 | 0 of 9 |
| `mapreduce` | 9 of 9 | 0 of 9 |
| `refine` | 9 of 9 | 0 of 9 |
| `flarefixed` | 8 of 9 | 0 of 9 |
| `flare` | 8 of 9 | 0 of 9 |

Every engine arm moved from 0 of 9 to 8 or 9 of 9. **Three arms sit at 8, not 9, and that is stated
as measured rather than rounded up to "all arms now comply."** The contract is met on the large
majority of queries, not universally, and nothing in this run explains which query each of the three
arms missed or why.

## Runaway check

Longest prediction across all 54 records: **890 characters**. Against the 86,091-byte response that
produced the two prior failures, this run's worst case is two orders of magnitude smaller, and no
record approaches the 3,747-byte historical maximum the design measured across the 47,151
pre-existing answer-cache entries.

## Accuracy — recorded, not published

Nine judged queries is underpowered for an accuracy headline, and none is published from this run.
Recorded only so the next reader knows what was seen (paper rule): `dense` 3/9, `chatengine` 7/9,
`mapreduce` 3/9, `refine` 4/9, `flarefixed` 4/9, `flare` 4/9.

RAPTOR's 50-query pilot put its headline difference at +0.0000 where the full 2,556-query sweep
found −0.0146 at p=0.0247 — a sign reversal from underpowered sampling, not noise around zero.
`chatengine` scoring 7/9 against `dense`'s 3/9 here is exactly the kind of gap a 9-query pilot
invents; it is not evidence that any engine arm outperforms dense retrieval.

## What this does not complete

This pilot answers the question the two failed 2026-08-29 runs left open — the fixes hold and the
contract is largely met — and nothing more. It does **not** complete Phase 6.2.1's DoD answer-engine
clause, which needs the full 2,556-query sweep; that sweep has not run. The phase still owes HyDE,
reranking, hybrid BM25, late chunking, SPLADE, every vector store through the SciFact parity leg, the
second-corpus RAPTOR arm, and local search's unexplained yes/no abstention.
