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
| `AuditChunkRef` (`DocumentId`, `ChunkIndex`, `Score`) | `src/Rag.NET.Security/Audit/AuditChunkRef.cs` | **reuse**, do not redefine |
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

`RagTrace` carries: `TraceId`, `StartedAt`, `Query` (nullable — only when content capture is on), `QueryHash` (always), `Chunks` (`IReadOnlyList<AuditChunkRef>` — **reused**, plus an optional parallel text list), `GuardActions`, `Stages`, `Prompt` (nullable), `Answer` (nullable).

`RagTraceOptions`: `Capacity` (default 50), `CaptureQueryText`, `CaptureChunkText`, `CapturePromptText`, `CaptureAnswerText` (**all default `false`**), `MaxCapturedCharacters` (default 4000, per field).

Every `Capture*` flag's XML must say what enabling it puts in memory, and reference `AuditLogOptions.LogQueryText`/`LogAnswerText` as the compliance-grade parallel. Validate `Capacity >= 1` and `MaxCapturedCharacters >= 0` in the initialisers, naming the property in the message (MA0015 forces `paramName` to be `value`).

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
    // Chunks are AuditChunkRefs, which carry no text at all; the text lives in the parallel
    // ChunkTexts list, null as a whole when CaptureChunkText is off.
    Assert.Null(trace.ChunkTexts);
    // Structure is still captured — that is the point of the default.
    Assert.NotEmpty(trace.QueryHash);
    Assert.Single(trace.Chunks);
}
```

**Verify by mutation**: flip each `Capture*` default to `true` in turn and confirm this test fails each time. Four mutations, four failures. Report them. This is the difference between a debugger and a data leak, and it is the one property in this phase that must not regress silently.

Also test: with flags on, text over `MaxCapturedCharacters` is truncated and the truncation is visible (not silent) — a trace that looks complete but is not would mislead exactly when someone is debugging.

**Commit:** `feat(diagnostics): trace collector with a single content gate`

### Task B2: stage timings from the existing spans

**Files:**
- Create: `src/Rag.NET.Diagnostics/Internal/StageActivityListener.cs`
- Test: `tests/Rag.NET.Diagnostics.Tests/StageActivityListenerTests.cs`

Subscribe an `ActivityListener` to the source named `Rag.NET` and record each `ragnet.*` span's name, duration and `TraceId` into the collector.

**`RagTelemetry` is `internal`**, so the source name cannot be referenced from another assembly. Either hard-code `"Rag.NET"` with a comment pointing at `RagTelemetry.SourceName`, or make that constant public. **Prefer hard-coding with the comment** — making an internal telemetry detail public to satisfy a listener is a larger commitment than it looks, and Phase 4.4 owns the OTel surface.

Tests need a real `Activity`, which requires a listener with `Sample = () => ActivitySamplingResult.AllData` — otherwise `StartActivity` returns `null` and the test passes vacuously. **Assert that the activity was actually created** before asserting on what was recorded.

**Commit:** `feat(diagnostics): collect stage timings from the existing spans`

### Task B3: retrieval and answer capture

**Files:**
- Create: `src/Rag.NET.Diagnostics/DiagnosticsRetrievalBehavior.cs`, `DiagnosticsAnswerEngineDecorator.cs`
- Test: matching test files

Mirror `AuditRetrievalBehavior` and `AuditAnswerEngineDecorator` closely — same swallow-and-log posture, same `LoggerMessage` source-gen. Correlate on `Activity.Current?.TraceId`, not a generated GUID.

**When there is no current `Activity`, capture nothing and do not throw.** A pipeline running without a listener is normal; that must be a silent no-op rather than a crash or a fabricated id.

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

**Commit:** `feat(diagnostics): record what guards and sanitisers removed`

### Task B5: prompt capture

**Files:**
- Modify: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`
- Create: the seam abstraction in `src/Rag.NET.Abstractions/`

The invasive part. `ChatAnswerEngine` assembles the prompt and nothing observes it. Add the **smallest possible** seam — an optional sink the engine calls with the assembled prompt, defaulting to nothing.

Keep the change to `ChatAnswerEngine` under ten lines and behaviour-identical when no sink is registered. **`Rag.NET.Tests` must stay at 1308 with no test edited**; if a test needs changing, the seam is too invasive — stop and reconsider.

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
2. All suites at or above baseline; report every count. `Rag.NET.Tests` must be **exactly 1308** with no test edited — B5 is the only task touching existing production code.
3. The four content-default mutations from B1, and the eviction mutation from A2.
4. No `#pragma`/`SuppressMessage` anywhere in the diff.
5. `docs/planning/ROADMAP.md` and `MILESTONE.md` flip to complete **after** the whole-phase review — both files, per the `73472b4` precedent.
