# Ingestion Operations — Design (Phase 1.3)

**Date:** 2026-07-24
**Milestone:** 1 — Feature Backlog, Phase 1.3
**Covers features.md rows:** Batch Ingestion Optimiser; Webhook / Event-Driven Ingestion; Embedding Versioning & Re-indexing

## Scope decisions (agreed)

1. **Triggers**: webhook endpoint + background polling ship this phase. The Azure Service Bus
   trigger is deferred; the job-queue abstraction is the seam it will later plug into.
2. **Batch optimiser** delivers real chunk-batch embedding inside a document (the features.md
   detail section falsely claims this exists — it will be corrected). Document-level
   parallelism (`IngestFromProviderAsync` + `MaxDegreeOfParallelism`) already exists and stays.
3. **Re-indexing** re-embeds from `SqliteDocumentStore`'s stored chunk text when an
   `IRagDataManager` is registered; otherwise `ReindexStaleAsync` reports the stale document
   list for caller-driven re-ingest. The CLI command mentioned in features.md lands with the
   CLI tool (Milestone 3).

## 1. Batch Ingestion Optimiser

**Package:** core `Rag.NET`

- `IngestionOptions` gains `EmbedBatchSize` (default 100, > 0) and
  `MaxConcurrentEmbeddingBatches` (default 2, > 0).
- `EmbeddingBehavior`: pending chunks (post precomputed-skip) are split into batches of
  `EmbedBatchSize`; batches embed concurrently via `Parallel.ForEachAsync` bounded by
  `MaxConcurrentEmbeddingBatches`; results reassemble by original chunk index (order
  preserved — same index-addressed array pattern as the precomputed merge). Per-batch
  count-mismatch guard (same message shape as the existing whole-call guard, plus batch
  ordinal). Telemetry: existing `chunk.count`/`chunk.precomputed` tags plus `embed.batches`.
  Single-batch documents take the exact current code path (no behavioral change below 100
  chunks with defaults).
- `StorageBehavior` is already a single bulk upsert — unchanged.
- features.md: correct the contradictory detail section (document-level parallelism was
  pre-existing; chunk-batch embedding is what this delivers) and tick the row.

## 2. Webhook / Event-Driven Ingestion

**Packages:** `Rag.NET.DataProviders` (queue, processor, polling), `Rag.NET.Api` (webhook endpoint)

### 2a. Job queue + processor (first background-processing infra in the repo)

```csharp
public sealed record IngestionJob
{
    public required Stream Content { get; init; }        // or byte[] — decided in planning vs Channel lifetime
    public required DocumentMetadata Metadata { get; init; }
    public IngestionOptions? Options { get; init; }
}

public interface IIngestionJobQueue
{
    ValueTask EnqueueAsync(IngestionJob job, CancellationToken ct = default);
    IAsyncEnumerable<IngestionJob> DequeueAllAsync(CancellationToken ct = default);
}
```

- `ChannelIngestionJobQueue`: bounded `Channel<IngestionJob>` (capacity 1000,
  `BoundedChannelFullMode.Wait` — backpressure, never drop).
- `IngestionJobProcessor : BackgroundService`: consumes `DequeueAllAsync`, calls
  `IIngestor.IngestAsync` per job; per-job failure → LoggerMessage warning (job id/document id),
  processor never crashes; graceful drain on shutdown token.
- DI: `UseEventDrivenIngestion(Action<EventDrivenIngestionOptions>? configure = null)` —
  registers queue + hosted processor. Options: `QueueCapacity` (1000).

### 2b. Webhook endpoint (`Rag.NET.Api`)

- `MapRagNetWebhooks(this IEndpointRouteBuilder, string prefix = "/rag/webhooks")` — minimal
  API POST endpoint: verify HMAC-SHA256 signature (options: `Secret` required,
  `SignatureHeader` default "X-Signature-256", GitHub-style `sha256=` prefix tolerated) over
  the raw body; parse via `IWebhookPayloadParser`; enqueue resulting jobs; 202 Accepted with
  job count. 401 on bad/missing signature; 400 on unparseable payload.
- `IWebhookPayloadParser`: `bool TryParse(JsonElement payload, out IReadOnlyList<IngestionJob> jobs)`
  (exact shape refined in planning). v1 ships `GenericWebhookPayloadParser`:
  `{ "documentId": "...", "content": "...", "metadata": { ... } }` (single doc or array).
  GitHub/Notion/Slack-specific parsers are extension points for connector packages — not this phase.
- Auth: webhook signature replaces the API-key middleware for this route (documented; the
  existing `ApiKeyMiddleware` continues to guard the other endpoints).

