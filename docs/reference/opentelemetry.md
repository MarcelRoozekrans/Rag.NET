# OpenTelemetry Integration

Rag.NET emits traces and metrics using the in-box `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics` APIs. There are no extra NuGet packages required on the library side — you bring your own OpenTelemetry SDK and wire it up with two lines.

---

## Quick Setup

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Rag.NET")
        .AddOtlpExporter())   // or AddConsoleExporter(), AddZipkinExporter(), etc.
    .WithMetrics(m => m
        .AddMeter("Rag.NET")
        .AddOtlpExporter());
```

That is all. No opt-in call inside `AddRagNet(...)` is needed — instrumentation is always active and is zero-overhead when no listener is attached.

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

| Instrument | Type | Unit | Description |
|---|---|---|---|
| `ragnet.ingest.duration` | Histogram | ms | Total ingestion time per document |
| `ragnet.embed.duration` | Histogram | ms | Embedding generation time per batch |
| `ragnet.retrieve.duration` | Histogram | ms | End-to-end retrieval time per query |
| `ragnet.ask.duration` | Histogram | ms | Answer generation time per query |
| `ragnet.chunks.stored` | Counter | chunks | Total chunks written to the vector store |
| `ragnet.chunks.retrieved` | Counter | chunks | Total chunks returned by retrieval |
| `ragnet.ingest.errors` | Counter | errors | Total ingestion failures |
| `ragnet.retrieve.errors` | Counter | errors | Total retrieval failures |

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
| Reranking (Onnx, Cohere) | `ragnet.rerank.<operation>` (e.g. `ragnet.rerank.score`) | `reranker.type` |

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
| Caching | `caching` | `ragnet.caching.lookup` |

A satellite span nests under whichever core span models the step it participates in — a
`ragnet.vectorstore.upsert` span is a child of `ragnet.store`, a `ragnet.rerank.score` span is a
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

---

## Example: Prometheus + Grafana

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Rag.NET")
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
