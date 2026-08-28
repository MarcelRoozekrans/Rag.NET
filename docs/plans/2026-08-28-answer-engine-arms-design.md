# Answer-engine arms — measure MapReduce, Refine and FLARE through the 5.2.2 harness

**Phase:** 6.2.1 — Retrieval & Answer Sweep. One thread of the sweep, not the phase.
**Status:** design, 2026-08-28.
**Surface:** Backend.

## The gap

Milestone 6's Definition of Done requires *"the three answer engines through the 5.2.2 harness against
MultiHop-RAG's gold answers"*. `MapReduceAnswerEngine`, `RefineAnswerEngine` and `FlareAnswerEngine`
all ship in `Rag.NET.AnswerEngines`, and **none of them has ever been measured**.

The reason is structural rather than neglect. `BeirGraphRagAnswerTests` varies *retrieval* per arm —
`RetrieveContextAsync` switches on the arm to build context — and then generates every answer with
**one hand-written prompt**, shared by every arm. The arm dimension is retrieval-only. Adding answer
engines means adding a second dimension the harness does not currently have.

## What is being built

Five new arms. Each is **`dense` retrieval + a different generation strategy**, reusing the existing
`AnswerArm.Dense` retrieval case *verbatim* — retrieval is held fixed by sharing the code path, not
by reimplementing it.

| Arm | Retrieval | Generation | What its difference isolates |
| --- | --- | --- | --- |
| `dense` *(exists, pinned 0.350)* | dense top-6 | the inline prompt | — the incumbent |
| `chatengine` *(new, the control)* | dense top-6 | `ChatAnswerEngine`, single-shot | `chatengine − dense` = **the prompt effect, quantified** |
| `mapreduce` | dense top-6 | `MapReduceAnswerEngine` | vs `chatengine` = the map-reduce mechanism |
| `refine` | dense top-6 | `RefineAnswerEngine` | vs `chatengine` = the refine mechanism |
| `flarefixed` | dense top-6 | FLARE, `MaxRetrievals = 0` | vs `chatengine` = sentence-by-sentence generation |
| `flare` | dense top-6 **+ lookahead** | FLARE as shipped | vs `flarefixed` = **what lookahead buys** |

### Why there is a control arm at all

Each engine builds its own prompts internally. Differencing `mapreduce` against `dense` would bundle
the *mechanism* with a *prompt change*, and no result could say which caused what. `chatengine` is
single-shot through the same routing, so it differs from `dense` only in prompt wording and from the
multi-call engines only in mechanism.

Prompt-versus-mechanism confounding is not a hypothetical concern here: it is what cost Phase 5.2
three weeks and a revised published finding.

## The pinned figures are protected by construction

The engines receive the existing `CachedGraphRagClient` as their `IChatClient` and build their own
prompts. **The answer cache is keyed on prompt text**, so every engine prompt is a new key and no
existing entry is touched. The inline prompt constant is not edited — one character would rekey the
whole cache, costing the three pinned answer figures and roughly $9 of warm cache.

This deliberately leaves Phase 5.2.2's recorded deviation — *"generation lives in the answer test
class rather than the tool"* — **unfixed**. Routing the existing arms through `IAnswerEngine` would
change their prompts, change their keys, and make the pinned figures unreproducible. That is a
re-baselining with its own evidence requirements, not a side effect of adding arms.

## Cost, measured from the engines' call patterns

Read off the implementations rather than estimated. At top-6 context over the sweep's 2,556 queries:

| Arm | Calls per query | Over 2,556 queries |
| --- | --- | --- |
| `chatengine` | 1 | ~2,600 |
| `refine` | 1 initial + 5 refine = 6 | ~15,300 |
| `mapreduce` | 6 map + 1 reduce = 7 | ~17,900 |
| `flarefixed` | up to 15 generation **+ up to 15 scoring** = 30 | up to ~76,600 |
| `flare` | up to 30, plus lookahead retrievals | up to ~76,600+ |

**Worst case ≈ 189,000 calls — roughly 34× the RAPTOR full sweep's ~5,600 answers**, with larger
prompts, since each carries top-6 context.

