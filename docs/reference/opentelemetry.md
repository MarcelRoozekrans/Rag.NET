# OpenTelemetry Integration

Rag.NET emits traces and metrics using the in-box `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics` APIs. Core and every instrumented satellite package take no OpenTelemetry SDK dependency themselves — instrumentation is always active and is zero-overhead when no listener is attached. No opt-in call inside `AddRagNet(...)` is needed either; spans and metrics simply exist, waiting for a listener.

---

## Quick Setup: `AddRagNetInstrumentation()`

The `Rag.NET.Telemetry` package is the one-call setup. It wires the OpenTelemetry SDK onto every signal Rag.NET actually emits — the shared `"Rag.NET"` `ActivitySource`, **both** meters (see below), and the resource attributes that identify this instrumentation to a backend:

```csharp
// dotnet add package Rag.NET.Telemetry
builder.Services.AddRagNetInstrumentation()
    .WithTracing(t => t.AddOtlpExporter())   // or AddConsoleExporter(), AddZipkinExporter(), etc.
    .WithMetrics(m => m.AddOtlpExporter());
```

`AddRagNetInstrumentation()` returns the `OpenTelemetryBuilder`, so you chain exporters onto it exactly as you would `AddOpenTelemetry()` — it registers no exporter of its own.

### The trap this closes: two meters, not one

