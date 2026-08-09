---
id: diagnostics
title: Pipeline Debugger
sidebar_position: 8
---

# Pipeline Debugger

*"Why did this query give a bad answer?"* is normally answered by adding log statements and running it again. `Rag.NET.Diagnostics` answers it from what already happened: a **disposable in-memory trace** of the last N query executions — which chunks came back and what they scored, how long each stage took, which guards and sanitisers fired and what each one removed, and — only if you ask for it — the query, the chunk text, the assembled prompt and the answer.

A trace is disposable by construction. It lives in a ring buffer, the oldest is evicted when the buffer is full, and everything is gone on restart. If you need a record that must survive, you want [`IAuditLog`](#not-an-audit-log) instead.

## Packages

```bash
dotnet add package Rag.NET.Diagnostics            # capture, and reading traces in-process
dotnet add package Rag.NET.Diagnostics.AspNetCore # the opt-in HTTP endpoint
```

The second is only needed if you want to read traces over HTTP. Capture works on its own in a console app, a worker, or a test.

## Registration

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Diagnostics;

services.AddRagNet(rag =>
{
    // ... the rest of your pipeline: chat client, embedding generator, vector store

    // Last, so it decorates the guards and sanitisers registered above.
    rag.AddRagDiagnostics();
});
```

Two things about that call:

- **It goes last.** It decorates the `IRetrievalGuard`, `IChunkSanitiser`, `IQuerySanitiser` and `IAnswerEngine` registrations that exist *at the time of the call* — the same ordering rule `ConfigureResilience` and `UseRateLimiting` carry. A guard registered afterwards still runs; it is simply not traced, and its absence from a trace would then read as *"it never fired"*, which is a wrong answer to the question traces exist to answer.
- **Finding nothing to decorate is a no-op, not a failure.** A pipeline with no guards and no sanitisers is an ordinary pipeline. Diagnostics does not require `Rag.NET.Security` to be installed.

`AddRagNet` must have been called — `AddRagDiagnostics` throws `InvalidOperationException` naming it otherwise, because the retrieval behavior it inserts needs the `RetrievalPipelineBuilder` that `AddRagNet` puts in the collection.

## What capture retains — and what each `Capture*` flag adds

**Every `Capture*` flag defaults to `false`.** Registering diagnostics captures **structure**, and structure only:

| Always captured | Field |
|---|---|
| The `Activity` trace id the execution ran under | `RagTrace.TraceId` |
| When it started | `RagTrace.StartedAt` |
| A SHA-256 of the query — so repeated questions are identifiable without retaining any of them | `RagTrace.QueryHash` |
| Which chunks came back, from which document, at which index, and what they scored | `RagTrace.Chunks` — `DocumentId`, `ChunkIndex`, `Score` |
| Which stages ran and how long each took | `RagTrace.Stages` — `ragnet.query`, `ragnet.retrieve`, `ragnet.ask`, and the ingestion spans |
| Which guards and sanitisers ran, how many results or characters went in and came out, and whether anything changed | `RagTrace.GuardActions` — `Component`, `InputCount`, `OutputCount`, `Changed` |

Capturing the **text** takes a further explicit flag per field, because *"turn on debugging"* must never silently mean *"start retaining customer documents and user questions in process memory"*.

| Flag | What enabling it puts in process memory |
|---|---|
| `CaptureQueryText` | The raw text of every traced user question: `RagTrace.Query`, **and** the `InputText`/`OutputText` of every **query sanitiser** action — a query sanitiser's input *is* the question as typed. |
| `CaptureChunkText` | The body text of every retrieved chunk — that is, your indexed documents — as `TraceChunk.Text`, **and** the `InputText`/`OutputText` of every **retrieval guard** and **chunk sanitiser** action. The largest of the four by far, and the one most likely to hold something confidential. |
| `CapturePromptText` | The assembled prompt as `RagTrace.Prompt`. It contains the question and the retrieved chunks together, so this retains what the two flags above retain combined — in one field, counted against the cap once rather than per chunk. |
| `CaptureAnswerText` | The model's reply as `RagTrace.Answer`, which is derived from your documents and can quote them at length. |

**Which flag governs a guard action depends on which kind of component produced it**, not on the fact that it is a guard action. The producer declares its content kind (`TraceContentKind`) and the collector applies the matching flag. That distinction is load-bearing: gating every action on `CaptureChunkText` — which an earlier revision did — meant enabling chunk text silently started retaining user questions with `CaptureQueryText` still off.

A `null` text field means *the flag was off*, never *the value was empty*. That is why they are nullable rather than defaulted to `""`.

```csharp
services.AddRagNet(rag => rag.AddRagDiagnostics(o =>
{
    o.Capacity = 50;                // default: how many executions are retained
    o.MaxCapturedCharacters = 4000; // default: per captured field, not per trace

    o.CaptureQueryText = true;      // the question as typed
    o.CaptureChunkText = true;      // your indexed document text
    o.CapturePromptText = true;     // question + chunks, as sent to the model
    o.CaptureAnswerText = true;     // the model's reply
}));
```

`MaxCapturedCharacters` applies **per field** — each chunk's text is its own field — and truncation is made visible rather than silent: a cut value ends in `RagTraceOptions.TruncationMarker` (`…[truncated]`). A field ending in the marker is a prefix of the real value; a field without it is the whole thing. A trace that looked complete but was not would mislead exactly when someone is reading it to find out why an answer was wrong.

```csharp
bool promptWasCut =
    trace.Prompt?.EndsWith(RagTraceOptions.TruncationMarker, StringComparison.Ordinal) == true;
```

Setting `MaxCapturedCharacters = 0` leaves the flags on and captures no characters — the field becomes the marker alone, which still says *"there was text here"* where `null` would have said *"the flag was off"*. It is a way to confirm the wiring without retaining anything.

## Reading traces in-process

`ITraceStore` is the read side. A trace appears in it when the **outermost `ragnet.*` span of the execution stops** — `ragnet.query`, which every public pipeline entry point opens, including `RetrieveAsync` — which is the point at which every part of it has been recorded.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Diagnostics;

var store = provider.GetRequiredService<ITraceStore>();

foreach (RagTrace trace in store.Snapshot())   // newest first
{
    Console.WriteLine($"{trace.TraceId}  {trace.StartedAt:O}  query {trace.QueryHash[..8]}");

    foreach (TraceStage stage in trace.Stages)
        Console.WriteLine($"  {stage.Name,-16} {stage.Duration.TotalMilliseconds:F1} ms");

    foreach (TraceChunk chunk in trace.Chunks)
        Console.WriteLine($"  {chunk.DocumentId}#{chunk.ChunkIndex} scored {chunk.Score:F3}");

    foreach (TraceGuardAction action in trace.GuardActions)
    {
        Console.WriteLine(
            $"  {action.Component}: {action.InputCount} in, {action.OutputCount} out, " +
            $"changed: {action.Changed}");
    }
}
```

`Snapshot()` is a point-in-time copy, not a live view: a reader walking it never observes traces committed while it iterates, which is what makes it safe to read while the pipeline keeps serving.

```csharp
if (store.TryGet(traceId, out RagTrace? trace))
{
    // null means the flag was off, not that the field was empty.
    Console.WriteLine(trace.Query ?? "(CaptureQueryText is off)");
    Console.WriteLine(trace.Prompt ?? "(CapturePromptText is off)");
    Console.WriteLine(trace.Answer ?? "(CaptureAnswerText is off)");

    foreach (TraceChunk chunk in trace.Chunks)
        Console.WriteLine(chunk.Text ?? "(CaptureChunkText is off)");
}
```

`TryGet` returns `false` for an id that was never seen **and** for one that has since been evicted; the caller cannot distinguish them and does not need to. Retention is bounded by `Capacity`, so an id that was readable a hundred queries ago may not be now.

### `Changed` is not `InputCount != OutputCount`

A guard drops results, so its counts move. A **sanitiser rewrites text in place**, so its counts are characters in and characters out and can stay equal while everything between them changed — a sanitiser swapping one word for another of the same length is the obvious case. `Changed` is the signal; the counts are the size of the effect.

A component that ran and did nothing is still recorded. *"The guard ran and let everything through"* and *"the guard never ran"* are different answers, and only the first produces a `TraceGuardAction`.

## What it costs to have on

### Enabling diagnostics changes the pipeline's cost profile

`AddRagDiagnostics` subscribes an `ActivityListener` to the `Rag.NET` `ActivitySource` with `AllData` sampling. That is what makes stage latencies available — an unsampled `StartActivity` returns `null` and there is nothing to time — and it has a consequence worth stating plainly:

> **The pipeline's spans start being *created* even when no exporter is configured.** Before diagnostics is registered, a pipeline with no OpenTelemetry pipeline set up allocates no `Activity` at all. After it, every `ragnet.*` span is materialised on every query. This is unavoidable rather than a tuning choice. It affects only the `Rag.NET` source, and only while diagnostics is registered.

If you are already exporting spans with OpenTelemetry, you are paying this cost already and diagnostics adds nothing on top of it. If you are not, this is the price of the latency breakdown.

### The memory arithmetic

Memory is bounded by `Capacity` **and** `MaxCapturedCharacters` together. Without the second, a large capacity quietly means tens of megabytes of document text. The worst case, with every `Capture*` flag on, is:

```text
Capacity × (TopK + 3 + 2 × Components) × MaxCapturedCharacters   characters
```

counting each capped field once:

- `TopK` chunk texts;
- **three** single fields — `Query`, `Prompt` and `Answer`;
- **two per guard or sanitiser** — every `TraceGuardAction` holds an `InputText` *and* an `OutputText`, each capped separately, and a retrieval guard's pair holds all `TopK` chunk texts joined together. `Components` is how many `IRetrievalGuard`, `IQuerySanitiser` and `IChunkSanitiser` implementations are registered, so a guard-and-sanitiser chain of four adds **eight** capped fields per trace — for the default `TopK`, more text than the chunks themselves.

Two adjustments to make the figure real:

- **Characters are UTF-16, so bytes are twice the number above.**
- A truncated field carries `TruncationMarker` on top of the cap, so the true per-field maximum is `MaxCapturedCharacters + TruncationMarker.Length`.

Structural fields are not in the arithmetic because they do not scale with content — ids, scores and stage latencies are tens of bytes each, and `QueryHash` is 64 characters per trace whatever the flags say.

Worked example, at `Capacity = 50`, `TopK = 5`, `MaxCapturedCharacters = 4000` (all defaults), every flag on and four guards or sanitisers registered:

| Setup | Characters | Bytes |
|---|---|---|
| Every flag on, four components | `50 × (5 + 3 + 8) × 4000 = 3,200,000` | ≈ **6.4 MB** |
| Every flag on, no guards or sanitisers | `50 × 8 × 4000 = 1,600,000` | ≈ **3.2 MB** |
| Every flag off — the default | none of this is retained | — |

> Earlier drafts of this figure read `Capacity × (TopK + 1) × MaxCapturedCharacters`. That omitted the query, the answer and every guard action, and anyone sizing `Capacity` from it would have under-budgeted several times over. The formula above is the one the code implements.

There is a second bound you will not normally see. A trace is started by the first thing that records into it and removed when it commits, so a request that fails in between would sit in the in-flight map forever — the ring buffer's own bound defeated one level up. The collector caps in-flight traces at four times `Capacity` and declines to start new ones past it, logging at `Debug` when it does.

## A trace can hold content the pipeline removed

Captured content is **not** passed back through `PiiChunkSanitiser`, `RegexChunkSanitiser` or any other redaction.

This looks wrong and is deliberate. The sanitisers run *inside* the pipeline, so a trace that recorded post-sanitiser state would show only what the sanitiser let through — and **the commonest reason to open a trace is to find out what a sanitiser or a guard did**. Redacting the capture would destroy the thing tracing was turned on to see.

The consequence is documented rather than mitigated:

> **A trace may contain content the pipeline itself later removed** — PII a sanitiser stripped, a chunk an RBAC guard dropped before the answer engine ever saw it. That is a reason to keep content capture off in production, which is already the default.

## The HTTP endpoint

`MapRagNetTrace()` is **explicit and never automatic.** Mapping it puts whatever the `Capture*` flags retained — the user's question, your document text, the assembled prompt, the answer, including anything a sanitiser stripped — behind an HTTP route. That is a decision an application makes on purpose, in one visible line, not something a package does for it because a reference happened to be present.

```csharp
using Rag.NET.Api.Authentication;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Diagnostics.AspNetCore;

builder.Services.Configure<ApiKeyOptions>(o => o.ApiKeys = ["your-api-key"]);

var app = builder.Build();

app.UseRagNetApiAuthentication();  // ApiKeyMiddleware, from Rag.NET.Api

// GET /ragnet/traces           — a summary per retained trace, newest first
// GET /ragnet/traces/{traceId} — the whole trace, captured text included
app.MapRagNetTrace();

app.Run();
```

**Authentication comes from the application, not from the endpoint.** `ApiKeyMiddleware` guards the whole pipeline: every path is checked unless it starts with one of `ApiKeyOptions.ExemptPathPrefixes`, which is how the webhook route — authenticated by an HMAC signature over its own body instead — opts out. The trace routes carry no alternative authentication of their own, so:

- Call `app.UseRagNetApiAuthentication()` with at least one API key configured, and the routes are behind it. The middleware fails closed: behind it with no keys configured and no explicit `AllowAnonymous` opt-out, every request is refused with `401` rather than served open.
- **Do not add the trace prefix to `ExemptPathPrefixes`.** Doing so serves captured content to anyone.
- Map these routes into an application with no authentication and they are as open as everything else in it.

A custom prefix, if the default collides with your own routes:

```csharp
app.MapRagNetTrace("/internal/ragnet/traces");
```

| Route | Returns |
|---|---|
| `GET {prefix}` | One `RagTraceSummary` per retained trace, newest first: id, start, query hash, chunk / guard-action / stage counts, the longest stage's duration, and `HasCapturedText`. **Never any captured text** — a fully-capturing buffer of 50 traces is megabytes of document text, and a list request should not be the way to get it. |
| `GET {prefix}/{traceId}` | The whole `RagTrace`, captured text included. Deliberately a second, explicit step. |

Mapping without `AddRagDiagnostics` is not an error: the list route answers with an empty list and a fetch answers `404`. A deployment that has left the route mapped with capture turned off should read as *"no traces"*, not as a 500 in someone's alerting. A fetch returns `404` for "never seen", "already evicted" and "capture is not registered" alike — the caller can do nothing different about any of them, and distinguishing them would tell a prober whether an id had ever existed.

## Not an audit log

`IAuditLog` in `Rag.NET.Security` is the **compliance-grade** record. It is the right tool whenever the answer to *"who saw what"* has to survive being asked months later. The two subsystems are deliberately separate, because they have opposite requirements:

| | Trace (`Rag.NET.Diagnostics`) | Audit log (`Rag.NET.Security`) |
|---|---|---|
| Purpose | A developer asking *"why was this answer wrong"* five minutes after the request | Proving later what the system did |
| Durability | In memory, last `Capacity`, gone on restart | Persisted — `SqliteAuditLog` — with retention |
| Losing an event | Acceptable; capture failures are swallowed and logged so the pipeline never breaks | Not acceptable |
| Content flags | `CaptureQueryText` / `CaptureAnswerText` … | `AuditLogOptions.LogQueryText` / `LogAnswerText` |
| Reachable over HTTP | Only if you map the endpoint | No |

The flag names are parallel on purpose — one concept under two prefixes rather than three unrelated words — and `TraceChunk` mirrors `AuditChunkRef`'s field names (`DocumentId`, `ChunkIndex`, `Score`) for the same reason. The **types** are not shared and neither is the assembly: `Rag.NET.Diagnostics` does not reference `Rag.NET.Security`, so a team that wants a debugger and has never enabled auditing is not handed SQLite and its native binaries, the ML tokenizers and their data file, Polly and protobuf to get one.

An audit log that developers toggle at will and read over an HTTP endpoint has stopped being an audit log. If you need both, register both.

## Limitations

- **A streamed prompt only correlates when the host supplies an ambient activity.** `ChatAnswerEngine.AskStreamingAsync` assembles the prompt *after* its first `yield return`, so the prompt observer runs on the **consumer's** execution context — where the spans the pipeline started inside its own iterator are not ambient. Under ASP.NET the request activity is the consumer's own and carries the trace id everything else joins on, so the prompt joins with it. In a console app or a test with no ambient activity of its own, a streamed prompt is not captured. **Only the prompt field is affected**: the chunks, the stage latencies and the commit are recorded on the pipeline's own context and are unaffected either way. The non-streamed path has no suspension between the span and the prompt and always captures.

- **Streamed answers are not recorded at all.** `DiagnosticsAnswerEngineDecorator` passes the streaming overload straight through, which is what the audit decorator does too: recording a streamed answer means buffering the whole thing before the caller has finished reading it, so the observer would change the memory profile of the stream it is observing. `RagTrace.Answer` is `null` for a streamed execution even with `CaptureAnswerText` on.

- **`IChunkSanitiser` runs at ingestion, not at query time.** `ChunkSanitiserBehavior` is an ingestion behavior, so a chunk sanitiser's actions land in whatever trace the **ingestion** spans ran under — a different trace from the query that later surfaces the chunk. It is still the answer to *"why does this chunk say [REDACTED]"*; it is just not found by looking up the query that returned it.

- **Correlation is `Activity.Current.TraceId`, and capture with no ambient activity is a silent no-op.** Not a crash, and not a fabricated id — fabricating one would turn every unjoined fragment into its own single-entry "trace" and fill the buffer with them. An activity predating the W3C id format is treated the same way, since its trace id is all zeroes and would merge every such execution into one trace. Inside the pipeline this is never the case, because the `ragnet.*` spans are themselves the ambient activity; the streamed prompt above is the one seam that can run outside them.

- **No UI, and no persistence.** The endpoint returns JSON. Traces are disposable; `SqliteAuditLog` is where durable records belong.

- **Ingestion is traceable but not the target.** The ingestion spans are recorded like any other stage, but the capture seams were built around the query path. Ingestion diagnostics can follow the same shape later.

- **The seams are not wired into `IAuditLog`.** The prompt observer and the guard/sanitiser decorators are shaped so the audit log could consume them, and nothing does yet — that is a change to a compliance path and deserves its own phase.
