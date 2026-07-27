# RAGAS Metrics — Verify, Test, Document (Phase 3.1) — Design

**Date:** 2026-07-27
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.1
**Covers:** the `RAGAS-Style Metrics` row in `features.md`

## The feature shipped; the work did not

`src/Rag.NET.Evaluation.Ragas` landed on 2026-04-11 with four metrics, a suite, a report and a
builder — and with **no tests and no documentation**. `features.md` recorded this contradiction
faithfully without anyone noticing: the detail section reads `**Status:** ✅ Done` while the
summary-matrix row at `:1054` reads `[ ]`. The matrix row was the honest one.

That matters more than an ordinary coverage gap. An evaluator that is wrong does not throw. It
returns a plausible number, and a plausible number is indistinguishable from a correct one
without a test that pins the definition. Every score this package has produced is unverified.

The audit below is what a read of the code found. It is not a list of hypotheticals.

## 1. What the audit found

### Definitional — these are not the RAGAS metrics

**Context Precision ignores rank.** Published Context Precision is rank-aware average precision:

```
CP@K = Σ(Precision@k × rel_k) / total_relevant
```

`ContextPrecisionEvaluator.cs:31` computes `relevant / total`. A retriever that returns the gold
chunk **first** scores identically to one that returns it **last** — precisely the discrimination
the metric exists to provide. It is a different, simpler metric wearing the RAGAS name.

**Answer Relevance never penalises evasion.** Published Answer Relevance classifies noncommittal
answers ("I don't know", "the context does not say") and scores them zero. There is no such
check, so an evasive answer that is topically close to the question scores high.

**Answer Relevance generates n identical questions.** `AnswerRelevanceEvaluator.cs:26-27` issues
the same prompt `n` times concurrently and averages the results. Nothing makes the questions
differ; at temperature 0 all `n` come back identical and the mean collapses to a single sample.
The metric silently degrades to `n = 1` while reporting as if it averaged three.

**Cosine similarity is not clamped.** `TensorPrimitives.CosineSimilarity` ranges over [-1, 1].
The XML doc claims 0–1. A negative similarity propagates into the mean and then into
`OverallScore`.

### A parse failure scores 1.0

`FaithfulnessEvaluator.cs:45-52` and `ContextRecallEvaluator.cs:50-51` are the same code twice:

```csharp
try { return JsonSerializer.Deserialize(raw, ...) ?? []; }
catch (JsonException) { return []; }
```

`[]` then meets `if (claims.Count == 0) return 1.0;`. **A model that returns malformed JSON
yields the best possible score**, indistinguishable from a genuinely perfect answer. This is the
worst defect in the package: it fails upward, silently, on the metric users most rely on.

### Robustness

- **`StartsWith("yes")`** in three evaluators. `"Yes, but only partially"` counts as full support;
  `"The claim is supported"` counts as unsupported.
- **Unbounded concurrency.** Every evaluator fans out one LLM call per claim, chunk or statement
  through `Task.WhenAll` with no ceiling. A 50-chunk sample is 50 simultaneous requests, and a
  suite with four metrics registered multiplies that again.
- **Empty `SourceChunks` returns 0.0**, conflating "nothing was retrieved" with "everything
  retrieved was wrong".
- **No cost recording**, though `ICostLedger` gained the shape for this in Phase 2.4 and these
  metrics are pure LLM spend.

### API honesty

- **`features.md` claims each metric is "a standalone `IRagEvaluator<T>` so they can be composed
  into a `RagasEvaluationSuite`".** Both halves are wrong. They implement `IRagasMetric`, which is
  unrelated to `IRagEvaluator`; and they cannot be composed by a caller, because
  `RagasEvaluationSuite`'s constructor is `internal` and the builder exposes only four fixed
  `Add*` methods. The evaluator classes *are* public with public `ScoreAsync`, so they can be
  constructed and called standalone — that part works, and is what the docs should say instead.
- **No per-sample results.** `RagasReport` carries four aggregate means. A score of 0.62 gives no
  way to find which sample caused it — while the sibling `LlmJudgeResult` already exposes
  per-sample detail.

## 2. Scope decisions (agreed)

1. **Fix the metrics to match published RAGAS.** Scores change; the name then means what a
   reader expects.
2. **Per-sample results**, matching the `LlmJudgeResult` precedent.
3. **Bounded concurrency.**
4. **Cost-ledger integration.**

## 3. Split the plumbing from the arithmetic