**The doubling is `SelfAssessmentConfidenceScorer`**, FLARE's default `IConfidenceScorer`, which
makes its own LLM call per sentence. A first pass at this estimate omitted it entirely and put the
total at ~70,000 — the same failure as the RAPTOR plan's cost model, which counted answers and
omitted tree construction. **An estimate that omits a call category is the recurring way this
project misprices a run**, which is why the pilot below prices the sweep from measured counters
rather than from any figure in this document.

## The pilot: a gate, not a headline

RAPTOR's pilot taught both halves. Its **gate held and saved the sweep**; its **headline (+0.0000)
was underpowered and reversed at full scale** (−0.0146, p=0.0247, on 2,255 queries). So this pilot
gates and explicitly refuses to publish accuracy.

### Three mechanical gates, all falsifiable

1. **Context identity.** For every pilot query, each engine arm's context must be byte-identical to
   the `dense` arm's — same chunk ids, same order. Stronger than RAPTOR's gate, which inferred
   corpus identity from a score landing near zero; here it is asserted directly, because the arms
   share the retrieval code path. If it fails, retrieval is not held fixed and no engine difference
   means anything. `flare` is gated on its **initial** context only — its lookahead additions are
   the thing being measured.

2. **Call counts match the predicted shape.** `chatengine` exactly 1, `refine` 6, `mapreduce` 7,
   FLARE ≤ 30. If MapReduce makes one call it is not doing map-reduce; if it makes forty, the cost
   model is wrong and the sweep is unaffordable. This is the gate RAPTOR lacked, and its absence is
   why an ~8-hour estimate built on a summarisation rate survived into a plan.

3. **Lookahead is observed firing in `flare`.** This exists because of a specific hazard:
   `SelfAssessmentConfidenceScorer` **fails open** — any error or unparsable output returns `1.0`,
   above the `0.6` threshold, so no lookahead fires. Under a cache-replay run that refuses on miss,
   every scorer call that missed would fail open and **`flare` would silently degrade into
   `flarefixed`** while still reporting as `flare`. Without this gate, `flare − flarefixed ≈ 0` has
   two readings — "lookahead does nothing" and "lookahead never ran" — and only one of them is a
   finding.

### Two interpreted observations, reported not asserted

- **`chatengine − dense` is the prompt effect.** Not automatically a failure — the prompts genuinely
  differ — but a large value is a stop-and-diagnose, because it bounds how much of any engine result
  is really the engine.
- **The FLARE fork resolves here.** `flare − flarefixed` at 50 queries says whether lookahead does
  anything detectable. If it does not, the sweep carries one FLARE arm instead of two and halves the
  largest cost line.

### The sweep is priced from the pilot's counters

The pilot emits calls-per-query and tokens-per-query per arm; the sweep's cost is those numbers times
2,556. **Never a rate observed elsewhere** — that is the specific mistake behind RAPTOR's "~8 hours",
taken from tree summarisation, whose prompts are far larger than answer generation's.

### And the pilot publishes no accuracy headline

Fifty queries with this dataset's skewed type mix put RAPTOR's corpus-versus-per-document difference
at exactly +0.0000 when the true value was −0.0146 at p=0.0247. Any accuracy number the pilot
produces goes into the notes as *"underpowered, not a result"*.

## Components

| File | Change |
| --- | --- |
| `AnswerArm.cs` | five new arm constants, added to `All` |
| `AnswerEngineArms.cs` *(new)* | builds each engine over the shared `CachedGraphRagClient`; owns the throwing stub retriever |
| `AnswerEngineArmsTests.cs` *(new)* | fast-tier call-shape assertions |
| `BeirGraphRagAnswerTests.cs` | engine arms reuse the `Dense` retrieval case; a generation switch; the three gates |

### What can be verified on an unprovisioned machine

**The cost model becomes a fast-tier test rather than a hope.** A counting fake `IChatClient` over six
synthetic sources asserts each engine's call shape with no corpus, no model and no spend:
`chatengine` exactly 1, `refine` 6, `mapreduce` 7, `flarefixed` ≤ 30 with **zero** retrievals. The
number that decides whether a ~189,000-call sweep is affordable is therefore checked before anyone
provisions anything.

