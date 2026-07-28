# Pipeline Debugger / Trace Viewer — Design (Phase 3.4)

**Date:** 2026-07-28
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.4
**Covers:** the `Pipeline Debugger / Trace Viewer` row in `features.md`

## Most of this already exists, in two places

There is no `Rag.NET.Diagnostics` package and no `RagDebugMiddleware` — verified across every `.cs`
file, not assumed. But the feature as specified is largely already built, split between two
subsystems nobody has joined.

| `features.md` asks for | Already exists |
|---|---|
| which chunks were retrieved | `AuditChunkRef` — `DocumentId`, `ChunkIndex` |
| their scores | `AuditChunkRef.Score` |
| latency breakdown per stage | `RagTelemetry` spans: `ragnet.{ingest,parse,chunk,embed,store,retrieve,ask}` |
| correlation across a request | `AuditRetrievalEvent.RequestId` + `AuditCorrelationContext` |
| content behind an opt-in | `AuditLogOptions.LogQueryText` / `LogAnswerText`, both default `false` |

Building a fresh capture system alongside these would duplicate a retrieval-recording path that
already works. That is the mistake Phases 3.1 and 3.2 spent their time undoing: the same JSON-parse
defect existed twice in the RAGAS metrics **because the plumbing had been copied**, and 3.2's whole
structural fix was moving the shared caller down rather than making a second copy.

## 1. What is genuinely missing

- **The assembled prompt.** *"What the answer engine received"* is captured nowhere. `ChatAnswerEngine`
  builds it and nothing observes it.
- **Guard and sanitiser actions.** `RbacRetrievalGuard`, `TrustLevelRetrievalGuard`,
  `RegexRetrievalGuard`, `PiiChunkSanitiser`, `RegexChunkSanitiser` and the query sanitisers all
  exist and all silently change what the pipeline saw. **Nothing records that one fired or what it
  removed**, so *"why is that chunk missing from the answer"* is currently unanswerable. This is the
  real diagnostic hole, and it is the question people actually ask.
- **Chunk text.** `AuditChunkRef` carries no text, deliberately.
- **The join.** Spans hold timings, audit events hold content, and they share no key —
  `RequestId` is not `TraceId`.
- **A disposable last-N view.** The audit log persists to SQLite. A debugger wants a ring buffer.

## 2. Scope decisions (agreed)

1. **A separate `Rag.NET.Diagnostics`**, reusing the audit types and following its naming, rather
   than extending `IAuditLog`.
2. **Capture off by default; content behind a further explicit opt-in.**
3. **The endpoint is a separate, explicit registration**, protected by the existing
   `ApiKeyMiddleware`.

## 3. Why not simply extend the audit log

It is the DRYer option and it was seriously considered. The objection is that the two have
**opposite requirements**:

An audit trail is a compliance record. It must not lose events, it needs retention and integrity,
and it is written for someone who may later have to prove what happened. A debug trace is
disposable by construction — last N, in memory, dropped on restart — and is read by a developer
five minutes after the request.

An audit log that developers toggle at will and read over an HTTP endpoint has stopped being an
audit log. So the systems stay separate, and the sharing happens at the level where sharing is
correct: **the same types, the same vocabulary, and the two new capture seams built once for both.**

## 4. Assembling a trace from what is already there

Three sources, none of them new machinery:

- **Timings** — an `ActivityListener` over the `Rag.NET` `ActivitySource`. All seven stage spans
  already exist, so the latency breakdown costs nothing beyond subscribing.
- **Retrieval** — a behavior mirroring `AuditRetrievalBehavior`, reusing `AuditChunkRef` rather
  than inventing a parallel shape for the same three fields.
- **Answer** — a decorator mirroring `AuditAnswerEngineDecorator`.

Joined by `TraceId` from `Activity.Current`, which gives the debugger the correlation the audit log
gets from `RequestId`.

### The two new seams

**Prompt capture** needs a seam in `ChatAnswerEngine`, which is the invasive part of this phase and
should stay as small as possible.

**Guard and sanitiser actions** are captured by **decorating** `IRetrievalGuard`, `IChunkSanitiser`
and `IQuerySanitiser` — all three interfaces exist — so no existing implementation changes. A
decorator can see what went in and what came out, which is exactly what "what did the guard remove"
means.

Both seams are built for the diagnostics package but shaped so the audit log can consume them
later. Neither is wired into `IAuditLog` in this phase.

## 5. Content capture, and the posture it inherits

Capture is off unless registered. Registering captures **structure**: chunk ids, scores, stage
latencies, which guards fired and how many chunks each removed.

Capturing the **text** — the query, the chunk contents, the assembled prompt, the answer — requires
further explicit flags. `AuditLogOptions` already expresses this idea as `LogQueryText` and
`LogAnswerText`; the diagnostics options use `Capture*` names and **document the parallel**, so a
reader meets one concept under two prefixes rather than three unrelated words.

The distinction is the phase's whole safety story. *"Turn on debugging"* must not silently mean
*"start retaining customer documents in memory and serving them over HTTP"*.

## 6. Deliberately not sanitised

Captured content is **not** passed through `PiiChunkSanitiser` or any redaction.

This looks wrong and is not. The sanitisers run *inside* the pipeline, so a trace that captures
post-sanitiser state shows only what the sanitiser let through — and the most common reason to open
a trace is to find out what a sanitiser or guard did. Redacting the capture would destroy the thing
tracing was turned on to see.

The consequence is documented rather than mitigated: **a trace may contain content the pipeline
itself later removed.** That is a reason to keep content capture off in production, which is
already the default.

## 7. Bounded memory

A ring buffer bounded by trace count **and** a per-field character cap. Without the second, a
capacity of 1000 silently means tens of megabytes of document text — `TopK` chunks plus a prompt per
trace, each potentially thousands of characters.

The worst case is stated in the docs as an arithmetic the reader can check, not as "bounded".

## 8. Testing

- **The ring buffer** — pure, table-tested: eviction order, capacity, behaviour under concurrent
  writes.
- **Content is off by default**, and **flipping that default must fail a test.** Everything else in
  this phase is convenience; this one is the difference between a debugger and a data leak. It gets
  a mutation check, not just an assertion.
- **The join** — a trace assembles correctly from spans plus payload events under a shared
  `TraceId`, including when a stage is missing.
- **Guard and sanitiser capture** — a decorated guard that drops two chunks is recorded as having
  dropped two chunks.
- **The endpoint** — refuses without the API key, and returns nothing when capture is not
  registered.

## 9. Documentation

`docs/guide/` gains a diagnostics section stating plainly what enabling content capture does: it
puts user queries, document text and assembled prompts in process memory and, if the endpoint is
mapped, behind an HTTP route. It should name the audit log as the compliance-grade alternative, and
say that a trace may contain pre-sanitiser content.

`features.md`: tick the row, and correct the `**Package:**` line — capture and endpoint are two
packages.

## Out of scope

- **A UI.** The endpoint returns JSON. A viewer is a separate concern and nobody has asked for one.
- **Persistence.** Traces are disposable; `SqliteAuditLog` is where durable records belong.
- **Wiring the new seams into `IAuditLog`.** They are shaped so it can be done, and doing it is a
  change to a compliance path that deserves its own phase.
- **Ingestion traces.** The spans exist, but the debugging question this phase answers is about
  query-time behaviour. Ingestion diagnostics can follow the same shape later.
