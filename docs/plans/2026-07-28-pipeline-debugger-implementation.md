# Pipeline Debugger Implementation Plan (Phase 3.4)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A disposable in-memory trace of the last N query executions — chunks and scores, stage latencies, which guards and sanitisers fired — readable in-process or, opt-in, over an authenticated HTTP endpoint.

**Architecture:** Two packages. `Rag.NET.Diagnostics` owns capture and a bounded ring buffer with no ASP.NET dependency; `Rag.NET.Diagnostics.AspNetCore` adds an explicit endpoint. A trace is assembled from three existing sources — stage spans via `ActivityListener`, retrieval via a behavior, answers via a decorator — plus two new seams for the prompt and for guard/sanitiser actions.

**Tech Stack:** .NET 10, `System.Diagnostics` (`ActivitySource`/`ActivityListener`), ASP.NET Core minimal APIs, xUnit v3, NSubstitute.

**Design:** `docs/plans/2026-07-28-pipeline-debugger-design.md`. Read it first — especially §3 (why not extend the audit log), §5 (content posture) and §6 (why capture is deliberately not sanitised).

---

## What already exists — read these before writing anything

This phase is mostly a **join**, not a new tracing system. Building a parallel capture path would duplicate a working one, which is the mistake Phases 3.1 and 3.2 spent their time undoing.

| Thing | Where | Use it for |
|---|---|---|
| Stage spans `ragnet.{ingest,parse,chunk,embed,store,retrieve,ask}` | `src/Rag.NET/Telemetry/RagTelemetry.cs` | latency breakdown, free via `ActivityListener` |
| `AuditChunkRef` (`DocumentId`, `ChunkIndex`, `Score`) | `src/Rag.NET.Security/Audit/AuditChunkRef.cs` | **mirror its field names**; do not reference it — see the note below |
| `AuditRetrievalBehavior` | `src/Rag.NET.Security/Audit/` | **mirror its shape** for the diagnostics behavior |
| `AuditAnswerEngineDecorator` | `src/Rag.NET.Security/Audit/` | mirror for the answer decorator |
| `LogQueryText` / `LogAnswerText` | `src/Rag.NET.Security/Audit/AuditLogOptions.cs` | the naming the new `Capture*` flags parallel |

Interfaces to decorate — all three are synchronous and trivially wrappable:

```csharp
IRetrievalGuard  : IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results);
IQuerySanitiser  : string Sanitise(string query);
IChunkSanitiser  : string Sanitise(string text, IReadOnlyDictionary<string, string> metadata);
```

Two details from `AuditRetrievalBehavior` worth copying:

- `ctx.Extensions["audit_request_id"]` — `RetrievalContext` has an `Extensions` dictionary, a legitimate correlation carrier.
- **Audit failures are swallowed and logged; results are always returned.** Diagnostics must hold the same line, harder: a debugger that breaks the pipeline is worse than no debugger.

One thing the diagnostics side does *better*: `AuditCorrelationContext`'s own XML warns it must be registered scoped under concurrency, because it is a plain field. Using `Activity.Current.TraceId` avoids that entirely — it is async-local and correct under concurrency without registration advice.

---

## Conventions that will fail the build if ignored

- **Warnings are errors.** MA0051 (methods ≤ 60 lines), MA0015 (`paramName` must be a real parameter — in an `init` accessor that is `value`), MA0048 (**one public type per file, name must match**), MA0006 (`string.Equals` not `==`), MA0008, ZA0601/ZA0501, EPS05/EPS06, EPC12, HLQ004 (`foreach` over a `ReadOnlySpan` needs `ref readonly`), HLQ012 (no `foreach` over `List<T>`), HLQ013 (`foreach` not `for` over arrays).
- **LoggerMessage source-gen** for all logging — `partial class` + `[LoggerMessage]`. Never `logger.LogWarning` directly.
- **No new `#pragma` or `SuppressMessage`.**
- xUnit v3: always `TestContext.Current.CancellationToken`. No sleeps.
- **Commits:** conventional, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — explicit paths. `.lucent/*` is expected dirty; leave it.

Verify after every task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

