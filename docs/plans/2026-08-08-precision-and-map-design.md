# Precision@k and MAP — Design (Phase 5.4)

**Date:** 2026-08-08
**Milestone:** 5 — Evaluation Depth
**Status:** approved (design)

## 0. What this is

`IrMetrics`' public surface is exactly `NormalizedDiscountedCumulativeGain`, `Recall`,
`ReciprocalRank` and `Evaluate` — verified against the source, not taken from the roadmap. This
phase adds the two IR metrics it lacks: **Precision@k** and **MAP**.

Small by volume — two methods and two fields on `IrEvaluation`. The roadmap's own note says where
the difficulty is: *"MAP's judged-query exclusion rule must match `Evaluate`'s, which is where the
one subtlety lives."*

It is scheduled ahead of 5.2 and 5.3 because both will want to compare against published figures
stated in these metrics, and because a metric added under deadline pressure to match a baseline is
a metric that gets bent to match it.

## 1. Three decisions that change the numbers

`IrMetrics` already documents the traps that produce plausible wrong answers — the IDCG cap, the
judged-query mean, `2^rel − 1`. Precision and AP add three more, so they are decided here and
written into the XML docs rather than left implicit.

### 1.1 Precision@k divides by `k`, not by what was retrieved

`relevant_in_top_k / k`. A query returning 3 documents at `k = 10` scores at most **0.3**.

Dividing by `min(k, retrieved)` would let that same query score **1.0**, which reads as perfect
precision from a run that returned almost nothing. Published baselines mean `/k`, and a metric
whose denominator depends on how much the system happened to return cannot be compared across
systems — which is the entire purpose of the number.

### 1.2 Average Precision divides by `min(k, |relevant|)`

`AP@k = (Σ over ranks i ≤ k of P(i)·rel(i)) / min(k, |relevant|)`.

Dividing by `|relevant|` is TREC's unbounded `map`, and against a query with 20 relevant documents
evaluated at `k = 10` it caps AP at **0.5** — a metric that cannot reach 1.0 at its own cutoff even
from a perfect ranking. Every other metric in this class is explicitly `@k`; a ceiling that moves
with a query's judgement count is the same shape of silent scaling as the SciFact note this file
already carries.

**Stated because it matters when comparing:** a published `MAP` figure computed the TREC way is
**not** comparable to this one on a dataset with more than `k` relevant documents per query. A
future phase quoting an external MAP baseline must check which the source used.

### 1.3 Relevance is binary at `rel > 0`

nDCG uses `2^rel − 1`; precision and AP are binary metrics and need a threshold.

`rel > 0` — identical to `CountRelevant` and to the existing rule that *"grades of zero and
documents absent from the map are equally irrelevant"*. No new threshold is invented, so a graded
dataset cannot mean one thing to nDCG and another to precision.

## 2. The exclusion rule is inherited, not re-implemented

`Evaluate` skips queries whose judgements contain nothing positive, counts the rest in
`evaluated`, and lets a **judged query that retrieved nothing contribute a real zero** rather than
vanishing.

Both new metrics go through that same loop and that same divisor. **Neither may introduce its own
skip.** A metric that quietly averaged over a different denominator than its siblings would make
the four numbers in one `IrEvaluation` incomparable with each other while all looking fine — the
failure this file's existing documentation exists to prevent.

## 3. `IrEvaluation` gains two fields

A positional record, constructed in exactly one place (`IrMetrics.Evaluate`), so the change is
contained. Consumers use it for reporting strings in the BEIR harness tests.

`Cutoff`, `EvaluatedQueryCount` and `ExcludedQueryCount` are appended-to rather than reordered, so
existing positional reads keep their meaning.

## 4. Testing

`IrMetricsTests` pins **hand-computed** values — that is the file's convention and it is kept.
A metric verified only against its own implementation verifies nothing.

Each decision in §1 gets a test that would fail under the alternative:

- a short run at a larger `k`, which separates `/k` from `/min(k, retrieved)`
- a query with more relevant documents than `k`, which separates `min(k, |relevant|)` from
  `|relevant|`
- a graded qrels row including a `0`, which pins the binary threshold
- a judged query with an empty run, which pins the inherited zero rather than an exclusion

**Every expected value is computed by hand in the test's comment**, so a reader can check the
arithmetic without running anything.

## 5. Out of scope

- **Scoring a real dataset with these metrics.** Milestone 5's DoD has its own criterion for
  graded gain against real qrels; this phase adds the metrics, it does not run a corpus.
- **The FiQA-qrels contradiction** recorded against `IrMetrics.cs:31-32` — a separate open item,
  and settling it needs the cached qrels file, not new metrics.
- **Reporting the new fields in the BEIR harness output strings.** They become available; whichever
  phase first needs to publish them decides how they are presented.