The four evaluators duplicate JSON-array extraction, yes/no classification and unbounded fan-out.
Fixing each in place would mean fixing the same bug four times, which is how the two copies of
the parse defect came to exist.

An internal **`RagasJudge`** owns prompting, parsing, throttling and cost. The evaluators become
arithmetic over judgement arrays.

The payoff is that the part that is definitionally wrong — the formulas — becomes testable with
**no LLM at all**. `ComputeAveragePrecision([gold, junk, junk]) == 1.00` and
`ComputeAveragePrecision([junk, junk, gold]) == 0.33` are table tests, not mock choreography.

## 4. Never fabricate a score

The rule that fixes the worst defect: **a verdict the model did not give is not a verdict.**

`RagasJudge` returns a tri-state verdict — `Yes`, `No`, `Unparseable` — instead of a `bool` that
silently means "no" on failure. Scores are computed over parseable verdicts only. Where a sample
yields none, its score for that metric is `null` and the sample is **excluded from the mean**
rather than counted as zero or one.

`RagasReport` carries the excluded count per metric, so a degraded run is visible rather than
quietly averaged. An empty claim list still scores 1.0 — nothing unfaithful was asserted — but
that is now distinguishable from a parse failure, which it was not before.

## 5. Metric fixes

| Metric | Change |
|---|---|
| Context Precision | Rank-aware average precision over the retrieved order |
| Answer Relevance | Noncommittal classifier (evasive → 0); one call returning `n` **distinct** questions; cosine clamped to [0, 1] |
| Faithfulness | Tri-state verdicts; no fabricated 1.0 |
| Context Recall | Tri-state verdicts; no fabricated 1.0 |

Empty `SourceChunks` becomes `null` (not scoreable) rather than 0.0, for the same reason as a
parse failure: absence of data is not evidence of bad retrieval.

## 6. Concurrency and cost

A `SemaphoreSlim` inside the judge, **shared across the whole suite run** — not per evaluator, or
four registered metrics multiply the fan-out fourfold again. The ceiling is configurable and
defaults conservatively.

`ICostLedger?` is optional and resolved by the judge, so the evaluators stay unaware of billing.
Chat and embedding calls are both recorded. As in Phase 2.4, this means evaluation spend now
counts toward the same budget window `UseCostBudgeting` enforces — correct, but a change users
will notice.

## 7. API changes

`RagasReport` gains `Samples` and the per-metric excluded counts.

**Correction to an earlier draft of this design:** it proposed making `IRagasMetric` internal.
It already is (`IRagasMetric.cs:5`), so there is nothing to change. The four evaluator classes
are public and implement it with public members, which means they are directly constructible and
callable standalone; only *suite composition* is closed. That is a defensible shape and stays as
it is. Custom metric registration remains a non-goal, and the docs describe what is actually
usable rather than implying more.

## 8. Testing

- **Pure arithmetic** — exhaustive table tests over judgement arrays, no LLM, no mocks. This is
  where metric fidelity is actually pinned.
- **`RagasJudge`** — a hand-written prompt-routing `IChatClient` fake that returns different
  responses per prompt and records the call sequence. It must assert the concurrency ceiling
  genuinely holds (peak observed concurrency, not just a total count) and that cost is recorded.
- **Each evaluator** — end to end over the routing fake, including the tri-state and
  noncommittal paths.
- **The suite** — aggregation, null exclusion, and that the shared semaphore is not per-metric.

`LlmJudgeEvaluatorTests` uses NSubstitute with one canned response, which suffices there because
that evaluator makes a single call. It does not suffice here: RAGAS evaluators make sequenced,
prompt-dependent calls. Existing tests stay as they are.

## 9. Documentation

`docs/guide/evaluation.md` gains a RAGAS section: what each metric measures, its formula, which
require a `ReferenceAnswer`, the noncommittal rule, the cost profile and throttle, per-sample
output, and how to read a `null` score.

`features.md` gets the matrix row at `:1054` ticked, the `IRagEvaluator<T>` claim corrected, and
the Status prose brought in line.

**Scores change.** Nothing is published — NuGet packaging is Phase 4.1 — so this follows the
posture already set twice in this repo: source-breaking is acceptable while not public. It is
documented in the guide, not recorded as a release break.

## Out of scope

- Custom metric registration (§7) — a deliberate non-goal, not a deferral.
- The remaining published RAGAS metrics beyond the four here.
- `EvaluationDatasetBuilder` — the same verify/test/document treatment, but that is Phase 3.2.
- Reconciling `IRagasMetric` with `IRagEvaluator` into one evaluation abstraction.