Baselines: `Rag.NET.Tests` **1308**, `Rag.NET.Evaluation.Tests` **262**, `Rag.NET.Api.Tests` **63**, `Rag.NET.DataProviders.Tests` **69**, plus a new `tests/Rag.NET.Diagnostics.Tests`.

New projects must be added to `Rag.NET.slnx` and follow the repo's csproj conventions — copy the shape from an existing small package such as `src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj`, including the `InternalsVisibleTo` `AssemblyAttribute` form used by ~35 other projects.

---

## Part A: the ring buffer

Pure and bounded, so it is table-testable with no pipeline. Same reason `RagasMath` and `ReservoirSampler` came first in their phases.

### Task A1: `RagTrace` and the trace model

**Files:**
- Create: `src/Rag.NET.Diagnostics/RagTrace.cs`, `TraceStage.cs`, `TraceGuardAction.cs`, `RagTraceOptions.cs`
- Create: `src/Rag.NET.Diagnostics/Rag.NET.Diagnostics.csproj`
- Modify: `Rag.NET.slnx`

`RagTrace` carries: `TraceId`, `StartedAt`, `Query` (nullable — only when content capture is on), `QueryHash` (always), `Chunks` (`IReadOnlyList<TraceChunk>`), `GuardActions`, `Stages`, `Prompt` (nullable), `Answer` (nullable).

`TraceChunk` carries `DocumentId`, `ChunkIndex`, `Score` and a nullable `Text`.

> **Corrected twice after this task was written.** It first said `IReadOnlyList<AuditChunkRef>` *"plus an optional parallel text list"*. Two positionally-aligned lists can drift, and chunk text attached to the wrong chunk is worse than no text — it sends you to debug a chunk that was never involved. Composition makes that unrepresentable.
>
> It then said to reuse `AuditChunkRef` rather than redefine it. Measured, that reference takes `Rag.NET.Diagnostics` from **15 transitive packages to 41** — SQLite and its native binaries, `Microsoft.ML.Tokenizers` and its data file, Polly, protobuf — for one three-property record in a package of five records and a ring buffer. `TraceChunk` mirrors the field names instead, so the vocabulary is shared without the assembly being. It was never a shape that could drift out of sync anyway: it diverges by design the moment it carries `Text`, which `AuditChunkRef` deliberately does not.
>
> **`src/Rag.NET.Diagnostics` therefore has no `ProjectReference` at all.** Keep it that way: the behaviors and decorators in B3–B5 need pipeline types, and if they cannot live here without dragging the closure back, they belong in their own assembly. Doc comments referring to `IAuditLog` and `AuditLogOptions` are `<c>` text rather than `<see cref>` as a result — a real, accepted cost.
>
> **Corrected a third time, at B3.** "No `ProjectReference` at all" survived exactly as long as the package held nothing but records, and B3 is where that ends: `IRetrievalBehavior` and `RetrievalContext` are in `Rag.NET`, and `IAnswerEngine`, `IRetrievalGuard`, `IQuerySanitiser`, `IChunkSanitiser` and C1's own `IRagBuilder` are in `Rag.NET.Abstractions`. There is no arrangement in which the capture seams name no pipeline type.
>
> Measured, from `obj/project.assets.json` (analyzer packages excluded — six of the counts below are Meziantou, Roslynator, ErrorProne ×2, NetFabric and ZeroAlloc.Analyzers, and none of them ship): `Rag.NET.Diagnostics` **2**, `Rag.NET.Abstractions` **16**, `Rag.NET` **43**, `Rag.NET.Security` **41**. The csproj's "15 → 41" was measured before the analyzer set changed; the ratio it was arguing about is unaffected.
>
> **`Rag.NET.Diagnostics` now references `Rag.NET`, and still not `Rag.NET.Security`.** The distinction is not the package count, it is who pays it. `Rag.NET.Security` is optional: a team that wants a debugger and has never enabled auditing would have been handed SQLite, its native binaries, the ML tokenizers and their data file, Polly and protobuf for nothing. `Rag.NET` is the thing being diagnosed — every consumer of this package already has it and its closure, so the reference adds nothing to any real dependency graph.
>
> **An `Rag.NET.Abstractions`-only variant was tried and does not work**, and the reason is not about packages. `IRetriever` is decorable (`TagRetriever`, `TimeWeightedRetriever` and `DeepResearchRetriever` all do it) and lives in Abstractions, so a decorator over it would have captured retrieval for 16 packages instead of 43 — but `PipelineRetriever` opens the `ragnet.retrieve` span *inside* `RetrieveAsync`, around the behavior pipeline. A decorator over `IRetriever` therefore runs **outside** that span and reads whatever ambient activity the host happens to supply, or none; a behavior runs **inside** it and reads the very trace id `StageActivityListener` files that stage's latency under. Correlation is the entire feature, so the seam has to be the behavior.

