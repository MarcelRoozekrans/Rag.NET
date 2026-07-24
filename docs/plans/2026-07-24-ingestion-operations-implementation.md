# Ingestion Operations Implementation Plan (Phase 1.3)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the three backlog ingestion features: chunk-batch embedding with bounded concurrency, webhook + polling event-driven ingestion, and embedding versioning with stale re-indexing.

**Architecture:** Per `docs/plans/2026-07-24-ingestion-operations-design.md`. Three independent parts: (A) `EmbeddingBehavior` batching; (B) job queue + `BackgroundService` processor + HMAC webhook endpoint + polling trigger; (C) `IEmbeddingVersionStore` + stamping + `ReindexStaleAsync`. Order A → B → C (C reuses A's batching).

**Tech Stack:** .NET 10, xUnit v3 + NSubstitute, System.Threading.Channels, ASP.NET Core minimal APIs + TestServer, Microsoft.Data.Sqlite, Microsoft.Extensions.AI (`EmbeddingGeneratorMetadata`).

**Conventions (read first):**
- Ingestion behavior tests: `tests/Rag.NET.Tests/Ingestion/Behaviors/` (hand-built `IngestionContext` + next delegate — copy `EmbeddingBehaviorTests.cs` `MakeContext`). API tests: `tests/Rag.NET.Api.Tests/Integration/RagApiIntegrationTests.cs` (TestServer pattern).
- House posture: degraded-never-broken; `catch (OperationCanceledException) { throw; }` first (in composite/background operations use the Part-C-of-1.2 form `when (ct.IsCancellationRequested)` where an inner component's own timeout must not kill the composite); LoggerMessage source-gen (`RagPipelineLog` for core).
- Options: mutable POCOs in `src/Rag.NET.Abstractions/Models/Options/`, validated in `Use*` extensions.
- Analyzers: MA0051 (60-line cap), MA0015, ZA0601/ZA0501, EPS05/HLQ, warnings-as-errors.
- Commit trailer: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Filtered tests + targeted builds during work; per part end: one `dotnet build Rag.NET.slnx`.

---

## Part A — Chunk-batch embedding

### Task A1: options + batched EmbeddingBehavior

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/Options/IngestionOptions.cs` — add:

```csharp
/// <summary>Chunks per embedding batch within a single document. Default 100.</summary>
public int EmbedBatchSize { get; init; } = 100;
/// <summary>Maximum embedding batches in flight concurrently per document. Default 2.</summary>
public int MaxConcurrentEmbeddingBatches { get; init; } = 2;
```

- Modify: `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs` (read fully — it already has pendingIndices/texts scan, precomputed skip via `is not { IsEmpty: false }`, count-mismatch guard, `AssembleEmbeddedChunks`): when `pending.Count <= EmbedBatchSize` keep the EXACT current single-call path. Otherwise: slice `texts` into batches of `EmbedBatchSize`; `Parallel.ForEachAsync` over batch descriptors with `MaxDegreeOfParallelism = MaxConcurrentEmbeddingBatches`; each batch calls `Embedder.GenerateAsync(batchTexts, ct)`, validates `generated.Count == batchTexts.Count` (guard message includes batch ordinal + counts + document id), writes vectors into the shared `byIndex` array at the batch's global indices (disjoint slices — no locking needed; document why). Stopwatch/EmbedDuration wraps the whole parallel section; activity gains `embed.batches` tag. `PipelineIngestor` validation (read it) gains `EmbedBatchSize > 0` and `MaxConcurrentEmbeddingBatches > 0` checks alongside its existing option validation.
- Test: `tests/Rag.NET.Tests/Ingestion/Behaviors/EmbeddingBehaviorTests.cs` (append):

```csharp
// 1. Batched_ReassemblesInOrder: 5 chunks, EmbedBatchSize=2 → 3 GenerateAsync calls with
//    ["c0","c1"],["c2","c3"],["c4"]; EmbeddedChunks[i] matches chunk i's distinct vector.
// 2. Batched_MixedPrecomputedAcrossBatchEdges: chunks 0,3 precomputed, batch size 2 →
//    batches ["c1","c2"],["c4"]; precomputed untouched; order preserved.
// 3. Batched_CountMismatchInSecondBatch_ThrowsWithBatchOrdinal.
// 4. Batched_ConcurrencyBoundRespected: fake embedder tracks max concurrent calls via
//    Interlocked; EmbedBatchSize=1, MaxConcurrentEmbeddingBatches=2, 6 chunks, embedder
//    delays via TaskCompletionSource → max in-flight <= 2.
// 5. SingleBatch_PathUnchanged: 3 chunks, batch size 100 → exactly 1 GenerateAsync call
//    (existing tests already cover semantics; this pins the no-split fast path).
// 6. PipelineIngestor validation: EmbedBatchSize=0 → validation error result (follow
//    PipelineIngestorValidationTests.cs conventions).
```

TDD; run `--filter "FullyQualifiedName~EmbeddingBehavior|FullyQualifiedName~PipelineIngestorValidation"`. **Commit** `feat(ingestion): chunk-batch embedding with bounded concurrency`

### Task A2: docs + tick

`docs/reference/features.md`: REWRITE the Batch Ingestion Optimiser detail section (~lines 888-895) to reflect reality — document-level parallelism (`IngestFromProviderAsync` + `MaxDegreeOfParallelism`) pre-existed; this phase adds chunk-batch embedding (`EmbedBatchSize`, `MaxConcurrentEmbeddingBatches`); remove the stale "✅ Done" contradiction and tick the table row (~line 1056). Ingestion guide (grep docs/guide for the ingestion doc) gains a batching paragraph. **Commit** `docs(ingestion): correct batch optimiser status; document chunk batching; tick feature`

---

## Part B — Event-driven ingestion

### Task B1: job queue + processor (`Rag.NET.DataProviders`)

**Files:**
- Create: `src/Rag.NET.Abstractions/Models/IngestionJob.cs` — record: `required byte[] Content` (bytes, NOT Stream — jobs outlive the enqueue call; document), `required DocumentMetadata Metadata`, `IngestionOptions? Options`.
- Create: `src/Rag.NET.Abstractions/Abstractions/IIngestionJobQueue.cs` — `EnqueueAsync(IngestionJob, ct)` + `IAsyncEnumerable<IngestionJob> DequeueAllAsync(ct)`.
- Create: `src/Rag.NET.DataProviders/EventDriven/ChannelIngestionJobQueue.cs` — bounded `Channel<IngestionJob>` (`BoundedChannelFullMode.Wait`), capacity from `EventDrivenIngestionOptions.QueueCapacity` (default 1000, validated > 0). `DequeueAllAsync` = `reader.ReadAllAsync(ct)`.
- Create: `src/Rag.NET.DataProviders/EventDriven/IngestionJobProcessor.cs` — `BackgroundService`; `ExecuteAsync`: `await foreach (var job in queue.DequeueAllAsync(stoppingToken))` → wrap `job.Content` in `MemoryStream` → `ingestor.IngestAsync`; `Result` failure or exception → LoggerMessage warning with document id, continue; `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` → clean exit. Check how Rag.NET.DataProviders csproj references hosting abstractions — add `Microsoft.Extensions.Hosting.Abstractions` (pin per repo version conventions) if absent.
- Create: `EventDrivenIngestionOptions` (Abstractions options) — `QueueCapacity = 1000`.
- DI in `src/Rag.NET.DataProviders/RagBuilderExtensions.cs` (read first; create the file if the project registers extensions elsewhere — grep `IRagBuilder` in that project): `UseEventDrivenIngestion(Action<EventDrivenIngestionOptions>? configure = null)` — validate, register options + `IIngestionJobQueue` singleton + `AddHostedService<IngestionJobProcessor>()`.
- Test: `tests/Rag.NET.DataProviders.Tests/EventDriven/ChannelIngestionJobQueueTests.cs` + `IngestionJobProcessorTests.cs`:

```csharp
// Queue: FIFO round-trip; bounded backpressure (capacity 1, second enqueue blocks until dequeue —
//   use TaskCompletionSource + timeout assertion); cancellation on blocked enqueue throws.
// Processor: drive ExecuteAsync directly (BackgroundService.StartAsync then enqueue N jobs →
//   substituted IIngestor received N IngestAsync calls with matching metadata);
//   job 2 of 3 fails (Result failure) → jobs 1,3 still processed;
//   ingestor throws → processor continues with next job;
//   StopAsync mid-queue → clean shutdown, no unobserved exceptions.
// DI: UseEventDrivenIngestion registers queue + hosted service; invalid capacity throws.
```

**Commit** `feat(data-providers): ingestion job queue + background processor`

### Task B2: webhook endpoint (`Rag.NET.Api`)

**Files:**
- Create: `src/Rag.NET.Api/Contracts/WebhookOptions.cs` — `Secret` (required non-empty), `SignatureHeader = "X-Signature-256"`, `RoutePrefix = "/rag/webhooks"`.
- Create: `src/Rag.NET.Api/Webhooks/IWebhookPayloadParser.cs` — `bool TryParse(JsonElement payload, [NotNullWhen(true)] out IReadOnlyList<IngestionJob>? jobs);` + `GenericWebhookPayloadParser` (single object `{documentId, content, metadata?}` or array of them; content required non-empty; metadata = string dictionary → Tags; FileName defaults `"{documentId}.txt"`).
- Create: `src/Rag.NET.Api/Webhooks/WebhookSignatureValidator.cs` — internal static: HMAC-SHA256 over the RAW request body bytes with `Secret`; compares hex (case-insensitive) via `CryptographicOperations.FixedTimeEquals`; tolerates GitHub-style `sha256=` prefix.
- Modify: `src/Rag.NET.Api/DependencyInjection/EndpointRouteBuilderExtensions.cs` — `MapRagNetWebhooks(this IEndpointRouteBuilder app)`: POST `{WebhookOptions.RoutePrefix}/ingest`; read raw body (`EnableBuffering` not needed — read once into byte[]); 401 missing/invalid signature; parse JSON → 400 on invalid JSON or parser rejection; enqueue all jobs (`IIngestionJobQueue` from DI — 503 if not registered, message "call UseEventDrivenIngestion"); return `Results.Accepted` with `{ enqueued = jobs.Count }`. IMPORTANT: this route must be reachable WITHOUT the API key — check `ApiKeyMiddleware` (read it) and exempt the webhook route prefix (signature auth replaces key auth; document in xmldoc + docs).
- DI: `AddRagNetWebhooks(this IServiceCollection, Action<WebhookOptions> configure)` — required configure, validate Secret non-empty; registers options + `IWebhookPayloadParser` (TryAdd — custom parser registered before wins; document).
- Test: `tests/Rag.NET.Api.Tests/Webhooks/WebhookEndpointTests.cs` (TestServer, substituted `IIngestionJobQueue`):

```csharp
// Valid signature single doc → 202 { enqueued: 1 }, queue received job with matching id/content.
// Array payload → 202 { enqueued: N }. Bad signature → 401 (queue untouched). Missing header → 401.
// sha256= prefixed signature → 202. Invalid JSON → 400. Parser-rejected payload (missing content) → 400.
// No queue registered → 503. Custom IWebhookPayloadParser honored.
// ApiKeyMiddleware: webhook route works WITHOUT api key while /ingest still requires it.
// Unit: WebhookSignatureValidator hex/prefix/timing-safe cases.
```

**Commit** `feat(api): HMAC-verified webhook ingestion endpoint`

### Task B3: polling trigger + docs

**Files:**
- Create: `src/Rag.NET.DataProviders/EventDriven/BackgroundPollingTrigger.cs` — `BackgroundService`: loop `while (!stoppingToken.IsCancellationRequested)`: run `pipeline.IngestFromProviderAsync(provider, providerId, hashStore, options, ct)` (read `RagPipelineExtensions` for the exact signature), log cycle summary (ingested/skipped/errors counts — the result type has them; read it), `Task.Delay(PollingInterval, stoppingToken)`; per-cycle exception → warning + next cycle; OCE-on-stopping → clean exit.
- Create: `PollingIngestionOptions` (Abstractions) — `PollingInterval` (default 5 min, validated > TimeSpan.Zero), `ProviderId` (string, required non-empty).
- DI: `UsePollingIngestion(Func<IServiceProvider, IFileContentProvider> providerFactory, Action<PollingIngestionOptions> configure)` — required configure; each call registers an independent hosted `BackgroundPollingTrigger` (closure over factory + own options instance — NOT a shared singleton options; multiple pollers must not collide).
- Test: `tests/Rag.NET.DataProviders.Tests/EventDriven/BackgroundPollingTriggerTests.cs` — fake provider + 20ms interval: >= 2 cycles observed (TaskCompletionSource counting), provider failure in cycle 1 → cycle 2 still runs, StopAsync exits cleanly; DI: two registrations → two hosted services.
- Docs: ingestion/data-providers guide — new "Event-driven ingestion" section (queue + processor + webhook setup incl. signature computation example + curl, polling trigger, Service Bus deferred note); features.md Webhook row tick + Status (webhook + polling delivered; Service Bus + provider-specific parsers deferred).

**Commit** `feat(data-providers): background polling trigger + event-driven ingestion docs`

---

## Part C — Embedding versioning & re-indexing

### Task C1: version store + identity

**Files:**
- Create: `src/Rag.NET.Abstractions/Abstractions/IEmbeddingVersionStore.cs` (design §3a shape).
- Create: `src/Rag.NET/Storage/SqliteEmbeddingVersionStore.cs` — mirror `SqliteContentHashStore` conventions EXACTLY (read it: connection handling, init lock, collection-name guard if applicable): table `embedding_versions(doc_id TEXT PRIMARY KEY, model_id TEXT NOT NULL, dimension INTEGER NOT NULL, embedded_at TEXT NOT NULL)`, `INSERT OR REPLACE`.
- Create: `src/Rag.NET/Ingestion/EmbeddingModelIdentity.cs` — internal static resolver: `Resolve(IEmbeddingGenerator<string, Embedding<float>> embedder, EmbeddingVersioningOptions options)` → `options.ModelId` if set, else `embedder.GetService<EmbeddingGeneratorMetadata>()` → `"{ProviderName}/{ModelId}"` (null-safe), else null (versioning disabled). Verify the exact `GetService` pattern against Microsoft.Extensions.AI 10.x — read how the repo queries generator metadata anywhere, or check the package surface; do not guess.
- Create: `EmbeddingVersioningOptions` (Abstractions) — `ModelId` (string?, explicit override), `DatabasePath` (follow SqliteContentHashStore's path convention).
- Test: `tests/Rag.NET.Tests/Storage/SqliteEmbeddingVersionStoreTests.cs` (temp db file, follow SqliteContentHashStore tests): round-trip, upsert replaces, remove, GetAll. `EmbeddingModelIdentityTests`: override wins; metadata path; neither → null.

**Commit** `feat(storage): embedding version store + model identity resolution`

### Task C2: stamping + delete integration

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs` — `[Inject(Required=false)] IEmbeddingVersionStore?` + embedder + options; after `DataManager?.Add(...)`: when store registered and identity resolves, `SetAsync(docId, modelId, dimension)` (dimension = first embedded chunk's vector length; skip stamping when zero chunks); failure → warning (new LoggerMessage), ingestion still succeeds; one-time warning when store registered but identity unresolvable.
- Modify: `src/Rag.NET/Ingestion/PipelineIngestor.cs` `DeleteAsync` — also `RemoveAsync` from the version store (`Required=false` inject).
- DI: `UseEmbeddingVersioning(Action<EmbeddingVersioningOptions>? configure = null)` in core `RagBuilderExtensions` — registers options + `IEmbeddingVersionStore` (Sqlite) singleton with `InitializeAsync` on first use (match SqliteContentHashStore registration timing).
- Test: StorageBehavior tests (append): stamped after store with resolved identity; not stamped when unresolvable (+ warning); stamp failure doesn't fail ingestion; DeleteAsync removes version row; DI test.

**Commit** `feat(ingestion): stamp embedding model version on store; remove on delete`

### Task C3: ReindexStaleAsync + docs + roadmap

**Files:**
- Create: `src/Rag.NET/Pipeline/RagPipelineReindexExtensions.cs` — `ReindexStaleAsync(this IRagPipeline pipeline, IServiceProvider services, CancellationToken ct)`? NO — extension needs services; better: instance method surface. Read how `IngestFromProviderAsync` gets its dependencies (extension on IRagPipeline taking them as parameters) and MATCH that pattern: `ReindexStaleAsync(this IRagPipeline pipeline, IEmbeddingVersionStore versionStore, IEmbeddingGenerator<...> embedder, IVectorStore vectorStore, IRagDataManager? dataManager, EmbeddingVersioningOptions options, CancellationToken ct = default)` — plus a DI-friendly overload resolving from an `IServiceProvider`. Logic per design §3b: resolve identity (null → InvalidOperationException, actionable); `GetAllAsync` → stale where modelId or dimension differs; no data manager → all stale into `ReportedStale`; else per doc: `dataManager.GetChunksAsync(docId)` (read IRagDataManager for the exact member) → re-embed texts in batches (`EmbedBatchSize` from IngestionOptions defaults — reuse a small shared batching helper if extraction from EmbeddingBehavior is clean, else local loop), build `EmbeddedChunk` list, `vectorStore.StoreAsync` (replaces by key), sparse: when `ISparseEmbeddingGenerator` + `ISparseSearchable` available regenerate + `StoreSparseAsync` (failure → warning, dense proceeds), `SetAsync` new stamp; per-doc failure → `Failed` list, continue.
- Create: `ReindexResult` (Abstractions/Models) — `Reindexed`, `ReportedStale`, `Failed` lists.
- Test: `tests/Rag.NET.Tests/Pipeline/ReindexStaleTests.cs` — fake version store + substituted embedder/vector store/data manager: stale detection (model change + dimension change), fresh docs untouched, re-embed + re-store + restamp verified by captured args, report-only path without data manager, per-doc failure collection (doc 2 of 3 fails → 1,3 reindexed), unresolvable identity throws, cancellation.
- Docs: guide section (model migration walkthrough: register UseEmbeddingVersioning at first ingest → switch model → ReindexStaleAsync; limitations: chunks reused verbatim, no re-parse; report-only without data manager); features.md Embedding Versioning row tick + Status (CLI command deferred to Milestone 3).
- `docs/planning/ROADMAP.md` + `MILESTONE.md`: Phase 1.3 complete.

**Commit** `feat(pipeline): ReindexStaleAsync + embedding versioning docs; tick feature; complete phase 1.3`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Full `dotnet test tests/Rag.NET.Tests` + `tests/Rag.NET.DataProviders.Tests` + `tests/Rag.NET.Api.Tests` green.
3. features.md: exactly three rows newly ticked; contradiction in the batch section gone.
4. Final whole-phase review (superpowers:requesting-code-review) over the branch range.
