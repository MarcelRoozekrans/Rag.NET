# OpenTelemetry Tracing & Metrics Design

**Date:** 2026-04-04
**Feature:** Built-in OpenTelemetry instrumentation for the Rag.NET core pipeline

---

## Goal

Instrument the Rag.NET pipeline with `System.Diagnostics.ActivitySource` spans and `System.Diagnostics.Metrics` counters/histograms so that production deployments can observe latency breakdowns, chunk throughput, and error rates without any code changes. Zero overhead when no OTel listener is attached.

---

## Decisions

- **Location:** Baked into `Rag.NET` core — no separate package, no opt-in registration call.
- **Approach:** Instrument the existing behavior layer and top-level facades directly (Option A). Spans are co-located with the code they measure, matching the pattern used by `System.Net.Http`, `Microsoft.Data.SqlClient`, and ASP.NET Core.
- **No new NuGet dependencies:** `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics` are in-box since .NET 8.
- **Scope:** Core pipeline only (ingest, chunk, embed, store, retrieve, ask). Package-specific spans (reranking, GraphRAG, RAPTOR, security) deferred to follow-up when evidence demands it.
- **Logging cleanup:** Debug-level `*Started`/`*Completed` log messages that duplicate span data are removed. All `Warning` and `Error` log messages stay.
- **PII safety:** Raw query text is never stored as a span attribute. A SHA-256 8-char prefix is used instead (`query.hash`).

---

## Core Infrastructure

`src/Rag.NET/Telemetry/RagTelemetry.cs` — single static class, all primitives:

```csharp
internal static class RagTelemetry
{
    internal const string SourceName = "Rag.NET";

    internal static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
    internal static readonly Meter Meter = new(SourceName, "1.0.0");

    // Histograms (unit: ms)
    internal static readonly Histogram<double> IngestDuration   = Meter.CreateHistogram<double>("ragnet.ingest.duration",   "ms", "Total ingestion time per document");
    internal static readonly Histogram<double> EmbedDuration    = Meter.CreateHistogram<double>("ragnet.embed.duration",    "ms", "Embedding generation time per batch");
    internal static readonly Histogram<double> RetrieveDuration = Meter.CreateHistogram<double>("ragnet.retrieve.duration", "ms", "End-to-end retrieval time per query");
    internal static readonly Histogram<double> AskDuration      = Meter.CreateHistogram<double>("ragnet.ask.duration",      "ms", "Answer generation time per query");

    // Counters
    internal static readonly Counter<long> ChunksStored    = Meter.CreateCounter<long>("ragnet.chunks.stored",    "chunks", "Total chunks written to the vector store");
    internal static readonly Counter<long> ChunksRetrieved = Meter.CreateCounter<long>("ragnet.chunks.retrieved", "chunks", "Total chunks returned by retrieval");
    internal static readonly Counter<long> IngestErrors    = Meter.CreateCounter<long>("ragnet.ingest.errors",    "errors", "Total ingestion failures");
    internal static readonly Counter<long> RetrieveErrors  = Meter.CreateCounter<long>("ragnet.retrieve.errors",  "errors", "Total retrieval failures");
}
```

### OTel SDK registration (caller-side, no change to library)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Rag.NET"))
    .WithMetrics(m => m.AddMeter("Rag.NET"));
```

---

## Spans

| Span name | Instrumented in | Key attributes |
|---|---|---|
| `ragnet.ingest` | `RagPipeline.IngestAsync` | `document.id`, `content.type` |
| `ragnet.parse` | `ParseBehavior.HandleAsync` | `document.id`, `parser.type` |
| `ragnet.chunk` | `ChunkingBehavior.HandleAsync` | `document.id`, `chunk.count` |
| `ragnet.embed` | `EmbeddingBehavior.HandleAsync` | `document.id`, `chunk.count` |
| `ragnet.store` | `StorageBehavior.HandleAsync` | `document.id`, `chunk.count`, `vector_store` |
| `ragnet.retrieve` | `PipelineRetriever.RetrieveAsync` | `query.hash`, `top_k`, `result.count` |
| `ragnet.ask` | `ChatAnswerEngine.AskAsync` | `source.count`, `synthesis.strategy` |

Span lifecycle pattern:

```csharp
using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.embed");
activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
activity?.SetTag("chunk.count", ctx.Chunks.Count);
try
{
    // ... work ...
    activity?.SetTag("chunk.count", ctx.EmbeddedChunks.Count); // update after
}
catch (Exception ex)
{
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    throw;
}
```

`StartActivity` returns `null` when no listener is active — all `.?` calls are zero-cost no-ops.

---

## Metrics usage

Metrics are recorded at the terminal point of each operation:

- `IngestDuration` — recorded in `RagPipeline.IngestAsync` after success, using `Stopwatch.GetElapsedTime`
- `EmbedDuration` — recorded in `EmbeddingBehavior` after `GenerateAsync` returns
- `RetrieveDuration` — recorded in `PipelineRetriever.RetrieveAsync` after success
- `AskDuration` — recorded in `ChatAnswerEngine.AskAsync` after LLM returns
- `ChunksStored` — recorded in `StorageBehavior` with `ctx.EmbeddedChunks.Count`
- `ChunksRetrieved` — recorded in `PipelineRetriever` with `result.Count`
- `IngestErrors` / `RetrieveErrors` — incremented in catch blocks

---

## Log message cleanup

Remove from `RagPipelineLog.cs` (replaced by spans):

- `IngestStarted`, `IngestCompleted`
- `RetrieveStarted`, `RetrieveCompleted`
- `AskStarted`
- `VectorStoreSearchCompleted`
- `RerankingCompleted`
- `RedundancyFilterCompleted`
- `MmrSelectionCompleted`
- `HydeDocumentGenerated`
- `QueryExpansionCompleted`
- `SelfQueryCompleted`
- `ParentDocumentRetrieved`
- `EmbeddingCacheMiss`, `ResultCacheMiss`

Keep all `Warning` and `Error` log messages — these are for human operators and carry failure context spans cannot provide.

---

## Testing

- Unit tests: assert `ActivitySource` emits the correct span name and tags by subscribing a test `ActivityListener` in the test body.
- Metric tests: assert `Meter` instruments fire with correct values using `InMemoryExporter` from `OpenTelemetry.Testing`.
- No integration infrastructure required — tests run fully in-process.

---

## Out of Scope

- Package-specific spans (reranking, GraphRAG, RAPTOR, security, memory) — deferred
- `Rag.NET.Telemetry` separate package — not needed; in-box types suffice
- OTel SDK package reference in `Rag.NET` — callers bring their own SDK
