# Service Bus Ingestion Trigger — Design (Phase 2.5)

**Date:** 2026-07-27
**Milestone:** 2 — Deferred Items & Technical Debt, Phase 2.5 (final phase)
**Covers:** the "Service Bus ingestion trigger" deferral from Phase 1.3

## The documented design is the wrong one

`docs/guide/data-providers.md:710-712` specifies this phase as:

> A Service Bus trigger (consume messages from a queue/topic and enqueue the referenced
> documents) is planned but not part of this phase. It will be a thin producer over the same
> `IIngestionJobQueue`.

Built that way it is close to redundant with the webhook path, and it makes durability **worse**.

`ChannelIngestionJobQueue` is an in-memory bounded channel with no persistence —
`data-providers.md:632` says so, and sells the Service Bus trigger as the answer. But a thin
producer would receive a durable broker message, hand it to that in-memory channel, and settle
it. Settling before the channel drains converts at-least-once into **at-most-once on crash** —
reintroducing exactly the loss window Service Bus was supposed to close. Settling after
ingestion is not expressible through `IIngestionJobQueue` as written: `EnqueueAsync` returns
`ValueTask` with no completion signal, and `IngestionJob` carries no correlation handle.

So the trigger owns ingestion end to end instead, bypassing the channel exactly as
`BackgroundPollingTrigger` already does. That is a correction to the published plan, and
`data-providers.md:617` and `:710-712` are corrected with it.

## Scope decisions (agreed)

1. **The processor owns ingestion end-to-end** and settles messages on the outcome.
2. **The BM25 duplicate-posting defect is fixed first**, as a prerequisite.
3. **Sessions are supported, opt-in**, for per-document ordering.
4. **`FileNameSanitizer` relocates to `Rag.NET.Abstractions`**, closing two recorded debts.

---

## 1. The prerequisite: re-ingest is not a replace

Verified in source, not inferred:

- `InMemoryBm25Index.Add` guards with `if (_docs.ContainsKey(docId)) return; // caller must remove before re-adding`.
- The caller does not remove. `StorageBehavior` calls `Bm25Index.Add(ctx.GetNextBm25DocId(), ...)`, and `PipelineIngestor` supplies `() => Interlocked.Increment(ref _nextBm25DocId)` — a **fresh id every call**, so the guard can never fire on a re-ingest.
- `Bm25Index.Remove` runs during ingest only inside `OverwriteBehavior`, under `ctx.Options?.Overwrite == true`.
- `IngestionOptions.Overwrite` defaults to `false`, and the webhook path never sets `Options` at all.

**Consequence:** ingesting the same document twice adds a second complete set of BM25 postings —
duplicate hits and inflated term statistics in keyword and hybrid search. Meanwhile
`data-providers.md:688-689` asserts the opposite: *"a replay re-ingests the same content under
the same `documentId` rather than duplicating it"*.

Service Bus is at-least-once with competing consumers, so duplicates stop being hypothetical.
Shipping the transport onto this would *manifest* a latent defect rather than introduce one —
which is why the fix comes first.

**Fix:** re-ingest becomes a true replace for BM25 — removal before re-add, unconditionally
rather than only under `Overwrite`. `IRagDataManager.Add` has the same unconditional-append
shape and gets the same treatment.

### Recorded, not fixed: orphan tail chunks

The vector store upserts on `(documentId, chunkIndex)`. When a re-ingested document is
*shorter* than its predecessor, chunks beyond the new length survive — a 9-chunk document
replaced by a 5-chunk one leaves chunks 5-8 stranded and retrievable.

Fixing that means making delete-before-insert unconditional, which changes what `Overwrite`
means for every existing caller. That is a semantic change to ingestion, and smuggling it into
a transport phase would be wrong. It is recorded instead.

**The honest statement the docs must carry:** after this phase, re-ingest is a clean replace
for BM25 and a *partial* replace for vectors. Both are better than today; neither is complete.

## 2. The trigger

New package **`Rag.NET.Ingestion.AzureServiceBus`** — the repo's convention is one package per
optional cloud dependency, and this is not a data provider, so `Rag.NET.DataProviders` is the
wrong home. References `Rag.NET.Abstractions`, `Azure.Messaging.ServiceBus` (`7.*`), and the
hosting/logging/DI abstractions `Rag.NET.DataProviders` already takes.

`AzureServiceBusIngestionTrigger` implements **`IHostedService` plus `IAsyncDisposable`**, not
`BackgroundService`: `ServiceBusClient` and `ServiceBusProcessor` are both async-disposable and
`BackgroundService` does not model that. `StartAsync` → `StartProcessingAsync`; `StopAsync` →
`StopProcessingAsync`; disposal releases the client. No existing hosted service in this repo
owns a disposable, so this shape cannot be copied from a sibling.

**It bypasses `ChannelIngestionJobQueue` and calls `IIngestor.IngestAsync` directly**, exactly
as `BackgroundPollingTrigger` bypasses it. Registration follows the poller's precedent —
`AddSingleton<IHostedService>(sp => new ...)` closing over its own options — so multiple
queues or subscriptions coexist without sharing a singleton options instance.

