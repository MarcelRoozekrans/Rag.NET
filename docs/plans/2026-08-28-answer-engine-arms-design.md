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

**Worst case ≈ 189,000 calls — roughly 34× the RAPTOR full sweep's ~5,600 answers.**

**Calls are not the cost, though, and reading them as cost is misleading.** Two facts flatten the
money:

- **`ChunkingOptions.MaxChunkSize` is 512 *characters*, not tokens** (its doc comment says so), so a
  chunk is ~128 tokens and top-6 context is **~770 tokens** — not the multi-thousand-token context a
  call count invites you to assume.
- **MapReduce and Refine send one chunk per call, not six.** Their token totals land close to a
  single-shot answer despite 6–7× the calls.

At `gpt-4o-mini` rates ($0.15/M input, $0.60/M output):

| Arm | Input tokens/query | Output | Cost/query | Over 2,556 |
| --- | --- | --- | --- | --- |
| `chatengine` | ~850 | ~30 | $0.00015 | ~$0.40 |
| `refine` | ~1,750 | ~240 | $0.00041 | ~$1.05 |
| `mapreduce` | ~1,950 | ~390 | $0.00053 | ~$1.35 |
| `flarefixed` | ~1,350 → 20,250 | ~40 → 600 | $0.0002 → $0.0034 | ~$0.60 → $8.70 |
| `flare` | as above, plus growing context | | | ~$0.60 → $9+ |

**Realistically ~$4; worst case ~$21. The 50-query pilot is 6–40 cents.**

**The whole range is FLARE's sentence count.** `MaxSentences = 15` is a ceiling, not a prediction,
and each sentence costs a generation call carrying the full context plus a scorer call.
MultiHop-RAG's gold answers are a few words, so FLARE plausibly stops after one to three sentences
and lands at the low end — but if it runs to fifteen, FLARE alone is 80% of the bill. That single
unknown swings the total roughly tenfold.

**These figures are derived, not measured**, from chunk size and prompt structure. The project's one
real anchor — ~$9 for 24.3M tokens of extraction and community reports — implies a blended $0.37/M,
but that workload is output-heavy while these arms are input-heavy with tiny answers, so the blended
rate here should be lower. OpenRouter's exact `gpt-4o-mini` rate card was not checked against the
standard $0.15/$0.60 used above.

**So money was never the reason for the gate.** At these numbers the sweep is affordable on any
reading. The gate exists for correctness reasons — that lookahead can silently not fire, and that a
mis-wired arm produces numbers meaning nothing.

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
taken from tree summarisation, whose prompts are far larger than answer generation's. It also
supersedes the dollar table above, which is derived rather than measured and exists only to show the
order of magnitude. In particular the pilot settles **FLARE's sentence count**, the one unknown that
moves the total tenfold.

### And the pilot publishes no accuracy headline

Fifty queries with this dataset's skewed type mix put RAPTOR's corpus-versus-per-document difference
at exactly +0.0000 when the true value was −0.0146 at p=0.0247. Any accuracy number the pilot
produces goes into the notes as *"underpowered, not a result"*.

## Components

| File | Change |
| --- | --- |
| `AnswerArm.cs` | five new arm constants, added to `All` |
| `AnswerEngineArms.cs` *(new)* | builds each engine over the shared `CachedGraphRagClient`; owns the recording stub retriever and the failure counter the engines log into |
| `AnswerEngineArmsTests.cs` *(new)* | fast-tier call-shape assertions |
| `BeirGraphRagAnswerTests.cs` | engine arms reuse the `Dense` retrieval case; a generation switch; the three gates |

### What can be verified on an unprovisioned machine

**The cost model becomes a fast-tier test rather than a hope.** A counting fake `IChatClient` over six
synthetic sources asserts each engine's call shape with no corpus, no model and no spend:
`chatengine` exactly 1, `refine` 6, `mapreduce` 7, `flarefixed` at least 1 with **zero** retrievals.
The number that decides whether a ~189,000-call sweep is affordable is therefore checked before
anyone provisions anything. (FLARE's own count is left unbounded on purpose — see the Definition of
done for why `≤30` would pin the fake's canned answer rather than the arm's claim.)

**`flarefixed`'s zero-retrieval claim is asserted by a recording stub.** Its `IRetriever` records
that it was called, and the test asserts it was not.

**An earlier draft of this design said the stub should simply *throw*, and that was wrong.**
`FlareAnswerEngine.TryLookaheadRetrievalAsync` wraps the retriever call in
`catch (Exception ex) { …log…; return null; }`, so a thrown exception is **swallowed and logged**.
Had a future change broken the `retrievalsUsed < MaxRetrievals` guard, the throw would have vanished
into that catch, the engine would still have returned an answer, and the test would have passed
silently while lookahead fired. The guarantee read well and did not hold — which is the exact defect
class this thread exists to catch, found in the design that proposed the catcher.

The stub still throws, because that is correct anywhere the exception is not swallowed, but the
durable check is the recorded flag. A counter reading zero, a code path that cannot execute, and an
exception that is caught and discarded are three different things.

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