`RagTraceOptions`: `Capacity` (default 50), `CaptureQueryText`, `CaptureChunkText`, `CapturePromptText`, `CaptureAnswerText` (**all default `false`**), `MaxCapturedCharacters` (default 4000, per field).

Every `Capture*` flag's XML must say what enabling it puts in memory, and name `AuditLogOptions.LogQueryText`/`LogAnswerText` as the compliance-grade parallel. Validate `Capacity >= 1` and `MaxCapturedCharacters >= 0` in the property setters, naming the property in the message (MA0015 forces `paramName` to be `value`).

**Properties are `set`, not `init`.** C1 configures these through `AddRagDiagnostics(Action<RagTraceOptions>?)`, matching `AddAuditLog` in `src/Rag.NET.Security/RagBuilderExtensions.cs`, and a configure delegate is handed an instance that already exists — an `init` accessor will not compile there. Corrected after the Part A review.

That validation needs tests, which this task cannot hold — the test project does not exist until A2. `tests/Rag.NET.Diagnostics.Tests/RagTraceOptionsTests.cs` lands with A2 and covers the defaults and both refusals.

**Commit:** `feat(diagnostics): trace model and capture options`

### Task A2: the bounded ring buffer

**Files:**
- Create: `src/Rag.NET.Diagnostics/Internal/TraceRingBuffer.cs`
- Test: `tests/Rag.NET.Diagnostics.Tests/TraceRingBufferTests.cs`

**Step 1: write the failing tests.**

```csharp
[Fact]
public void Add_BeyondCapacity_EvictsOldestFirst()
{
    var buffer = new TraceRingBuffer(capacity: 3);
    for (var i = 0; i < 5; i++)
        buffer.Add(TraceWithId($"trace-{i}"));

    // Newest first, oldest evicted. A debugger is read immediately after the request,
    // so recency is the ordering that matters.
    Assert.Equal(["trace-4", "trace-3", "trace-2"], buffer.Snapshot().Select(t => t.TraceId));
}

[Fact]
public void Snapshot_IsAPointInTimeCopy_NotALiveView()
{
    var buffer = new TraceRingBuffer(capacity: 3);
    buffer.Add(TraceWithId("a"));
    var snapshot = buffer.Snapshot();
    buffer.Add(TraceWithId("b"));

    // A reader iterating a snapshot must not observe concurrent writes mid-iteration.
    Assert.Single(snapshot);
}

[Fact]
public async Task Add_UnderConcurrentWriters_NeverExceedsCapacityAndLosesNothingButTheEvicted()
{
    var buffer = new TraceRingBuffer(capacity: 100);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
    {
        for (var i = 0; i < 250; i++)
            buffer.Add(TraceWithId($"w{w}-{i}"));
    })));

    Assert.Equal(100, buffer.Snapshot().Count);
}

[Fact]
public void TryGet_ByTraceId_FindsARetainedTrace() { … }

[Fact]
public void TryGet_AnEvictedTraceId_ReturnsFalse() { … }
```

**Step 3: implement.** A lock plus a fixed array is correct and obvious here; do not reach for a lock-free ring. The critical section is an array write and an index increment. Use `Lock` (.NET 9+, already used in ~5 places in this repo).

**Verify the eviction test bites:** make `Add` no-op once full instead of evicting, and confirm `Add_BeyondCapacity_EvictsOldestFirst` fails. Report the message.