### Settlement

| Outcome | Action |
|---|---|
| Ingestion succeeds | `CompleteMessageAsync` |
| Transient failure (I/O, timeout, throttling) | `AbandonMessageAsync` — the broker redelivers and counts the attempt |
| Permanent failure (unparseable payload, missing required field, repeated failure past `MaxDeliveryCount`) | `DeadLetterMessageAsync` with a reason |

This is the capability that genuinely does not exist today: a job that throws in
`IngestionJobProcessor` is logged at Warning and **silently dropped** — no retry, no
dead-letter, no operator surface.

### Sessions, opt-in

On a session-enabled queue the trigger uses a session processor and treats `SessionId` as the
document id, giving **per-document FIFO**. That closes a real hole: there is no per-`DocumentId`
lock anywhere in the ingestion path, so two hosts ingesting the same document today interleave
`OverwriteBehavior` and `StorageBehavior` with no coordination. Non-session queues keep working
unchanged; sessions are configuration, not a requirement.

This is the one capability the webhook path cannot provide at all.

## 3. Message contract

The message body is **the same JSON the webhook already accepts** — `documentId`, `content`,
`metadata` — so there is one payload contract across both transports rather than two.

This contradicts the docs' phrase *"enqueue the referenced documents"*, which implies pointer
messages ("here is an id, go fetch it"). No such shape exists anywhere in the codebase; the
shipped contract is inline content only. Pointer-based fetch would need a provider to fetch
*from* and is a separate feature. The doc wording is corrected rather than built to.

## 4. `FileNameSanitizer` relocation

`GenericWebhookPayloadParser.cs:77` builds `$"{documentId}.txt"` from an untrusted payload —
recorded as debt in `ROADMAP.md` and unscheduled because `Rag.NET.Api` cannot reach
`FileNameSanitizer` in `Rag.NET.DataProviders`. A Service Bus trigger doing the same thing would
create a **third** copy of that debt in a third assembly.

`FileNameSanitizer` moves to `Rag.NET.Abstractions` (the fix already recorded in `ROADMAP.md`),
is used by the new trigger, and retrofits the webhook parser. That closes two debts and prevents
a third.

It is a **public type changing assembly** — source-breaking for anyone referencing it directly,
and it needs a release note. The parsers' local copy (`EmbeddedMessageMetadata`, recorded
separately) can then also be retired, but that is not this phase.

## 5. Error handling

House posture holds: transient failures are retried by the broker, permanent ones are
dead-lettered with a reason, and neither crashes the processor. Cancellation on host shutdown
stops processing cleanly and does not settle in-flight messages as failures.

Configuration errors — missing connection string, unreachable namespace at startup — fail fast
and loud, matching every other `Use*` extension.

## 6. Testing

- **Unit tests over pure seams**: message → `IngestionJob` translation, and the settle-policy
  decision, as plain functions. `ServiceBusReceivedMessage` is constructible only through
  `ServiceBusModelFactory` — that is what makes this testable at all, and it belongs in the plan
  so the implementer does not discover it late.
- **DI registration tests**, following the existing `UseEventDrivenIngestionTests` shape.
- **Integration is the phase's real unknown.** The Service Bus emulator needs **two containers**
  (emulator plus SQL Edge) and a **mounted config JSON** declaring entities up front, because it
  has no runtime management plane. Every existing fixture in this repo is single-container, and
  there is no `docker-compose` anywhere. The plan attempts the emulator fixture; if it proves
  unreliable, the fallback is an **env-gated live test** mirroring the `RAGNET_DOCINTEL_ENDPOINT`
  precedent — with the fallback **recorded as a coverage gap**, not quietly taken.
- WireMock cassettes do not apply: Service Bus speaks AMQP, not HTTP.
- Determinism convention holds — `TaskCompletionSource` signalling and bounded `WaitAsync`, no
  sleeps, as `BackgroundPollingTriggerTests` already does for hosted services.

## 7. Documentation

- `docs/guide/data-providers.md:617` and `:710-712` — both name the wrong seam ("a thin producer
  over the same `IIngestionJobQueue`") and must be corrected to the end-to-end design.
- `:688-689` — the replay-safety claim is currently false and becomes true only for BM25.
  State the BM25/vector asymmetry from §1 precisely.
- `:632` — the durability note can now point at a trigger that actually delivers durability.
- `docs/reference/features.md:452,458,1066` — three "deferred" mentions.
- `docs/guide/ingestion.md:558` — the event-driven paragraph gains the third trigger.

## Out of scope

- Pointer-style messages (§3) — needs a fetch provider, separate feature.
- Making delete-before-insert unconditional to fix orphan tail chunks (§1) — a semantic change
  to `Overwrite` affecting every caller.
- Topics/subscriptions beyond what a queue name plus an optional subscription name expresses.
- Retiring the parsers' local `FileNameSanitizer` copy (§4).