**`flarefixed`'s zero-retrieval claim is structural, not observed.** Its `IRetriever` is a stub that
**throws if called**. At `MaxRetrievals = 0` the retriever is unreachable, so the stub never fires;
if a future change ever reaches it, the test fails loudly instead of quietly retrieving. A counter
reading zero and a code path that cannot execute are different guarantees.

### The `flare` arm's dependency on #414

Shipped FLARE needs a real `IRetriever` over the harness's store. The pipeline-parity work (PR #414,
**open at the time of writing**) demonstrates that a real `AddRagNet` pipeline over the harness's own
store returns byte-identical results to the harness's dense row, and that pipeline exposes
`IRetriever` — so lookahead can retrieve from exactly the corpus the arm is measured on, with the
equivalence tested rather than assumed.

**If #414 does not merge, `flare` needs its own adapter and that equivalence reverts to an
assumption.** `flarefixed`, `mapreduce`, `refine` and `chatengine` carry no such dependency.

## Cost is opt-in by the harness's existing design

Generation happens only under `RAGNET_GRAPHRAG_ANSWERS_GENERATE` with an API key; a plain run replays
from cache refusing on miss. A pilot that would spend money cannot start by accident. This is kept
as-is rather than reworked.

## One concurrency question, checked rather than assumed

`MapReduceAnswerEngine.MapOneAsync` runs its per-source calls under a `SemaphoreSlim`, so **map
calls are concurrent** — while every other arm in this harness calls the answering client
sequentially.

`CachedGraphRagClient`'s counters are already `Interlocked` throughout (`Calls`, `Retries`,
`InputTokens`, `OutputTokens`, `LongestPrompt` via `CompareExchange`), so it was built
concurrency-aware and the pilot's cost counters will not be corrupted by parallel maps. The six map
prompts also differ from one another (different chunks), so concurrent cache *writes* land in
different files rather than contending for one.

**What remains to confirm during implementation** is the cache read/write path itself under
concurrency — the counters being safe does not prove the file I/O is. The plan carries a step for
it. If it turns out unsafe, the fix is to bound `MapReduceOptions`' concurrency to 1 for the arm,
which changes wall-clock but not the call count and therefore not the figure.

## The risk nobody had named

**The scoring rule may punish format rather than reasoning, and that would look exactly like a
finding.** The inline prompt is tuned terse — it instructs *"answer exactly: Insufficient
information"* — and the dataset authors' rule scores against short gold answers. MapReduce's reduce
step, Refine's iterative rewrite and FLARE's sentence-by-sentence assembly each have their own output
style. A large negative for an engine could be verbosity rather than worse reasoning.

`chatengine` bounds this only partially: it isolates *one* prompt change, not each engine's own
style. The mitigation is procedural and cheap — **the pilot reads answers, not just scores.** The
harness already emits `DumpAnswers` for every scored answer, and the protocol requires eyeballing a
sample per arm before any number is believed. This is the same class of error as 5.2's
misattribution, and it is caught by looking at the artifact rather than the aggregate.

## Out of scope

- **Fixing 5.2.2's deviation** (routing generation through the tool) — it would rekey the cache.
- **Re-baselining the existing arms.**
- **The full 2,556-query sweep** — scheduled separately, priced from the pilot's counters.
- **Improving any engine.** Milestone 6's bar is *measured*, not good; a feature measured and found
  wanting is a completion.

## Definition of done

- Five arms build and register; fast-tier tests pin each one's call shape (1 / 6 / 7 /
  ≤30-with-zero-retrievals).
- `flarefixed`'s retriever stub throws if reached.
- All three pilot gates implemented: context identity against `dense`, call counts matching shape,
  and **lookahead observed firing** in `flare`.
- The pilot emits calls-per-query and tokens-per-query per arm, so the sweep is priced from
  measurement.
- No existing cache key changes; the inline prompt constant is untouched; the three pinned answer
  figures stay reproducible.
- ROADMAP records the thread **without completing Phase 6.2.1**, and states plainly that no pilot has
  run on the machine that built this.