**Commit:** `feat(diagnostics): bounded trace ring buffer`

---

## Part B: capture

### Task B1: `ITraceCollector` and the truncation rule

**Files:**
- Create: `src/Rag.NET.Diagnostics/ITraceCollector.cs`, `Internal/TraceCollector.cs`
- Test: `tests/Rag.NET.Diagnostics.Tests/TraceCollectorTests.cs`

The collector builds a trace incrementally, keyed by `TraceId`, and commits it to the buffer when the request ends. It owns the **content gate**: every text field passes through a single `Capture(string?, bool enabled)` helper that returns `null` when the flag is off and truncates to `MaxCapturedCharacters` when on.

**One gate, one place.** Four independently-written `if (options.CaptureX)` checks is four chances to get it wrong, and the one that matters most is the one nobody tests.

**The test that carries the phase:**

```csharp
[Fact]
public void WithDefaultOptions_NoTextIsCaptured()
{
    var collector = new TraceCollector(new RagTraceOptions(), new TraceRingBuffer(10));
    collector.RecordQuery(traceId, "what is the admin password");
    collector.RecordChunks(traceId, [Chunk("secret contract text")]);
    collector.RecordPrompt(traceId, "system: ...\nuser: what is the admin password");
    collector.RecordAnswer(traceId, "the password is hunter2");
    collector.Commit(traceId);

    var trace = Assert.Single(buffer.Snapshot());

    // Defaults must be safe. "Turn on debugging" must not silently mean "start retaining
    // customer documents and user questions in memory".
    Assert.Null(trace.Query);
    Assert.Null(trace.Prompt);
    Assert.Null(trace.Answer);
    // Structure is still captured — that is the point of the default. The chunk is present;
    // only its text is withheld.
    Assert.NotEmpty(trace.QueryHash);
    Assert.Null(Assert.Single(trace.Chunks).Text);
}
```

**Verify by mutation**: flip each `Capture*` default to `true` in turn and confirm this test fails each time. Four mutations, four failures. Report them. This is the difference between a debugger and a data leak, and it is the one property in this phase that must not regress silently.

Also test: with flags on, text over `MaxCapturedCharacters` is truncated and the truncation is visible (not silent) — a trace that looks complete but is not would mislead exactly when someone is debugging. `RagTraceOptions.TruncationMarker` is the visible suffix.

**Uncommitted traces need their own bound**, which this plan did not anticipate. A trace is started by the first `Record*` call and removed by `Commit`; a request that throws in between leaves it in the map forever, which is the ring buffer's own bound defeated one level up. The collector caps in-flight traces at four times `Capacity` and declines to start new ones past it.

**Commit:** `feat(diagnostics): trace collector with a single content gate`

### Task B2: stage timings from the existing spans

**Files:**
- Create: `src/Rag.NET.Diagnostics/Internal/StageActivityListener.cs`
- Test: `tests/Rag.NET.Diagnostics.Tests/StageActivityListenerTests.cs`

Subscribe an `ActivityListener` to the source named `Rag.NET` and record each `ragnet.*` span's name, duration and `TraceId` into the collector.

**`RagTelemetry` is `internal`**, so the source name cannot be referenced from another assembly. Either hard-code `"Rag.NET"` with a comment pointing at `RagTelemetry.SourceName`, or make that constant public. **Prefer hard-coding with the comment** — making an internal telemetry detail public to satisfy a listener is a larger commitment than it looks, and Phase 4.4 owns the OTel surface. *(Done as written.)*

Filter on the `ragnet.` name prefix as well as the source. The source is shared, so a future non-stage span under `Rag.NET` would otherwise appear in traces as though it were a stage.

Note that subscribing with `AllData` **changes sampling**: the pipeline's spans start being created even with no exporter configured. That is unavoidable — an unsampled `StartActivity` returns `null` and there is nothing to time — but it belongs in the type's XML and eventually in the docs.

