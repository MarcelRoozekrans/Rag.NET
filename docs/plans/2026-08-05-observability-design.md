# Observability — Design (Phases 4.3 and 4.4)

**Date:** 2026-08-05
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. What the measurement changed

Both roadmap entries are one-liners, and measuring the code moved both.

**Phase 4.3 — "Consistent scoped/structured logging"** conflates one thing that is nearly finished
with one that has not started:

| Logging shape | Count |
|---|---|
| `[LoggerMessage]` source-generated | **140** |
| `ILogger.Log*` with structured templates | 12 |
| Plain string interpolation | **0** |
| `BeginScope` | **0** |

**Structured logging is ~92% done and there is no interpolation cleanup to run.** Scoped logging
does not exist at all. A reader of the one-liner would reasonably assume both were equally
unfinished; they are not.

**Phase 4.4 — "First-class OTel wiring … on top of the existing `RagTelemetry`"** starts from more
than nothing: **8 spans and 11 instruments already exist**, covered by 13 telemetry tests. What is
absent is the *wiring* — a repo-wide grep for `AddOpenTelemetry`, `AddSource(` and `AddMeter(`
returns **zero matches** outside two test projects pinning a transitive version.

## 1. Phase 4.3 — narrow, and it should say so

The phase's first act is **recording that the structured half is already true**, so nobody re-does
it. What remains is genuinely new:

- **Scopes.** `BeginScope` appears zero times. One scope per pipeline operation carrying the
  document or query identity, so every log inside inherits correlation instead of restating it.
- **Event-name standardisation** — currently PascalCase method names (`IngestFailed`,
  `QueryExpansionFailed`).
- **The named properties** `features.md` promises (`document_id`, `chunk_index`, `vector_store`,
  `strategy`) — message-template placeholders in some places, absent as log fields in others.

This is a smaller phase than the roadmap implies. Saying so is the honest outcome, not a
shortfall.

## 2. Phase 4.4 — semantic conventions where they fit

OTel's GenAI conventions cover LLM calls: model, token usage, operation. They have **no concept**
of chunking, retrieval or vector storage.

- **`gen_ai.*`** on the LLM surface — `ragnet.ask`, and `CostAccounting`'s token and cost counters.
- **`ragnet.*`** everywhere else, with names made internally consistent. They are currently a mix
  of dotted and snake_case (`document.id`, `top_k`, `query.hash`, `synthesis.strategy`).

**Stated rather than discovered later: OTel's GenAI semantic conventions are experimental.**
Adopting them means tracking a spec that may move. The design **pins the semconv version it
targets and records it**, so future drift is a known upgrade rather than a mystery.

**This renames tags that 13 existing tests assert** across `AskTelemetryTests`,
`RetrieveTelemetryTests` and `IngestTelemetryTests`. They are updated as a stated consequence — not
adjusted until green.

Nothing is published, so the rename costs nothing now and becomes permanent at 6.3 — the same
window that made Phases 4.7 and 4.8 cheap.

## 3. Tracing every package, without any package gaining a dependency

The 2026-04-04 design deferred package-specific spans "until evidence demands it". **That was
written when the library was a fraction of its current size, and the evidence now exists**: a user
seeing slow retrieval gets one generic `ragnet.retrieve` span and a `vector_store` tag holding a
type name. They cannot tell whether the store, the reranker or graph traversal is the cost.

**Nine packages are untraced**: the six vector stores, both rerankers, GraphRAG, RAPTOR, Graph,
Security and Caching.

The obstacle was real — `RagTelemetry` is `internal` to core, and `ShadowTelemetry` documents
exactly this, having built its own meter *"because `RagTelemetry` is internal to `Rag.NET` and this
package must not depend on core."*

**The escape hatch already exists in this repository.** Three packages link shared source files
today (`GraphErrorMapping.cs` into Microsoft365, `BertWordPieceTokenization.cs` into both ONNX
packages) via `<Compile Include="..\Shared\X.cs" Link="…" />`. The `ActivitySource` moves to
`src/Shared/` and is linked into each package that traces.

