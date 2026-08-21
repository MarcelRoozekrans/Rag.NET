---
id: shadow-mode
title: A/B Shadow Mode
sidebar_position: 7
---

# A/B Shadow Mode

Shadow mode is the production half of the [A/B testing framework](evaluation.md#ab-testing). `ShadowRagPipeline` wraps a live `IRagPipeline`: the caller always gets the primary's answer, and a sampled share of `AskAsync` calls also runs a secondary pipeline out-of-band, persisting the pair — question, both answers, both context sets, both latencies, spend, failures — for offline scoring. Nothing is scored on the request path.

```bash
dotnet add package Rag.NET.Evaluation
```

```csharp
using Rag.NET.Evaluation.Shadow;

services.AddRagNet(rag => rag
    /* ...the primary pipeline's configuration... */
    .UseShadow<CandidatePipeline>(o => o.SampleRate = 0.05));
```

`UseShadow<TSecondary>` must be called **after** the pipeline exists — it decorates the last `IRagPipeline` registration, the same ordering rule `AddRagDiagnostics` carries, and is refused at registration time otherwise. `TSecondary` is a whole pipeline, built however the variant under evaluation differs: chunking, vector store, embedding model, reranker. The capture consumer is a hosted service; in a host that never starts hosted services, nothing consumes the queue — captures accumulate to its capacity and then drop, counted.

That rule is about where the `IRagPipeline` registration sits, not about `UseShadow` being the last call in the chain. `UseShadow` hands back an `IRagBuilder` rather than a `RagBuilder` — `Rag.NET.Evaluation` depends on `Rag.NET.Abstractions` alone and needs nothing beyond `Services` — and every satellite package's registration extension is generic over the builder type, so `rag.UseShadow<CandidatePipeline>().UseRaptor(leafStorePath: "raptor-leaves.db")` compiles and registers into the same collection. (`UseRaptor` needs that argument because `RaptorTreeScope` defaults to `Corpus`; the chaining point is the builder type, not the argument list.) What does not follow it is the core builder's own surface beyond `IRagBuilder`: `UseMmr`, `UseSelfQuery`, `UseParentDocumentRetrieval` and the other methods declared on `RagBuilder` itself. Configure those before `UseShadow`.

`AskStreamingAsync` is delegated and **deliberately not shadowed**: a streamed answer completes token by token on the caller's schedule, so there is no completed primary result to pair a secondary against without buffering the caller's stream — which would put shadow work on the request path.

## Every sampled request roughly doubles its spend

The secondary runs the same question through a second, complete pipeline — its own retrieval, its own LLM call — so a `SampleRate` of 0.05 adds about 5% × 2× to the bill, out-of-band but very much real.

**`SampleRate` defaults to `0.0`. Registering shadow mode does nothing until someone deliberately chooses a number.** Nobody should discover a doubled bill by upgrading a package. An out-of-range rate is refused at registration rather than clamped — a clamped rate is a rate nobody chose. Rates 0 and 1 are exact (nothing / everything, no randomness consulted); in between, each request is sampled independently with probability equal to the rate.

Sampling does not break the comparison: the unit is a request, and a sampled request runs both pipelines over the same question, so the captured pairs stay paired.

## It persists user input verbatim

A capture holds the production question **exactly as the user typed it**, plus the retrieved document text of both variants. Whoever persists captures is persisting production data.

- **The sanitiser seam is `IShadowCaptureSanitiser`, and the default applies no sanitisation** — verbatim persistence is the deliberate, documented default, not something to discover in an audit. What scrubbing is appropriate is an application decision, and `Rag.NET.Evaluation` takes no dependency on `Rag.NET.Security`; wrap its `PiiChunkSanitiser`, `RegexChunkSanitiser` or `LlmPiiChunkSanitiser` in an implementation and register it before `UseShadow` — it is picked up automatically. The seam fails safe: a sanitiser that throws or returns null costs exactly that capture — counted and recorded — and nothing can cause an unsanitised capture to be persisted.
- **Retention, encryption at rest and subject-access deletion belong to whoever implements `IShadowCaptureStore`.** The seam provides none of them, and says so rather than implying protection the default does not provide. The default store, `InMemoryShadowCaptureStore`, is for tests and samples: unbounded, unencrypted, gone with the process. Register your own store before `UseShadow` to persist anywhere real.

## The isolation contract

The secondary is structurally incapable of affecting the primary:

1. **The primary's response is returned before anything is scheduled.** The shadow observes a completed primary; it never races one.
2. **Scheduling never blocks and never throws into the caller.** The enqueue completes synchronously, accepted or dropped, and its whole path is caught.
3. **The secondary runs on the background consumer**, where a failure becomes a recorded `ShadowVariantFailure` inside a persisted pair — a result for the comparison, because dropping failed secondaries would bias it toward whatever the secondary handles well.

A primary that throws still throws: shadowing must not swallow genuine failures, and with no served answer there is nothing to pair a secondary against.

## Loss is counted, never silent

The queue is bounded (`ShadowCaptureQueueOptions.Capacity`, default 1000) with a drop-on-full policy: a full queue drops the **incoming** capture — never blocks the enqueuer, never evicts an accepted one — and the drop is counted exactly in `ShadowCaptureQueue.DroppedCount`. On shutdown the consumer stops accepting, drains what is queued within `ShadowCaptureConsumerOptions.DrainTimeout` (default 5 seconds), and counts whatever the deadline left unpersisted in `ShadowCaptureConsumer.AbandonedCount`, logged once. Each loss is counted the moment the deadline declares it, so this holds even when the host's own shutdown timeout fires first: a `StopAsync` abandoned early by its caller's token still counts — and logs — the capture it left mid-save, when the drain deadline later expires on it.

**`DroppedCount` plus `AbandonedCount` is the entire gap between the configured sample rate and what the store holds**, beyond the individually recorded persistence failures in `FailureSnapshot()`. Without those numbers the real capture rate sits quietly below the configured rate and every offline comparison rests on a denominator nobody can reconstruct.

The same accounting lands on the `Rag.NET.Evaluation` meter, with the identity stated where the numbers are read:

| Counter | Meaning |
|---|---|
| `ragnet.shadow.enqueued` | Sampled captures handed to the queue, accepted or not — tracks the configured sample rate |
| `ragnet.shadow.dropped` | Queue full, or completed by shutdown |
| `ragnet.shadow.processed` | Pairs persisted, secondary run included |
| `ragnet.shadow.failed` | Store or sanitiser threw; each also recorded individually |
| `ragnet.shadow.abandoned` | Unpersisted when the shutdown drain deadline expired |

`enqueued − dropped − failed − abandoned = processed`. A dashboard whose `processed` sits below `enqueued` with all three loss counters at zero is watching a consumer that has not caught up yet, not loss.

## Scoring captured pairs: `ShadowReplay`

Shadow mode captures and does not score. `ShadowReplay.From` turns stored captures into exactly what `RagAbTester.CompareAsync` consumes — two variants whose pipelines replay each side's captured answer and context texts, plus the sample list:

```csharp
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Shadow;

// Read captures from wherever your IShadowCaptureStore persisted them.
IReadOnlyList<ShadowCapture> captures = LoadCaptures();

// Optional: ground truth added after capture, keyed by question.
var references = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["What is the refund window?"] = "Refunds are accepted within 30 days of purchase.",
};

var replay = ShadowReplay.From(captures, references);

AbReport report = await tester.CompareAsync(replay.VariantA, replay.VariantB, replay.Samples);
```

Pass `replay.Samples` **unfiltered and unreordered** — the replaying pipelines serve captures by position and verify the question on every call, so a subset or a shuffle fails loudly instead of pairing answers with the wrong questions. To compare a subset, build a replay from that subset of captures.

### Two of four metrics without references; all four with them

Production traffic carries no ground truth, so a capture stores no reference answer and an unannotated replay scores with the reference-free metrics only:

| Metric | On captured pairs alone | With references supplied at replay |
|---|---|---|
| Faithfulness | Yes | Yes |
| Answer Relevance | Yes | Yes |
| Context Precision | **No** — refuses an empty reference | Yes |
| Context Recall | **No** — refuses an empty reference | Yes |

**Supplying reference answers at replay time unlocks all four — and that is precisely why capture beats inline scoring.** An inline scorer runs at request time, when no ground truth exists, and is forever limited to two metrics; a stored capture is where ground truth can be added later. Annotate the captured questions, pass the answers to `ShadowReplay.From`, and all four RAGAS metrics run over traffic that was production's. Both directions are covered by tests: an unannotated replay scoring with the two reference-free metrics, and an annotated one running the real Context Precision and Context Recall evaluators.

### Read latency and spend from the replay, not the report

**The report's latency and cost sections are meaningless under replay.** The scorer times the live call it makes, which for a replay is a dictionary lookup: it will dutifully report 0.2&nbsp;ms for a pipeline that really took 900&nbsp;ms in production, and its cost section has no ledgers to read. Do not mistake the one for the other.

The real figures are the ones the captures carry, computed by the replay in the report's own shapes so the substitution is mechanical:

- **`ShadowReplay.Latency`** — the production latency comparison, from the captured wall-clocks, paired over the captures where both sides carry one (a failed side carries none).
- **`ShadowReplay.Spend`** — total captured spend by variant name. A side is present only when *every* capture measured its spend; summing whichever captures happened to be measured would present an undercount as the total.

The report's quality metrics and failure accounting are trustworthy as-is; only its two live measurements are meaningless under replay, because a replay makes no live calls to measure.

## Primary spend is not measured, deliberately

Set `ShadowCaptureConsumerOptions.SecondaryCostLedger` — a ledger **dedicated to the secondary pipeline** — and every capture records what its secondary run cost, as a before/after day-window diff. That diff is honest only because the consumer runs secondaries one at a time, so the ledger must be the secondary's alone.

**The primary's spend stays absent, and this is not a gap to be filled later without solving a real problem first.** The primary serves concurrent production traffic on a shared ledger, so a per-request diff over it would include every overlapping request — a fabricated number, worse than an honest absence. `ICostLedger`'s read surface is aggregate time windows with no per-caller attribution; until that changes, no honest per-request primary figure exists. Absent, never zero: a zero would claim the requests were free. Secondary spend is real, per capture, and is how the doubled spend shows up as a number rather than as an unexplained rise on a bill.

## No significance testing

Shadow mode counts captures; it does not decide when you have enough of them, and nothing in this feature performs significance testing on your behalf. **Two averages over ten captured pairs is not a result.** A replayed comparison gets the offline scorer's paired bootstrap interval, and the discipline from the [A/B guide](evaluation.md#a-confidence-interval-spanning-zero-is-not-a-win) applies unchanged: an interval spanning zero means *no difference this run can support*, and over a handful of captures it will span zero almost regardless of what the variants did. Let captures accumulate; check `DroppedCount` and `AbandonedCount` before trusting the denominator; and read the interval, not the means.

## Options

| Option | Default | Description |
|---|---|---|
| `ShadowPipelineOptions.SampleRate` | `0.0` | Share of `AskAsync` calls shadowed, in [0, 1]. Out-of-range refused, never clamped. |
| `ShadowPipelineOptions.PrimaryVariantName` | `"primary"` | Label for the served side. Non-blank; must differ from the secondary's. |
| `ShadowPipelineOptions.SecondaryVariantName` | `"shadow"` | Label for the shadowed side. |
| `ShadowCaptureQueueOptions.Capacity` | `1000` | Queue bound. Raising it buys tolerance for consumer stalls at the price of memory holding verbatim production text. |
| `ShadowCaptureConsumerOptions.DrainTimeout` | 5 s | Shutdown drain grace. Zero is valid: everything still queued at stop is abandoned, counted, reported. |
| `ShadowCaptureConsumerOptions.SecondaryCostLedger` | `null` | Dedicated secondary ledger. Null records spend as absent, never zero. |

`ShadowCaptureQueueOptions`, `ShadowCaptureConsumerOptions`, an `IShadowCaptureStore` and an `IShadowCaptureSanitiser` are all honoured if registered before `UseShadow`.

## Limitations

- **`AskAsync` only.** Streaming, retrieval, ingestion and deletion delegate to the primary and are never shadowed.
- **One secondary.** A shadow compares one pair of configurations — which matches the offline scorer, whose statistics are strictly pairwise; `ShadowReplay` refuses a capture set that mixes variant pairs.
- **The in-memory store is not production storage**, and the library ships no other. Persisting production traffic — and everything that legally follows from doing so — is the application's decision.
- **Captured latency is production wall-clock under production load** for the primary, but the secondary runs on the background consumer, one at a time, which is a quieter environment than the primary's. The comparison is honest about what each side measured; it is not a controlled experiment.
