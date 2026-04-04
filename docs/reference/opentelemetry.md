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
| `ragnet.store` | Vector store write behavior | `document.id`, `chunk.count`, `vector_store` |
| `ragnet.retrieve` | Top-level retrieval call | `query.hash`, `top_k`, `result.count` |
| `ragnet.ask` | Answer generation engine | `source.count`, `synthesis.strategy` |

All spans are nested under their parent in the same trace, so a single `IngestAsync` call produces a tree:

```
ragnet.ingest
  ragnet.parse
  ragnet.chunk
  ragnet.embed
  ragnet.store
```

And a single `AskAsync` call produces:

```
ragnet.retrieve
  (vector store / retrieval behaviors)
ragnet.ask
```

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