Tests need a real `Activity`, which requires a listener with `Sample = () => ActivitySamplingResult.AllData` — otherwise `StartActivity` returns `null` and the test passes vacuously. **Assert that the activity was actually created** before asserting on what was recorded. This bites hardest in the negative tests: *"a span from another source is not recorded"* must supply its **own** `AllData` listener for that other source, or it passes by proving only that an unsampled span is not recorded.

**Commit:** `feat(diagnostics): collect stage timings from the existing spans`

### Task B3: retrieval and answer capture

**Files:**
- Create: `src/Rag.NET.Diagnostics/DiagnosticsRetrievalBehavior.cs`, `DiagnosticsAnswerEngineDecorator.cs`
- Test: matching test files

Mirror `AuditRetrievalBehavior` and `AuditAnswerEngineDecorator` closely — same swallow-and-log posture, same `LoggerMessage` source-gen. Correlate on `Activity.Current?.TraceId`, not a generated GUID.

**When there is no current `Activity`, capture nothing and do not throw.** A pipeline running without a listener is normal; that must be a silent no-op rather than a crash or a fabricated id.

> **Three things this task found that the plan and the design both had wrong.**
>
> **1. The reference.** See the corrected A1 note: the package now references `Rag.NET`, still never `Rag.NET.Security`, and the seam has to be an `IRetrievalBehavior` rather than an `IRetriever` decorator because only the behavior runs inside the `ragnet.retrieve` span.
>
> **2. Nothing calls `Commit`, so no trace ever reaches the ring buffer.** B1 built `Commit`, and no task in the plan is assigned to call it. Every part of B is therefore readable only through `ITraceCollector.Current` — which is what B4's own test sketch already does, so the gap was there in writing and went unnoticed. **This belongs to C1**, and it is not a one-liner: `ragnet.retrieve` and `ragnet.ask` are **siblings**, opened and closed in sequence by `RagPipeline.AskAsync`, so "commit when the outermost `ragnet.*` span stops" would commit at the end of retrieval and throw away everything the answer contributes. The workable options are to commit from the answer decorator (leaving retrieve-only executions to expire against the in-flight ceiling), or to give `RagPipeline` a span that encloses both — a second edit to production code, which B5 was supposed to be the only one of.
>
> **3. The join only works under an ambient activity, which the design asserted rather than checked.** Design §4 says the trace is "joined by `TraceId` from `Activity.Current`". With no ambient activity — a console host, a worker, any of the pipeline's own tests — `StartActivity` creates each of the two sibling stage spans as its own **root**, so `ragnet.retrieve` and `ragnet.ask` get **different trace ids** and the two halves of a query cannot join no matter what captures them. Under ASP.NET, where the request activity is the shared parent and where the C2 endpoint is the point, it works as described. B2's `NestedStageSpans_JoinIntoOneTraceOnTheSharedTraceId` passes because it nests the spans; the pipeline does not. Fixing it is the same edit as (2) — one span around the whole query — and the two should be done together.

**Commit:** `feat(diagnostics): capture retrieval results and generated answers`

### Task B4: guard and sanitiser actions — the real diagnostic hole

**Files:**
- Create: `src/Rag.NET.Diagnostics/Internal/TracingRetrievalGuard.cs`, `TracingChunkSanitiser.cs`, `TracingQuerySanitiser.cs`
- Test: matching test file

Today, when `RbacRetrievalGuard` drops a chunk or `PiiChunkSanitiser` rewrites one, **nothing anywhere records it**, so *"why is that chunk missing from the answer"* cannot be answered. This task closes that.

Each decorator wraps one implementation, calls through, and records a `TraceGuardAction`: the decorated type's name, how many results went in and came out, and — only under content capture — what changed.

```csharp
[Fact]
public void Guard_ThatDropsTwoOfFive_IsRecordedAsHavingDroppedTwo()
{
    var guard = new TracingRetrievalGuard(new DropsTwoGuard(), collector, options);

    guard.Inspect(FiveResults());

    var action = Assert.Single(collector.Current(traceId).GuardActions);
    Assert.Equal(nameof(DropsTwoGuard), action.Component);
    Assert.Equal(5, action.InputCount);
    Assert.Equal(3, action.OutputCount);
}
```

