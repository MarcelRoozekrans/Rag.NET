# Service Bus Ingestion Trigger Implementation Plan (Phase 2.5)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** An Azure Service Bus trigger that owns ingestion end to end and settles each message on the outcome — built on top of a re-ingest path that actually replaces rather than duplicates.

**Architecture:** Per `docs/plans/2026-07-27-service-bus-ingestion-design.md`. Part A fixes the BM25 duplicate-posting defect and **gates everything else** — shipping an at-least-once transport onto a re-ingest path that appends would turn a latent bug into a routine one. Part B relocates `FileNameSanitizer` so the new package and the webhook parser can both reach it. Part C is the trigger. Part D is docs. **A → B → C → D, strictly sequential.**

**Tech Stack:** .NET 10, `Azure.Messaging.ServiceBus` (`7.*`), `Microsoft.Extensions.Hosting.Abstractions`, xUnit v3, Testcontainers (emulator attempt).

**Conventions:** MA0051 (≤60-line methods), MA0015, ZA0601/ZA0501, EPS05/EPS06, HLQ012/HLQ013 — warnings-as-errors, build ends 0/0. **HLQ012 is muted in tests but active in `src/`**; the repo has exactly two justified pragmas and the standing rule is not to add a third — restructure into synchronous helpers instead. Logging via LoggerMessage source-gen (see `src/Rag.NET.DataProviders/Logging/DataProvidersLog.cs`), never `logger.LogWarning` directly. **`EPS06` bites any test that faults a `ValueTask`-returning member** — `IIngestionJobQueue.EnqueueAsync` is one; the existing EventDriven suites sidestep it with hand-written fakes, so do the same. Conventional commits ending with a blank line then `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Never stage `.lucent/*` or `.claude/worktrees/*`.**

**Read first:** the design doc; `src/Rag.NET/Search/InMemoryBm25Index.cs` (the `Add` guard); `src/Rag.NET/Ingestion/PipelineIngestor.cs` (`GetNextBm25DocId`); `src/Rag.NET/Ingestion/Behaviors/{StorageBehavior,OverwriteBehavior}.cs`; `src/Rag.NET.DataProviders/EventDriven/BackgroundPollingTrigger.cs` (the hosted-service precedent this copies); `src/Rag.NET.DataProviders/RagBuilderExtensions.cs:96-104` (the per-instance `AddSingleton<IHostedService>` registration shape); `src/Rag.NET.DataProviders.AzureBlob/AzureBlobDataProviderExtensions.cs:12-43` (dual credentials).

---

## Part A — Make re-ingest a replace (prerequisite)

### Task A1: prove the defect, then fix it

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs` and/or `OverwriteBehavior.cs`
- Test: `tests/Rag.NET.Tests/` (find where BM25 + ingestion tests live)

**Write the failing test first**, run it, and paste the failure into your report:

```csharp
// 1. Ingest_SameDocumentTwice_DoesNotDuplicateBm25Postings
//    Ingest a document, ingest it again with identical content, then assert a keyword
//    search returns ONE hit for that document — not two.
```

The defect, verified in source (do not re-derive, but do confirm before editing):
- `InMemoryBm25Index.Add` guards `if (_docs.ContainsKey(docId)) return; // caller must remove before re-adding`
- `StorageBehavior.cs:53` calls `Bm25Index.Add(ctx.GetNextBm25DocId(), ec.Chunk)`
- `PipelineIngestor.cs:47` supplies `() => Interlocked.Increment(ref _nextBm25DocId)` — a fresh id per call, so the guard **can never fire**
- `Bm25Index.Remove` runs during ingest only in `OverwriteBehavior`, under `ctx.Options?.Overwrite == true`
- `IngestionOptions.Overwrite` defaults `false`; the webhook path never sets `Options`

**Fix:** removal before re-add becomes unconditional, so re-ingest is a true replace for BM25.
`IRagDataManager.Add` has the same unconditional-append shape — give it the same treatment and
say so.

Think about where the removal belongs. `OverwriteBehavior` already does exactly this under a
flag; making it unconditional there may be cleanest, but check what else `Overwrite` gates
(`VectorStore.DeleteByDocumentIdAsync`) — **you must not make the vector-store delete
unconditional**, that is explicitly out of scope (§1 "Recorded, not fixed") because it changes
what `Overwrite` means for every caller. Only the BM25 and data-manager removals become
unconditional.

```csharp
// 2. Ingest_SameDocumentTwice_VectorStoreStillUpserts — pins that you did NOT change vector semantics
// 3. Ingest_ShorterDocument_LeavesOrphanTailChunks — pins the RECORDED limitation, so the
//    asymmetry is visible in the suite rather than only in prose. Name it so it reads as
//    intentional, and comment it pointing at the design doc.
```

**Commit:** `fix(ingestion): make re-ingest replace BM25 postings instead of appending`

---

## Part B — Relocate `FileNameSanitizer`

### Task B1: move it to Abstractions and retrofit the webhook parser

**Files:**
- Move: `src/Rag.NET.DataProviders/FileNameSanitizer.cs` → `src/Rag.NET.Abstractions/`
- Modify: every current consumer (grep `FileNameSanitizer` across `src/`)
- Modify: `src/Rag.NET.Api/Webhooks/GenericWebhookPayloadParser.cs:77` — builds `$"{documentId}.txt"` from an untrusted payload
- Test: existing sanitizer tests move with it; add one for the webhook parser

Two recorded ROADMAP debts close here: the unsanitized webhook filename, and the sanitizer being
unreachable from `Rag.NET.Api`. A Service Bus trigger doing the same thing would make a third
copy.

**This is a public type changing assembly** — source-breaking for anyone referencing it
directly. Verify the whole solution still builds and note it for the release notes.

Leave the parsers' local copy (`EmbeddedMessageMetadata`) alone — recorded separately, not this
phase.

**Commit:** `refactor(abstractions): relocate FileNameSanitizer so every layer can reach it`

---

## Part C — The trigger

### Task C1: package + pure seams

**Files:**
- Create: `src/Rag.NET.Ingestion.AzureServiceBus/` — csproj referencing `Rag.NET.Abstractions`, `Azure.Messaging.ServiceBus` (`7.*`), `Microsoft.Extensions.{Hosting,Logging,DependencyInjection}.Abstractions` (`10.*`). Add to `Rag.NET.slnx`.
- Create: the message-translation and settle-policy functions.
- Test: `tests/Rag.NET.Ingestion.AzureServiceBus.Tests/`

**Build the pure seams first and test them without a broker.** Two functions:
- message body → `IngestionJob` (same JSON contract the webhook accepts: `documentId`, `content`, `metadata`)
- outcome → settle action (complete / abandon / dead-letter)

**`ServiceBusReceivedMessage` is constructible only via `ServiceBusModelFactory`** — that is what
makes any of this unit-testable, and it is easy to lose an afternoon discovering it. Use it.

Filenames come from `FileNameSanitizer` (Part B), not string interpolation.

```csharp
// 1. Translate_ValidPayload_ProducesJob
// 2. Translate_MissingRequiredField_IsPermanentFailure   (→ dead-letter, not abandon)
// 3. Translate_MalformedJson_IsPermanentFailure
// 4. Translate_HostileDocumentId_SanitizesTheFileName
// 5. SettlePolicy: success → complete; transient → abandon; permanent → dead-letter
```

**Commit:** `feat(ingestion): Service Bus message translation and settle policy`

### Task C2: the hosted trigger

**Files:** the trigger, its options, its log class, its builder extension.

`AzureServiceBusIngestionTrigger : IHostedService, IAsyncDisposable` — **not `BackgroundService`**,
because `ServiceBusClient` and `ServiceBusProcessor` are both async-disposable and
`BackgroundService` does not model that. No existing hosted service in this repo owns a
disposable, so you cannot copy a sibling's shutdown shape. `StartAsync` → `StartProcessingAsync`;
`StopAsync` → `StopProcessingAsync`; dispose releases processor then client.

**It calls `IIngestor.IngestAsync` directly and does NOT touch `IIngestionJobQueue`** — exactly as
`BackgroundPollingTrigger` bypasses it. Routing through the in-memory channel is the design the
docs specified and the design doc rejects; §"The documented design is the wrong one" explains
why, and if you find yourself reaching for the queue, re-read it.

**Registration** copies `RagBuilderExtensions.cs:96-104` — `AddSingleton<IHostedService>(sp => new ...)`
closing over its own options, so multiple queues coexist without sharing a singleton options
instance. Dual credentials, mapping onto `ServiceBusClient`'s own constructor pair:
- `(connectionString, queueName, configure?)`
- `(fullyQualifiedNamespace, TokenCredential, queueName, configure?)`
following `AzureBlobDataProviderExtensions.cs:12-43` including its argument guards.

**Sessions, opt-in.** On a session-enabled queue use `ServiceBusSessionProcessor` and treat
`SessionId` as the document id, giving per-document FIFO. Non-session queues keep working. This
closes the hole where two hosts ingest one document with no coordination — there is no
per-`DocumentId` lock anywhere in the ingestion path.

```csharp
// 6. StartAsync_StartsProcessing_StopAsync_StopsAndDisposes
// 7. SuccessfulIngestion_CompletesTheMessage
// 8. TransientFailure_AbandonsForRedelivery
// 9. PermanentFailure_DeadLettersWithAReason
// 10. HostShutdown_DoesNotSettleInFlightMessagesAsFailures
// 11. DI: both credential overloads register one IHostedService; two registrations coexist
```

Determinism: `TaskCompletionSource` signalling and bounded `WaitAsync`, no sleeps — the shape
`BackgroundPollingTriggerTests.cs:54-71` already uses for hosted services.

**Commit:** `feat(ingestion): Azure Service Bus ingestion trigger`

### Task C3: integration coverage — the phase's real unknown

**Attempt the Service Bus emulator fixture.** It needs **two containers** — the emulator plus
SQL Edge (`ACCEPT_EULA`, `MSSQL_SA_PASSWORD`) on a shared Testcontainers `INetwork` — and a
**mounted `Config.json`** declaring queues up front, because the emulator has no runtime
management plane. AMQP on 5672; connection string uses the local-emulator form with
`UseDevelopmentEmulator=true`.

**Every existing fixture in this repo is single-container and there is no `docker-compose`
anywhere**, so this is a new pattern. Budget for it not working.

**If it proves unreliable, fall back** to an env-gated live test mirroring the
`RAGNET_DOCINTEL_ENDPOINT` precedent (`Assert.SkipWhen` on missing credentials) — and **record
the fallback as a coverage gap in your report and in the docs**, do not take it quietly. Either
outcome is acceptable; an unreported one is not.

**Commit:** `test(ingestion): Service Bus integration coverage`

---

## Part D — Docs

**Files:**
- `docs/guide/data-providers.md` — `:617` and `:710-712` both name the wrong seam ("a thin producer over the same `IIngestionJobQueue`"); correct to the end-to-end design. `:688-689` claims replay safety that is currently false and becomes true **only for BM25** — state the BM25/vector asymmetry from design §1 precisely. `:632` can now point at a trigger that delivers real durability.
- `docs/reference/features.md:452,458,1066` — three "deferred" mentions.
- `docs/guide/ingestion.md:558` — the event-driven paragraph gains the third trigger.

Also document: settlement behaviour (complete/abandon/dead-letter) and that this is the first
path with a DLQ; sessions and what they buy; the shared message contract; and that
`FileNameSanitizer` moved assembly.

**Commit:** `docs(ingestion): Service Bus trigger, settlement and the replace semantics`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Green with exact counts: the new test project, `tests/Rag.NET.Tests`, `tests/Rag.NET.DataProviders.Tests`, `tests/Rag.NET.Api.Tests` (Part B touches the webhook parser).
3. State plainly whether the emulator fixture worked or the env-gated fallback was taken.
4. `docs/planning/ROADMAP.md` + `MILESTONE.md` — **at close-out, after the whole-phase review.** This is the last phase of Milestone 2, so the milestone audit follows.
5. Whole-phase review; merge decision.
