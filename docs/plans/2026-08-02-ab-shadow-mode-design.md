# A/B Shadow Mode — Design (Phase 3.8)

**Date:** 2026-08-02
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.8
**Completes:** the `A/B Testing Framework` feature row, whose offline half shipped in Phase 3.3

Phase 3.3 built `RagAbTester.CompareAsync` — two variants, a fixed question set, four RAGAS
metrics, per-variant spend. It is an offline harness. This phase adds the production half, and it
was scoped out deliberately because the production path has failure modes the harness does not.

## 0. The decision that shapes everything: capture, do not score

**Shadow mode runs the secondary pipeline and persists the pair. It scores nothing.** Scoring
happens offline, later, with the harness Phase 3.3 already built.

The alternative — judging on the request path — was rejected on measured grounds. All four RAGAS
metrics are LLM-judged (`AnswerRelevanceEvaluator`, `FaithfulnessEvaluator`,
`ContextPrecisionEvaluator`, `ContextRecallEvaluator` all take an `IChatClient`), and Faithfulness
decomposes an answer into claims and verifies each, so one "metric" is several calls. Inline
scoring costs:

```
primary pipeline          1× LLM
secondary pipeline        1× LLM
Faithfulness judge      1..n LLM
Answer Relevance judge  1..n LLM
                       = 4–6× a normal request
```

Capture costs **2×**, puts no judge on the request path, and cannot fail in a way that touches
production.

It also disposes of the roadmap's first named failure mode rather than merely documenting it.
**Production traffic has no reference answer**, so Context Precision and Context Recall — which
throw on an empty `ReferenceAnswer` — can never run inline. Under capture that stops being a
permanent limitation: the pair sits in storage, and if a reference answer is added later, **all
four metrics become available offline**. Inline scoring forecloses that forever.

## 1. The isolation contract

The roadmap's sharpest constraint: `IRagPipeline.AskAsync` returns `Task<RagResponse>` and
**throws** rather than returning a `Result`, so a secondary failure surfacing on the primary's
task would break a request the caller had already been served.

**The secondary is made structurally incapable of affecting the primary, not merely wrapped in a
`try`.** Three properties, in order:

1. **The primary's response is returned before the shadow is scheduled.** Not concurrently —
   before. The shadow observes a completed primary.
2. **Scheduling is non-blocking and cannot throw into the caller.** Enqueue to a bounded channel;
   a full channel drops (§3) rather than blocking the request thread or propagating.
3. **The shadow runs on a background consumer**, where an exception is caught, counted, and
   recorded as a `VariantFailure` — the type Phase 3.3 already defines for exactly this.

**A `try/catch` around an awaited secondary is not sufficient** and is worth naming as the wrong
answer: it still couples the primary's latency to the secondary's, so a slow secondary degrades
the very requests it is supposed to be invisible to.

## 2. Off by default, and the multiplier stated where the switch is

**Sampling defaults to zero.** Registering shadow mode does nothing until a rate is set:

```csharp
services.AddRagNet(rag => rag
    .UseShadow<SecondaryPipeline>(o => o.SampleRate = 0.05));
```

Nobody discovers doubled spend by upgrading a package. The secondary costs real money on someone
else's bill, so turning it on is a deliberate act with a number attached, and **the 2× multiplier
is documented next to the setting rather than in a page nobody reads**.

**Sampling does not break the comparison.** The unit is a request, and a sampled request runs
*both* pipelines over the *same* question — so the captured pairs stay paired. A 5% sample is 5%
of traffic compared properly, not a 5% sample of each side compared across different questions.

**Per-variant spend is already solved.** `CostTrackingChatClient` and `CostTrackingEmbeddingGenerator`
exist and `RagAbTester.SpendAsync` already reports per-variant cost; the shadow records both
variants' spend into the captured pair, so the doubled cost is visible per request rather than as
an unexplained rise on a bill. [Corrected during implementation: the shadow records only the
**secondary's** spend. The primary serves concurrent production traffic on a shared ledger, so no
honest per-request primary figure exists with `ICostLedger`'s read surface — the consumer measures
the secondary alone, around its one-at-a-time run, against the dedicated
`ShadowCaptureConsumerOptions.SecondaryCostLedger`. The primary side's `Spend` stays absent,
deliberately; `docs/guide/shadow-mode.md` ("Primary spend is not measured, deliberately") documents
the shipped behaviour.]

## 3. Loss is counted, never silent

Two ways captured work disappears, and **both are counted and surfaced**:

**Backpressure.** A bounded channel that is full drops the sample rather than blocking the request.
Blocking would let a slow secondary throttle production, which is the failure this design exists to
prevent. **A dropped sample is data loss and is counted as such** — a silent drop would make the
capture rate quietly lower than the configured sample rate, and every offline comparison would then
rest on a denominator nobody could reconstruct.

**Shutdown.** The roadmap names fire-and-forget loss explicitly: background work is abandoned when
the host stops, and a naive implementation loses it silently. An `IHostedService` **drains the
channel on `StopAsync` within a bounded timeout, then reports how many items were still queued.**
Draining without a timeout would hang shutdown; draining without reporting would be the same silent
loss in slower clothing.

## 4. What is captured

Enough for `RagAbTester` to run offline, and no more: the question, both answers, both retrieved
context sets, both spends, both latencies, the variant labels, and a timestamp. Failures are
captured too — a secondary that threw is a result, and dropping it would bias the comparison toward
whatever the secondary handles well.

**Storage is an abstraction with an in-memory default.** Persisting production traffic is an
application decision — file, table, blob, queue — and this phase ships the seam plus the trivial
implementation, not a storage engine.

## 5. Captured payloads contain production questions verbatim

**This is a data-protection concern the offline harness never had**, and it is the design's least
obvious consequence. Phase 3.3 compared a fixed question set the operator wrote. Shadow mode
persists whatever real users typed, together with retrieved document text.

The library already ships `PiiChunkSanitiser`, `RegexChunkSanitiser` and `LlmPiiChunkSanitiser` in
`Rag.NET.Security`. **Capture runs through a sanitiser seam, and the documentation states plainly
that enabling shadow mode persists user input**, so the decision is taken deliberately rather than
discovered in an audit.

Not designed here: retention, encryption at rest, or subject-access deletion. Those belong to
whoever implements the store, and saying so is more honest than a default that implies more
protection than it provides.

## 6. What this phase does not do

- **It does not score anything.** Offline scoring is `RagAbTester`, which exists.
- **It does not make all four metrics available.** Two are reference-free and work today; the other
  two need a reference answer that production cannot supply. Capture makes them *possible* later —
  it does not supply them.
- **It does not ship a durable store.** In-memory default plus a seam.
- **It does not compare pipelines statistically.** No significance testing, no minimum sample size
  guidance. A user reading two averages off ten captured pairs will draw a bad conclusion, and the
  documentation says so rather than the code pretending to prevent it.

## Out of scope

- **Inline scoring**, including as an opt-in switch — it doubles the surface for a mode capture can
  gain later, once capture has proven itself.
- **Routing a share of traffic to the secondary** (canary release). Shadow means the caller always
  gets the primary; serving the secondary to real users is a different feature with a different
  risk profile.
- **Multi-variant shadowing.** One secondary. `AbVariant` supports two by construction and three
  would be 3× spend.