Also test that a sanitiser which changes text is recorded as having changed it, and one that returns its input unchanged is recorded as a no-op rather than omitted — *"the guard ran and did nothing"* and *"the guard never ran"* are different answers to the debugging question.

> **The content leak this task had to fix first.** The Part A review found that `TraceCollector.RecordGuardAction` gated **both** text fields on `CaptureChunkText`, and `TraceGuardAction`'s XML said the same. That is correct for a retrieval guard and a chunk sanitiser, and wrong for a query sanitiser, whose input **is the user's raw question** — so `CaptureChunkText = true` with `CaptureQueryText = false` would have retained every traced user question in process memory, silently, which is the one thing the options type exists to prevent. `CapturePromptText`'s remarks one file over already handled the same overlap correctly by naming what it retains in terms of the other two flags.
>
> Fixed by making the producer declare what kind of content it is producing: a new public `TraceContentKind` (`Query`, `Chunk`, `Prompt`, `Answer`), a third parameter on `ITraceCollector.RecordGuardAction`, and one `kind → flag` switch inside the collector that every field now goes through. **The single gate survives** — `Capture` is still the only place a text field is kept or dropped; it just no longer assumes every guard action holds document text. An unrecognised kind fails closed. `TraceGuardAction`'s and `RagTraceOptions`' XML now say which flag governs which producer.
>
> **Constructor shapes differ from the sketch above, deliberately.** `TracingRetrievalGuard` takes `RagTraceOptions`; the two sanitisers do not. The guard is the only one of the three that has to *build* the text it records — joining several kilobytes of chunk text per query — so it reads `CaptureChunkText` to skip work that would be discarded, which is **work avoidance, not a second gate**: the collector remains authoritative and can only be more restrictive, so the check can cost a trace text it was entitled to and can never leak text it was not. The sanitisers' two strings already exist, so there is nothing to skip and no reason to read the flags twice.
>
> **Two facts about where these run that C1 needs.** All three interfaces are resolved as `IEnumerable<T>`, not as a single service — `RetrievalGuardBehavior.Guards`, `ChunkSanitiserBehavior.Sanitisers` and `QuerySanitiserPipelineDecorator`'s constructor all take the enumerable — so C1's decoration must replace **every** `ServiceDescriptor` registered for the interface, not the last one, and the `ConfigureResilience` idiom (which decorates single registrations) does not transfer unchanged. And `IChunkSanitiser` runs at **ingestion**, not at query time: its actions land in whatever trace the ingestion spans are under, which is a different trace from the query that later surfaces the chunk.

**Commit:** `feat(diagnostics): record what guards and sanitisers removed`

### Task B5: prompt capture

