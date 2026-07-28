# A/B Testing Framework — Design (Phase 3.3)

**Date:** 2026-07-28
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.3
**Covers:** the `A/B Testing Framework` row in `features.md`

## Genuinely greenfield, and it depends on the two phases before it

Verified rather than assumed: `AbTester`, `ShadowMode`, `SideBySide` and their variants return
**zero matches across every `.cs` file in the repository**, and the full listing of
`Rag.NET.Evaluation` and `Rag.NET.Evaluation.Ragas` contains nothing A/B related. Unlike Phases
3.1 and 3.2, there is no shipped-but-unverified code here.

It could not have been built before now. Comparing two pipeline configurations requires a
**reproducible dataset** — Phase 3.2, where sampling was unseeded until yesterday — and **metrics
that do not fabricate scores** — Phase 3.1, where a malformed model reply scored 1.0. Built on
either of those, an A/B result would have been noise wearing a number.

## 1. What the spec asks for, and where it does not fit

`features.md` specifies a `RagAbTester` with **shadow mode** (primary returned to the caller,
secondary run out-of-band) and **side-by-side mode**, "integrat[ing] with `IRagEvaluator` to score
both results automatically".

Shadow mode runs against production traffic, which has **no ground-truth answer**. Two of the four
RAGAS metrics — Context Precision and Context Recall — throw on an empty `ReferenceAnswer`. So
"score both automatically" in a shadow context can only mean the reference-free half. Shadow mode
also implies doubled LLM spend on every request, fire-and-forget work that is lost on host
shutdown, and a secondary that must never break a primary the caller already received.

**Scope decision:** build the offline harness first. It has none of those problems, can use all
four metrics because an evaluation dataset carries reference answers, and is the engine Phase 3.7
needs for its ablation table. Shadow mode is **recorded and scheduled**, not silently dropped.

## 2. Scope decisions (agreed)

1. **Offline harness first**; shadow mode deferred to its own phase.
2. **A variant is an `IRagPipeline` plus optional `RagOptions`** — so a comparison can span
   chunking, vector store, embedding model and reranker, not merely per-call settings.
3. **Sequential execution with alternating order.**
4. **Paired per-sample deltas with a bootstrap confidence interval.**

## 3. Split the statistics from the orchestration

One class doing execution, evaluation, statistics and reporting would put the subtlest arithmetic
in the phase behind a live pipeline and an LLM. The same split that worked for `RagasMath`
(Phase 3.1) and `ReservoirSampler` (Phase 3.2) applies:

- **`AbStatistics`** — internal static. Paired deltas, mean, win/loss/tie, bootstrap CI. Pure
  functions over `double[]`, table-tested with no pipeline and no model.
- **`AbRunner`** — internal. Executes both variants in alternating order, times them, returns raw
  per-sample responses. Performs no scoring.
- **`RagAbTester`** — public. Composes the runner, the existing evaluators and the statistics.

## 4. Fairness: alternating order

Whichever variant runs second benefits from provider-side prompt caching and a warm vector store.
A fixed order therefore hands one variant a systematic advantage and reports it as a result.

Execution alternates which variant leads, per sample. Concurrent execution was rejected: it
roughly halves wall-clock, but the two variants then contend for the same provider and connection
pool, so the latency numbers measure contention as much as they measure the variants — and latency
is half the reason to run this at all.

This matters least for quality scores and most for latency. Both are reported, so the ordering is
chosen for the measurement that is sensitive to it.

## 5. A failed variant excludes the pair, not the side

`IRagPipeline.AskAsync` throws rather than returning a `Result` (unlike `RetrieveAsync`). A
variant that fails on one sample must not abort the comparison, so the failure is caught and
recorded.

**The sample is then excluded from the paired statistics entirely — both sides — and counted.**
Dropping only the failing side would compute the two means over *different sample sets* while
still describing the result as paired. That is the same defect this milestone has found three
times already: a number that looks comparable and is not.

The same rule covers RAGAS returning `double?` for an unscoreable sample (Phase 3.1): a `null` on
either side means that pair cannot be compared **for that metric**, so it is excluded and counted
per metric rather than per sample.

## 6. Paired statistics, and why bootstrap

Both variants see identical samples, so the comparison is paired: `delta_i = B_i − A_i`. Pairing
removes between-sample variance, which is the dominant term — some questions are simply harder —
and is what makes a 50-sample comparison worth anything at all.

Reported per metric: mean delta, win/loss/tie counts, and a **95% bootstrap confidence interval on
the mean delta**.

Bootstrap rather than a t-test because RAGAS scores are bounded on [0, 1] and frequently skewed —
a metric where most samples score 0.9+ is nowhere near normal, and a t-interval would overstate
its own precision. The bootstrap assumes only that the samples are exchangeable.

**The bootstrap takes a seed.** An unreproducible confidence interval is not evidence, and Phase
3.2 established the same rule for sampling. Same seed, same deltas, same interval.

**What the CI is for:** distinguishing a real difference from sampling noise. A mean delta of
`+0.07` over 50 samples with a CI of `[+0.02, +0.12]` is a finding; the same delta with
`[−0.04, +0.18]` is not. Without it, every run produces a winner.

## 7. Latency and cost

**Latency** is measured by the runner — wall-clock per variant per sample — because `RagResponse`
carries only `Answer` and `Sources`. Reported as p50, p95 and the mean delta with the same paired
CI machinery.

**Cost needs one ledger per variant.** `ICostLedger` aggregates into a daily bucket with no
per-caller attribution, so a shared ledger cannot say which variant spent what. Each variant gets
its own instance and they are reported separately. Rag.NET does not price tokens itself, so the
caller supplies the price sheet as everywhere else.

## 8. Testing

- **`AbStatistics`** — table tests over known delta arrays. A seeded bootstrap makes the interval
  deterministic, so it can be asserted exactly rather than within a tolerance. This is where the
  statistics are actually pinned.
- **`AbRunner`** — a fake `IRagPipeline` recording call order. Assert that alternation **actually
  alternates** (a runner that ignores the rule and always leads with A must fail), that per-variant
  timings are recorded, and that a throwing variant neither aborts the run nor leaves the sample
  half-paired.
- **`RagAbTester`** — end to end over fake pipelines and the existing `RoutingChatClient`, covering
  the exclusion rules from §5.

## 9. Documentation

`docs/guide/evaluation.md` gains an A/B section: what a variant is, why execution alternates, how
to read the CI and — explicitly — that a CI spanning zero is not a win. State that shadow mode is
not in this phase and where it is scheduled, so the `features.md` promise is not left half-met
without explanation.

`features.md`: tick the matrix row, and correct the Status prose to say what shipped rather than
what was specified.

## Out of scope

- **Shadow mode** — recorded and scheduled, not dropped. It is a production-path concern with its
  own failure modes (doubled spend, fire-and-forget loss on shutdown, reference-free metrics only)
  and deserves its own design rather than being bolted on here.
- **More than two variants.** Pairwise is what the statistics above describe; N-way comparison
  needs a multiple-comparisons correction, which is a different conversation.
- **Power analysis** — telling a caller how many samples they need before running. The CI reports
  what the run achieved, which is the honest half of that question.