Each assembly gets its own `ActivitySource` instance **sharing the name `"Rag.NET"`**, which is
normal — OTel matches sources by name and listens to all of them. So consumers still write one
`AddSource`, and **no package gains a project reference or a NuGet dependency**.

### `ZeroAlloc.Telemetry` was evaluated and rejected — with evidence

The org already supplies ten of this repository's dependencies and publishes
**`ZeroAlloc.Telemetry`**: source-generated OTel instrumentation, `[Instrument]` on an interface
plus `[Trace]`/`[Count]`/`[Histogram]` on methods, generating a proxy. Zero transitive NuGet
dependencies, net10, genuinely zero marginal allocation. It was measured against this phase rather
than adopted on convention.

**It cannot set span tags at all.** Its entire API is four attributes, none parameter-targeted, and
no tag-setting code is ever emitted. **This phase exists for the tags** — of the 13 traced units
examined, **zero** have all their wanted tags settable by the proxy.

Three structural mismatches beyond that:

- **The generated proxy is `internal sealed`**, constructible only from the assembly declaring the
  interface. `IVectorStore` lives in `Rag.NET.Abstractions`, so the annotation would land *there* —
  the most foundational assembly taking a new dependency, **inverting what Phase 4.7 achieved**.
  (Only four of the six store packages even have `InternalsVisibleTo` from Abstractions today.)
- **GraphRAG and RAPTOR have no package-specific interface** — they implement the generic
  `IIngestionBehavior`/`IRetrievalBehavior` shared by ~30 implementations, so one `[Trace]` name
  would cover all of them indistinguishably.
- **Caching has no interface or class at all**; `UseCaching()` only registers `HybridCache`.

**The probe validated two assumptions this design rests on**, which is worth as much as the
rejection:

- **Cross-assembly source-name sharing works** — one listener on `"Rag.NET"` received spans from
  two independently-generated `ActivitySource` instances in separate assemblies. The linked-file
  approach in §3 is proven before it is built.
- **Measured allocation, 200k calls, Release, no listener**: bare call **72 B**, hand-written no-op
  decorator **144 B**, generated proxy **144 B**. `StartActivity` allocates **zero** when
  unobserved; the extra cost is *decorator-shaped*, not telemetry-shaped. **Spans placed inside
  existing methods — this design — are therefore cheaper than any proxy approach**, and the
  existing `TelemetryOverheadBenchmarks` zero-overhead property is preserved.

## 4. `Rag.NET.Telemetry`, following the 4.7 pattern

A satellite package holding `AddRagNetInstrumentation()` for tracing and metrics. Core keeps
depending only on `System.Diagnostics` primitives, exactly as Phase 4.7 arranged.

**Its real value is the trap it closes.** A consumer wiring `.AddMeter("Rag.NET")` today silently
misses everything `ShadowTelemetry` publishes under **`Rag.NET.Evaluation`** — a second meter no
document mentions. The extension registers both, plus resource attributes, which exist nowhere
today.

## 5. Documentation

`docs/reference/opentelemetry.md`'s metrics table lists **8 of the 11** instruments — it predates
`ragnet.ratelimit.wait.duration`, `ragnet.llm.tokens` and `ragnet.llm.cost`, and contradicts
`features.md`, which is correct.

Fixed, **with a guard test asserting the documented instrument list matches `RagTelemetry`**, so it
cannot drift again. A doc that disagrees with the code is this repository's most-repeated defect;
this one has already drifted once.

Plus the sample dashboard 4.4's own description promises and which exists nowhere.

## 6. Scale, stated plainly

Instrumenting nine packages is a large phase — each needs spans, tags, tests, and a naming decision
consistent with the others. It is deliberately one phase rather than two, because splitting it
would mean deciding the span-naming convention twice and living with half-instrumented traces in
between.

## 7. Out of scope

- **Converging `ShadowTelemetry` onto the shared source.** The linked file would now make it
  possible, but renaming its meter is a breaking change for anyone already scraping
  `Rag.NET.Evaluation`, and it works correctly today. Recorded as a follow-up.
- **Log-to-trace correlation beyond what the OTel logging provider gives for free.** Emitting trace
  ids into log messages by hand duplicates what the provider already does.