**Files:**
- Modify: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`
- Create: the seam abstraction in `src/Rag.NET.Abstractions/`

The invasive part. `ChatAnswerEngine` assembles the prompt and nothing observes it. Add the **smallest possible** seam — an optional sink the engine calls with the assembled prompt, defaulting to nothing.

Keep the change to `ChatAnswerEngine` under ten lines and behaviour-identical when no sink is registered. **`Rag.NET.Tests` must stay at 1308 with no test edited**; if a test needs changing, the seam is too invasive — stop and reconsider.

> **Done, at three lines of code plus a four-line comment.** `IPromptObserver` in `src/Rag.NET.Abstractions/Abstractions/`, an optional trailing constructor parameter on `ChatAnswerEngine`, one more `GetService` in `CreateFromServices`, and one `promptObserver?.OnPromptAssembled(messages)`. `Rag.NET.Tests` stayed at **1308** with nothing edited: an optional trailing parameter is source-compatible with every existing construction.
>
> **Both `ragnet.ask` call sites capture, from one call site in code.** `AskAsync` and `AskStreamingAsync` each open their own `ragnet.ask` span and then both call `BuildMessagesAsync` inside it, so putting the seam at the end of that method covers both paths, cannot drift apart from itself, and needs no duplicated line at either caller. It also gives the streamed path the only capture it gets — `DiagnosticsAnswerEngineDecorator` deliberately does not record streamed answers, because buffering one to record it would change the memory profile of the stream being observed.
>
> **The seam passes `IReadOnlyList<ChatMessage>`, not a string.** Flattening a prompt is a decision about how it should be read, it cannot be undone by the observer afterwards, and the engine has no business making it for a package it knows nothing about — so `TracePromptObserver` in the diagnostics package renders it, keeping the roles. This commits nothing new to the public surface: `RagOptions.ConversationHistory` already exposes `ChatMessage`, and `Rag.NET.Abstractions` already references `Microsoft.Extensions.AI.Abstractions`.
>
> Note that the prompt is therefore filed under **`ragnet.ask`**'s trace id while the chunks are filed under **`ragnet.retrieve`**'s. Those are the same id only when the host supplies an ambient activity for both spans to descend from — see the B3 note.

**Commit:** `feat(diagnostics): observe the assembled prompt`

---

## Part C: registration and the endpoint

### Task C1: DI registration

**Files:**
- Create: `src/Rag.NET.Diagnostics/RagBuilderExtensions.cs`
- Test: registration tests

`AddRagDiagnostics(Action<RagTraceOptions>?)` registers the collector, buffer, listener, behavior and decorator, and **decorates any already-registered** `IRetrievalGuard`, `IChunkSanitiser` and `IQuerySanitiser`. Follow the decoration idiom already used by `ConfigureResilience` (which decorates `IEmbeddingGenerator` and `IVectorStore`) — read it rather than inventing one.

Test that registering diagnostics **after** the security package still decorates the guards, and that registering with no guards present is a no-op rather than a failure.

**Commit:** `feat(diagnostics): registration that decorates existing guards and sanitisers`

### Task C2: the endpoint

**Files:**
- Create: `src/Rag.NET.Diagnostics.AspNetCore/` (project, `EndpointRouteBuilderExtensions.cs`)
- Test: `tests/Rag.NET.Diagnostics.Tests/` or a new AspNetCore test project

`MapRagNetTrace()` — explicit, never automatic. Routes: list recent traces, fetch one by id. Behind the existing `ApiKeyMiddleware`; read `src/Rag.NET.Api/Authentication/ApiKeyMiddleware.cs` and the webhook route-exemption precedent in `EndpointRouteBuilderExtensions.cs` to match how the repo already does this.

Test: **refuses without the API key**, returns empty when diagnostics is not registered rather than throwing, and returns a trace by id.

**Commit:** `feat(diagnostics): opt-in trace endpoint behind the API key`

---

## Part D: documentation

### Task D1

**Files:**
- Create or modify: a diagnostics section in `docs/guide/`
- Modify: `docs/reference/features.md`

Must state plainly:
- What enabling each `Capture*` flag puts in memory, and that mapping the endpoint puts it behind an HTTP route.
- **A trace may contain content the pipeline later removed**, because capture is deliberately not re-sanitised — the most common reason to open a trace is to see what a sanitiser did. Design §6.
- The memory arithmetic: `Capacity × (TopK + 1) × MaxCapturedCharacters` as a worst case the reader can check, not the word "bounded".
- That `IAuditLog` is the compliance-grade alternative, and why they are separate (design §3).

`features.md`: tick the row and correct the `**Package:**` line — capture and endpoint are two packages.

Every code sample must compile against the real API. Verify by pasting into a throwaway project with `ProjectReference`s and building, with a negative control (rename a member, confirm the compiler objects). Say how you verified.

**Commit:** `docs(diagnostics): document the trace viewer and what capture retains`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. All suites at or above baseline; report every count. `Rag.NET.Tests` must be **exactly 1308** with no test edited. B5 is *not* the only task touching existing production code, as the note under C1 already records: `RagPipeline` also gains the `ragnet.query` span that encloses retrieval and answer generation, without which nothing can tell when a query is over.
3. The four content-default mutations from B1, and the eviction mutation from A2.
4. No `#pragma`/`SuppressMessage` anywhere in the diff.
5. `docs/planning/ROADMAP.md` and `MILESTONE.md` flip to complete **after** the whole-phase review — both files, per the `73472b4` precedent.