**The cache read/write path itself under concurrency** — the counters being safe does not prove the
file I/O is — was left open here as something to confirm during implementation. It is closed now, by
reading rather than by measurement, and the conclusion is recorded here rather than left as a
promise nobody kept: no step in the implementation plan ever carried it.

Read at `src/Rag.NET.Benchmarks.Quality/GraphExtractionCache.cs`, the path is **read, generate,
write, with no per-key lock**: `GetOrAddAsync` calls `TryRead`, and on a miss generates and then
writes, with nothing serialising two callers on one key. What that costs, and what it does not:

- **The file I/O is safe on its own.** `WriteAsync` writes to `path + "." + Guid + ".partial"` and
  then `PublishRename.ReplaceFileAsync`s it into place, so each writer has its own scratch file and
  publication is a single atomic replace. Two writers on one key cannot tear a file or interleave
  bytes; one replace simply wins, and both wrote the same generated text anyway.
- **What the missing lock actually costs is a duplicate generation.** Two concurrent misses on the
  same key both call the model — the arm pays twice for one cache entry. That is a billing and
  wall-clock cost, not a correctness one.
- **`TryRead` treats an `IOException` as a miss**, so a read landing mid-replace returns `null`. On
  a fill run that is one extra generation; on a replay it would be a spurious `MissingEntry`. It
  cannot arise here: a replay writes nothing, so there is no replace to collide with.

None of that bites this arm, for one reason: **the six map prompts differ from one another**,
because each embeds a different chunk. Six concurrent map calls are six distinct keys and six
distinct files, so there is no same-key concurrency at all. The reduce call happens strictly after
`Task.WhenAll` and does not overlap them either.

That is a property of the workload, not of the cache. If a future change ever gives two concurrent
map calls the same prompt — identical chunks in one context, or a map prompt that stops embedding
the chunk — the duplicate-generation cost returns, and the fix is the one this section already
named: bound `MapReduceOptions.MapConcurrency` to 1 for the arm, which changes wall-clock but not
the call count and therefore not the figure.

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

- Five arms build and register; fast-tier tests pin each one's call shape: `chatengine` exactly 1,
  `refine` exactly 6, `mapreduce` exactly 7, and `flarefixed` **at least 1, with zero retrievals**.
  - *As built, and deliberately looser than the `≤30` this document first promised.* FLARE's call
    count is `2 × sentences` and the sentence count is hostage to prompt wording — the fake client's
    fixed reply never emits the done-token, so the loop runs to `FlareOptions.MaxSentences` and the
    observed figure is 30 only because that default is 15. Pinning `≤30` would pin a default of the
    library under test and a property of the fake's canned answer, neither of which is this arm's
    claim; it would fail on a `MaxSentences` change that costs nothing and pass on a lookahead
    regression that costs everything. The number that matters is the second half of the pair, and it
    is asserted exactly: **zero retrievals**. The observed 30 is recorded in the test's remarks as an
    upper-bound signal rather than asserted as a bound.
- `flarefixed`'s retriever stub **records that it was called, and the run asserts the flag is
  clear** — in the fast-tier test and again in the harness after the last answer.
  - *The stub still throws, but the throw is not the guarantee.*
    `FlareAnswerEngine.TryLookaheadRetrievalAsync` swallows every exception the retriever raises, so
    a test watching for the throw would pass while lookahead fired. `WasCalled` is set before the
    throw and survives the swallow. The harness installs one stub instance for the run and reads its
    flag; the factory refuses to build the arm without one, so the flag can never be on an object
    nobody holds.
- Every engine is built with a **counting logger, never `NullLogger`**, and the run asserts that
  **no exception was swallowed**. `MapReduceAnswerEngine`, `RefineAnswerEngine` and
  `SelfAssessmentConfidenceScorer` each answer through a catch-all that logs and continues, so a
  missing cache entry on a replay would otherwise degrade an arm's answer silently — and the
  call-shape gate cannot see it, since its counter increments before the request is forwarded.
  - *Exceptions fail the run; warnings without one are printed, not asserted.*
    `ConfidenceScoreUnparsable` (the model's self-assessment did not parse) and
    `FlareLookaheadFailed` (retrieval returned an error result) are output, not faults, and failing
    on them would be self-perpetuating — the unparsable reply is itself cached, so every replay
    would reproduce it and `flare` could never run again without a code change. Both counts appear
    in the cost block, where a total far above the exception count is the signal that the scorer is
    failing open; Gate 3 backs that up by asserting the lookahead fired at all.
- All three pilot gates implemented: context identity against `dense`, call counts matching shape,
  and **lookahead observed firing** in `flare`.
- The pilot emits calls-per-query and tokens-per-query per arm, so the sweep is priced from
  measurement.
- No existing cache key changes; the inline prompt constant is untouched; the three pinned answer
  figures stay reproducible.
- ROADMAP records the thread **without completing Phase 6.2.1**, and states plainly that no pilot has
  run on the machine that built this.