### 2c. Background polling trigger

- `BackgroundPollingTrigger : BackgroundService` wrapping a configured
  `IFileContentProvider` + the existing `IngestFromProviderAsync` (hash-skip preserved) every
  `PollingInterval` (TimeSpan, default 5 min). Per-cycle failure → warning, next cycle
  continues. Cron/NCrontab deferred (YAGNI).
- DI: `UsePollingIngestion(Func<IServiceProvider, IFileContentProvider> provider, Action<PollingIngestionOptions>? configure)`;
  multiple registrations = multiple pollers.

## 3. Embedding Versioning & Re-indexing

**Package:** core `Rag.NET`

### 3a. Version store

```csharp
public interface IEmbeddingVersionStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task SetAsync(string documentId, string modelId, int dimension, CancellationToken ct = default);
    Task<IReadOnlyList<(string DocumentId, string ModelId, int Dimension)>> GetAllAsync(CancellationToken ct = default);
    Task RemoveAsync(string documentId, CancellationToken ct = default);
}
```

- `SqliteEmbeddingVersionStore`: table `embedding_versions(doc_id PK, model_id, dimension, embedded_at)`,
  `INSERT OR REPLACE`, same connection/init conventions as `SqliteContentHashStore`.
- Model identity resolution (`EmbeddingModelIdentity` helper): from
  `IEmbeddingGenerator.GetService<EmbeddingGeneratorMetadata>()` → `ProviderName/ModelId`;
  explicit override via option (`EmbeddingVersioningOptions.ModelId`) for adapters exposing no
  metadata; if neither → versioning disabled with a one-time warning (never guess).

### 3b. Stamping + re-indexing

- `StorageBehavior` (or a small trailing behavior — decided in planning): after successful
  store, `SetAsync(docId, modelId, dimension)`. `PipelineIngestor.DeleteAsync` also removes
  the version row.
- `ReindexStaleAsync(CancellationToken)` extension on `IRagPipeline` returning
  `ReindexResult { IReadOnlyList<string> Reindexed, IReadOnlyList<string> ReportedStale, IReadOnlyList<(string, string)> Failed }`:
  1. Resolve current model identity; enumerate version store; stale = different modelId (or dimension).
  2. With `IRagDataManager`: per stale doc, fetch stored chunks, re-embed (batched per §1),
     re-store via `IVectorStore.StoreAsync` (replaces by `(DocumentId, ChunkIndex)`), update stamp.
     Per-document failure → collected in `Failed`, loop continues.
  3. Without a data manager: all stale docs land in `ReportedStale`.
- Sparse vectors: re-indexing re-embeds dense only in this phase; if a sparse encoder + sparse
  store are registered the sparse vectors are also regenerated from the same stored text
  (cheap to include since the encoder is local — confirmed in planning against StoreSparseAsync
  idempotency). BM25 needs no re-index (text unchanged).
- DI: `UseEmbeddingVersioning(Action<EmbeddingVersioningOptions>? configure = null)` — registers
  the store + enables stamping. Re-indexing requires it.

## Error handling summary

House posture throughout: degraded-never-broken (batch failure fails that document only;
job failure logs and continues; per-cycle polling failure retries next cycle; per-document
re-index failure collected, loop continues), OCE-first catches, LoggerMessage source-gen.

## Testing

- Batching: reassembly order with hand-computed batch boundaries (e.g. 5 chunks, batch size 2 →
  3 batches), mixed precomputed/plain across batch edges, per-batch count-mismatch, concurrency
  bound respected (max in-flight tracked by fake embedder), single-batch path unchanged.
- Queue/processor: bounded backpressure, FIFO drain, per-job failure isolation, graceful
  shutdown mid-queue, cancellation.
- Webhook: TestServer — valid signature → 202 + jobs enqueued; bad/missing signature → 401;
  malformed payload → 400; array payload → N jobs; custom parser honored.
- Polling: fake provider + short interval — cycles run, hash-skip respected, failure isolation,
  shutdown.
- Versioning: sqlite store round-trip; stamping on ingest + removal on delete; model identity
  resolution (metadata / override / neither→disabled); `ReindexStaleAsync` with fake stores —
  stale detection, re-embed + re-store + stamp update, report-only path, per-doc failure
  collection, dimension-change detection.

## Out of scope

- Azure Service Bus trigger (abstraction seam left ready).
- Provider-specific webhook parsers (GitHub/Notion/Slack) — connector-package extension points.
- Cron scheduling for polling (interval only).
- CLI re-index command (Milestone 3, CLI tool phase).
- Re-parsing/re-chunking on re-index (stored chunks are reused verbatim).