Hand-wiring the SDK yourself is possible without taking a dependency on `Rag.NET.Telemetry` — `AddSource("Rag.NET")` / `AddMeter("Rag.NET")` on your own `AddOpenTelemetry()` call reaches core and every satellite's spans and the `"Rag.NET"` meter. But it **silently misses every counter `Rag.NET.Evaluation`'s shadow-capture pipeline publishes** under a second, easy-to-miss meter name, `"Rag.NET.Evaluation"` (see [Two meters](#two-meters-ragnet-and-ragnetevaluation) below). `AddRagNetInstrumentation()` registers both names so this cannot happen by omission; hand-wiring both names yourself works identically if you would rather avoid the package dependency:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Rag.NET")
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter("Rag.NET")
        .AddMeter("Rag.NET.Evaluation")
        .AddOtlpExporter());
```

### Resource attributes

`AddRagNetInstrumentation()` sets two resource attributes identifying the instrumentation distribution that configured a signal — the OpenTelemetry Semantic Conventions' pair for exactly that purpose, as distinct from `telemetry.sdk.*` (the bare SDK, set by the SDK itself) and `service.*` (your application's own identity, which this call deliberately never sets):

| Attribute | Value |
|---|---|
| `telemetry.distro.name` | `"Rag.NET"` |
| `telemetry.distro.version` | `"1.0.0"` (pinned independently of the `Rag.NET.Telemetry` package's own NuGet version) |

Hand-wiring the SDK yourself does not set these — only `AddRagNetInstrumentation()` does.

---

## Spans (Traces)

| Span name | Emitted by | Key attributes |
|---|---|---|
| `ragnet.ingest` | Top-level ingest call | `document.id`, `content.type`, `chunk.count` |
| `ragnet.parse` | Document parser behavior | `document.id`, `parser.type`, `section.count`, `chunk.count` |
| `ragnet.chunk` | Chunking behavior | `document.id`, `chunk.count` |
| `ragnet.embed` | Embedding behavior | `document.id`, `chunk.count` |
| `ragnet.store` | Vector store write behavior | `document.id`, `chunk.count`, `vector.store` |
| `ragnet.query` | `IRagPipeline.RetrieveAsync`, `AskAsync`, `AskStreamingAsync` | *(none)* |
| `ragnet.retrieve` | Top-level retrieval call | `query.hash`, `top.k`, `result.count` |
| `ragnet.ask` | Answer generation engine | `source.count`, `synthesis.strategy`, `gen_ai.*` (see below) |

### GenAI semantic-convention attributes on `ragnet.ask`

`ragnet.ask` additionally carries the subset of [OpenTelemetry's GenAI semantic conventions]
(https://github.com/open-telemetry/semantic-conventions/blob/v1.41.0/docs/gen-ai/gen-ai-spans.md)
that genuinely applies to a chat completion call, pinned against **v1.41.0** — the last version
tagged in `open-telemetry/semantic-conventions` before `gen_ai.*` moved to the dedicated
`semantic-conventions-genai` repository, which has not cut a release of its own to pin against
instead. Every `gen_ai.*` attribute is `Development`-stability in that revision and may be renamed
by a future spec update; re-check the pin (`ChatAnswerEngine.TagGenAi`'s remarks) before assuming
a name below still matches upstream.

| Attribute | Always set? | Source |
|---|---|---|
| `gen_ai.operation.name` | Yes, `"chat"` | Constant — `ChatAnswerEngine` only performs chat completions. |
| `gen_ai.request.stream` | Only on `AskStreamingAsync`, `true` | Which overload was called. |
| `gen_ai.provider.name` | Only when the wrapped `IChatClient` exposes it | `chatClient.GetService<ChatClientMetadata>()?.ProviderName` |
| `gen_ai.request.model` | Only when the wrapped `IChatClient` exposes it | `chatClient.GetService<ChatClientMetadata>()?.DefaultModelId` |
| `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` | Only on `AskAsync`, when the provider reports usage | `ChatResponse.Usage` |

`ChatClientMetadata` is frequently unavailable — a bare test double, or a provider that does not
implement `GetService`, returns `null` — in which case the corresponding attribute is left unset
rather than fabricated. `gen_ai.usage.*` is span-level and only captured on the non-streaming path,
where `ChatResponse.Usage` is a single already-in-scope property; the streaming path would need to
scan every update for a trailing `UsageContent`, which is `CostTrackingChatClient`'s job for the
cost ledger, not this span's.

`source.count` and `synthesis.strategy` stay `ragnet.*` deliberately: OpenTelemetry's GenAI
conventions have no equivalent for "how many retrieved sources fed the prompt" or "which RAG
synthesis strategy combined them" — those are RAG concepts, not LLM-call concepts, and a
plausible-looking `gen_ai.rag.*` name would misrepresent them as standardized when they are not.
Similarly, `ragnet.llm.cost` (see Metrics below) has no GenAI-metric equivalent — the spec defines
no cost/spend metric at all — so it stays `ragnet.*` in its entirety.

The ingest spans are nested under their parent, so a single `IngestAsync` call produces a tree:

```
ragnet.ingest
  ragnet.parse
  ragnet.chunk
  ragnet.embed
  ragnet.store
```

Query spans are nested too. Every public `IRagPipeline` query method opens `ragnet.query` around its whole execution, and `ragnet.retrieve` and `ragnet.ask` are children of it:

```
ragnet.query
  ragnet.retrieve
  ragnet.ask
```

A retrieve-only call produces the same tree without the `ragnet.ask` child:

```
ragnet.query
  ragnet.retrieve
```

`ragnet.query` carries no attributes. It exists so that the spans of one query share a trace id in **every** host — without it, `PipelineRetriever.RetrieveAsync` and `ChatAnswerEngine.AskAsync` each start a top-level span from the ambient `Activity` context, which makes them two unrelated roots in a console app or a worker and correlated siblings only under ASP.NET, where a request activity happens to parent both. It also gives a query one unambiguous end: `ragnet.query` stopping.

When the caller holds an ambient span, the whole tree hangs beneath it:

```
[caller span]
  ragnet.query
    ragnet.retrieve
    ragnet.ask
```

A retriever that fans a query out — `DeepResearchRetriever`, registered whenever `DeepResearchOptions` is configured — opens `ragnet.retrieve` once per sub-question, and all of them are children of the same `ragnet.query`:

```
ragnet.query
  ragnet.retrieve   (primary)
  ragnet.retrieve   (sub-question 1)
  ragnet.retrieve   (sub-question 2)
```

Multi-query (`RetrievalOptions.UseMultiQuery`) fans out too, but it does so as a *behavior* inside the retrieval chain, below the one span `PipelineRetriever` opens. It produces a single `ragnet.retrieve` however many variants it searches with.

> **Changed in the pipeline debugger release.** This page previously stated that `ragnet.retrieve` and `ragnet.ask` are *not* nested inside each other and appear as siblings. That is no longer true: `ragnet.query` was added as their parent, and it is emitted on the `Rag.NET` source like every other span here, so an exporter already attached to that source will start seeing it with no configuration change. Anything filtering or asserting on the span tree — a sampler keyed on a root span name, a test asserting a span's parent — needs to account for it.

### PII note

Raw query text is never stored as a span attribute. The `query.hash` attribute on `ragnet.retrieve` is an 8-character hex prefix of the SHA-256 hash of the query string — sufficient for correlation without exposing sensitive content.

### Error recording

When an operation fails the span status is set to `Error` with the exception message. Error counters (see below) are also incremented. All `Warning`/`Error` log messages are preserved separately for operator visibility.

---

## Metrics

All 11 instruments below are defined in `src/Rag.NET/Telemetry/RagTelemetry.cs`, on the `"Rag.NET"` meter. A test (`RagTelemetryMetricsDocumentationTests` in `Rag.NET.RepoConventions.Tests`) asserts this table's instrument names against that file directly, so the two cannot silently drift apart the way they did once already — this table used to list 8 of the 11, predating `ragnet.ratelimit.wait.duration`, `ragnet.llm.tokens` and `ragnet.llm.cost`.

| Instrument | Type | Unit | Description |
|---|---|---|---|
| `ragnet.ingest.duration` | Histogram | ms | Total ingestion time per document |
| `ragnet.embed.duration` | Histogram | ms | Embedding generation time per batch |
| `ragnet.retrieve.duration` | Histogram | ms | End-to-end retrieval time per query |
| `ragnet.ask.duration` | Histogram | ms | Answer generation time per query |
| `ragnet.ratelimit.wait.duration` | Histogram | ms | Time spent waiting for a rate-limit permit. Tagged `surface=chat\|embedding` and `outcome=granted\|rejected\|cancelled\|faulted` (`rejected` only for the two deliberate rejections — queue overflow and over-capacity permits; unexpected failures record `faulted`). Recorded for every acquire outcome; rejection *details* remain observable through the caller's exception handling |
| `ragnet.chunks.stored` | Counter | chunks | Total chunks written to the vector store |
| `ragnet.chunks.retrieved` | Counter | chunks | Total chunks returned by retrieval |
| `ragnet.ingest.errors` | Counter | errors | Total ingestion failures |
| `ragnet.retrieve.errors` | Counter | errors | Total retrieval failures |
| `ragnet.llm.tokens` | Counter | tokens | LLM tokens consumed by chat and embedding calls. Tagged `direction=in\|out` and `surface=chat\|embedding`. Provider-reported when the response carries full usage, tiktoken `cl100k` estimates otherwise — the same numbers the cost ledger records |
| `ragnet.llm.cost` | Counter | usd (nominal) | LLM spend computed from configured prices. Tagged `surface=chat\|embedding`. The unit is nominal: values are in whatever currency `CostBudgetOptions` prices are quoted in |

**Deliberately absent: `ragnet.ask.errors`.** Answer-generation failures are observable through the `ragnet.ask` span status (`ActivityStatusCode.Error`) and the caller's exception handling; a fourth error counter would exist only to expose a public metric surface for a single call site.

### Two meters: `Rag.NET` and `Rag.NET.Evaluation`

The 11 instruments above all live on the `"Rag.NET"` meter. A second, separate meter exists — `"Rag.NET.Evaluation"`, defined by `Rag.NET.Evaluation`'s internal `ShadowTelemetry` (`src/Rag.NET.Evaluation/Shadow/ShadowTelemetry.cs`) — with five of its own counters for the shadow-capture pipeline (`ragnet.shadow.enqueued`, `ragnet.shadow.dropped`, `ragnet.shadow.processed`, `ragnet.shadow.failed`, `ragnet.shadow.abandoned`; together `enqueued − dropped − failed − abandoned = processed`).

**Why a second meter rather than one.** `RagTelemetry`, the type that owns the `"Rag.NET"` meter, is `internal` to the `Rag.NET` core assembly — the same reason satellite packages get their own linked `ActivitySource` instance (see [Satellite spans](#satellite-spans) below) rather than referencing core's. `Rag.NET.Evaluation` must not take a dependency on core for this either, so it builds its own meter, on its own name, mirroring `RagTelemetry`'s shape (an internal static class of `ragnet.*`-named instruments) rather than sharing one.

**Why this matters to a consumer:** a hand-wired `.AddMeter("Rag.NET")` reaches none of the five `ragnet.shadow.*` counters. This is exactly the trap [`AddRagNetInstrumentation()`](#the-trap-this-closes-two-meters-not-one) closes by registering both names at once.

---

## Conventions for instrumenting a satellite package

Phase 4.4 gives nine previously-silent satellites (the six vector stores, the two rerankers,
GraphRag, Raptor, Graph, Security, Caching) spans of their own, on the same shared `"Rag.NET"`
`ActivitySource` core uses — see `src/Shared/RagTelemetrySource.cs` for the linking mechanism
that lets every package create its own instance of that name without depending on core. This
section is decided once, here, so instrumenting each package is a lookup rather than a fresh
design.

### Span names: `ragnet.<area>.<operation>`

Core's eight spans — `ragnet.ingest`, `ragnet.parse`, `ragnet.chunk`, `ragnet.embed`,
`ragnet.store`, `ragnet.query`, `ragnet.retrieve`, `ragnet.ask` — are all two segments:
`ragnet.<operation>`, because the "area" is implicitly the core pipeline itself. A satellite has
no such default area, so its spans take a third segment naming it: `ragnet.<area>.<operation>`.

**The area names one subsystem, not one backend.** Where several packages implement the same
abstraction — the six vector stores, the two rerankers — they share one area and one span name,
and the specific backend is a *tag*, not a name suffix. This already exists: `ragnet.store`
carries a `vector.store` tag rather than core minting `ragnet.qdrant.store`,
`ragnet.pgvector.store`, and so on. The same discipline applies going forward:

| Area | Span name shape | Backend tag |
|---|---|---|
| Vector stores (Qdrant, PgVector, Pinecone, Weaviate, Chroma, Azure AI Search) | `ragnet.vectorstore.<operation>` (e.g. `ragnet.vectorstore.upsert`, `ragnet.vectorstore.search`) | `vector.store` |
| Reranking (Onnx, Cohere) | `ragnet.rerank` (one span; both rerankers expose a single scoring operation, so there is no `<operation>` suffix to fork on) | `reranker.type` |

Forking the span name per backend multiplies the number of distinct span names by the number of
backends for no analytical benefit — every dashboard or alert built on `ragnet.vectorstore.search`
p99 latency would otherwise need to know the full backend list and OR them together. Six span
names collapsing to one, tagged, is the same reasoning that produced `vector.store` on
`ragnet.store` in the first place.

Where a package is its own subsystem with no shared abstraction to fork from, its area is its own
name and every operation is genuinely area-specific:

| Package | Area | Example span names |
|---|---|---|
| GraphRag | `graphrag` | `ragnet.graphrag.extract`, `ragnet.graphrag.communities`, `ragnet.graphrag.search` |
| Raptor | `raptor` | `ragnet.raptor.build`, `ragnet.raptor.summarize` |
| Graph | `graph` | `ragnet.graph.cluster`, `ragnet.graph.pagerank` |
| Security | `security` | `ragnet.security.sanitize`, `ragnet.security.guard` |

**Caching is deliberately not on this list.** `Rag.NET.Caching`'s `UseCaching()` only registers `HybridCache` — the cache-hit/miss logic it wraps lives in core, not in the satellite, so a `ragnet.caching.lookup` span in this package would restate a decision core already made rather than add one of its own. See [Three packages left uninstrumented deliberately](#three-packages-left-uninstrumented-deliberately) below.

A satellite span nests under whichever core span models the step it participates in — a
`ragnet.vectorstore.upsert` span is a child of `ragnet.store`, a `ragnet.rerank` span is a
child of `ragnet.retrieve` — the same way `ragnet.parse`/`ragnet.chunk`/`ragnet.embed`/`ragnet.store`
already nest under `ragnet.ingest`.

### Tag names: dotted, no exceptions

Every tag is dotted (`document.id`, `chunk.count`, `query.hash`, `result.count`, `source.count`,
`parser.type`, `synthesis.strategy`, `top.k`, `vector.store`). `top_k` and `vector_store` were
snake_case outliers predating this convention; Task 7 renamed both to `top.k` and `vector.store`
(and updated the test assertions that pinned the old names) — nothing outside this repository
consumed them, so no compatibility shim was needed. **New tags — every tag a satellite adds — are
dotted**, with no exceptions going forward.

**No tag is mandated on every span.** `ragnet.query` itself carries none, by design (see the PII
note above and its rationale in the Spans section). Two tags recur today only because the same
identifying value threads through several stages of the *same* tree: `document.id` on every span
in the ingest tree, `query.hash`/`top.k` on `ragnet.retrieve`. A satellite span nested inside one
of those trees should repeat the identifying tag it inherits — `document.id` inside ingest,
`query.hash` inside retrieve — when it is cheaply available at that call site; it costs one field
copy and saves a trace-tree walk for anyone inspecting a single span in isolation. It should not
invent a new cross-cutting tag that duplicates what an ancestor already carries for a different
purpose.

Everything else is area-specific: an operation's own inputs and outputs, named `<area>.<attribute>`
— `vectorstore.batch.size`, `reranker.candidate.count`, `graphrag.entity.count`, `raptor.tree.depth`,
`security.guard.action`, `caching.hit` — mirroring the shape of `parser.type` and
`synthesis.strategy`, not the bare-noun shape reserved for the core cross-area identifiers above.

### What must never appear in a tag

- **Raw query text.** This is exactly why `query.hash` exists instead — see the PII note above.
- **Document or chunk content.** Tag counts and types (`chunk.count`, `parser.type`), never the text itself.
- **Anything a Security guard removed or blocked.** A span may say *that* a guard acted
  (`security.guard.action=redact`) and how much (a count), never *what* it acted on. Task 8
  instruments the Security package against this rule directly — a `ragnet.security.sanitize`
  span records the sanitiser ran and what it found the *shape* of (e.g. an injection-pattern
  category), never the substring that matched.
- **Credentials, connection strings, and API keys.** Not called out by the existing spans because
  core never touches them, but every vector-store and Cohere-reranking satellite does, and none
  of that ever belongs in a tag.

If a value fails any of these, hash it (`query.hash`'s pattern), count it, or classify it —
never carry the raw value.

### Satellite spans

Every span the nine instrumented satellites emit, with its tags and its parent in the core span
tree. All of them are on the same shared `"Rag.NET"` `ActivitySource` described above.

| Package | Span | Tags | Nests under |
|---|---|---|---|
| Qdrant, PgVector, Pinecone, Chroma | `ragnet.vectorstore.upsert` | `vector.store`, `vectorstore.collection`, `vectorstore.batch.size` | `ragnet.store` |
| Qdrant, PgVector, Pinecone, Chroma | `ragnet.vectorstore.search` | `vector.store`, `vectorstore.collection`, `vectorstore.result.count` | `ragnet.retrieve` |
| Qdrant, PgVector, Pinecone, Chroma | `ragnet.vectorstore.delete` | `vector.store`, `vectorstore.collection` | — (not on the ingest/retrieve hot path) |
| Weaviate, Azure AI Search | `ragnet.vectorstore.search` (hybrid overload) | `vector.store`, `vectorstore.collection`, `vectorstore.hybrid=true`, `vectorstore.result.count` | `ragnet.retrieve` |
| Onnx, Cohere | `ragnet.rerank` | `reranker.type`, `reranker.candidate.count` (Cohere also sets `reranker.result.count`) | `ragnet.retrieve` |
| GraphRag | `ragnet.graphrag.extract` | `document.id`, `graphrag.entity.count`, `graphrag.relationship.count` | `ragnet.ingest` |
| GraphRag | `ragnet.graphrag.communities` | `document.id`, `graphrag.community.count` | `ragnet.ingest` (parents the Graph spans below) |
| GraphRag | `ragnet.graphrag.search` | `graphrag.search.mode` (`local`\|`global`), `graphrag.entity.count` (local) or `graphrag.community.count` (global) | `ragnet.retrieve` |
| Graph | `ragnet.graph.cluster` | `graph.node.count`, `graph.relationship.count`, `graph.community.count` | `ragnet.graphrag.communities` |
| Graph | `ragnet.graph.pagerank` | `graph.node.count`, `graph.relationship.count` | `ragnet.graphrag.communities` |
| Raptor | `ragnet.raptor.build` | `document.id` (the reserved `raptor://corpus-tree` under `Corpus` scope), `raptor.tree.depth`, `raptor.summary.count` | `ragnet.ingest` — **not emitted by `RaptorTreeRebuilder.RebuildAsync`**, which builds outside ingestion; only `ragnet.raptor.summarize` appears on that path |
| Raptor | `ragnet.raptor.summarize` | `raptor.tree.level`, `raptor.chunk.count`, `raptor.cluster.count`, `raptor.cluster.maxclusters.overridden` (set `true` only when `RaptorOptions.MaxClusters` is configured and honouring it would produce a cluster averaging above `TargetClusterSize` — `TargetClusterSize`'s floor wins in that case; absent otherwise) | `ragnet.raptor.build` (once per level) — parented by the ambient activity instead when `RaptorTreeRebuilder.RebuildAsync` drives the build |
| Security | `ragnet.security.guard` (RBAC, regex, trust-level) | `security.guard.type`, `security.guard.action`, `security.chunks.affected` | `ragnet.retrieve` |
| Security | `ragnet.security.sanitize` (regex/LLM/PII chunk sanitisers) | `security.sanitizer.type`, `security.matches.count` | `ragnet.ingest` |

### Three packages left uninstrumented deliberately

Nine of the twelve packages the 2026-04-04 deferral named got spans this phase. Three did not, and
each is a considered decision rather than an oversight — a span that only restates its parent's
cost is worse than none, because it adds a row to every trace view without adding information to
any of them:

- **`Rag.NET.Caching`** — `UseCaching()` registers `HybridCache` and nothing else; the hit/miss
  decision it wraps is core's, already covered by whatever span core opens around the cached call.
- **`RaptorRetrievalBehavior`** — a no-op in Raptor's default retrieval mode (all levels of the
  tree participate in ordinary retrieval directly; the behavior only does something in the
  non-default configuration where it filters by level), so a span on the common path would read
  "ran, did nothing" on every trace.
- **`IQuerySanitiser`** (`LlmQuerySanitiser`, `RegexQuerySanitiser`, and the pipeline decorator
  that wraps them) — it runs *before* `ragnet.query` opens, on the raw user question, so it has no
  core span to nest under. A root-level `ragnet.security.sanitize` span here would be a second,
  disconnected tree per query rather than a child of anything — the same problem `ragnet.query`
  itself was added to solve for `ragnet.retrieve`/`ragnet.ask` (see the changelog note above).

---

## Example: Prometheus + Grafana

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Rag.NET")
        .AddMeter("Rag.NET.Evaluation")
        .AddPrometheusExporter());

app.MapPrometheusScrapingEndpoint();
```

Useful Prometheus queries:

```promql
# P99 ingest latency
histogram_quantile(0.99, rate(ragnet_ingest_duration_bucket[5m]))

# Chunks written per second
rate(ragnet_chunks_stored_total[1m])

# Error rate
rate(ragnet_ingest_errors_total[5m])
```

---

## Example: Console exporter (local development)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Rag.NET")
        .AddConsoleExporter())
    .WithMetrics(m => m
        .AddMeter("Rag.NET")
        .AddMeter("Rag.NET.Evaluation")
        .AddConsoleExporter());
```

---

## Example: ActivityListener (unit tests / no SDK)

If you want to assert spans in tests without pulling in the full OTel SDK:

```csharp
var activities = new List<Activity>();
using var listener = new ActivityListener
{
    ShouldListenTo = s => s.Name == "Rag.NET",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activities.Add,
};
ActivitySource.AddActivityListener(listener);

await pipeline.IngestAsync(stream, metadata);

var ingestSpan = activities.Single(a => a.OperationName == "ragnet.ingest");
Assert.Equal("my-doc", ingestSpan.GetTagItem("document.id"));
```
