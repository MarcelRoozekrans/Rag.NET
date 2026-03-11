# Pipeline Fortification Design

**Goal:** Add resiliency, observability, and idempotency to `RagPipeline` without changing provider implementations.

**Architecture:** Inline `ActivitySource` + `[LoggerMessage]` into `RagPipeline`; wire Polly retry pipeline via `RagPipelineBuilder`; add opt-in `Overwrite` flag on new `IngestionOptions`.

**Tech Stack:** `Microsoft.Extensions.Resilience` (Polly v8), `System.Diagnostics.ActivitySource` (OpenTelemetry), `Microsoft.Extensions.Logging` with `[LoggerMessage]` source generator.

---

## Section 1: Architecture

Three orthogonal capabilities added to `RagPipeline`, wired via the existing builder:

**Observability** — `RagPipeline` gains an `ActivitySource` static field (`"Rag.NET"`) and an `ILogger<RagPipeline>` constructor parameter (optional, defaulting to `NullLogger`). Every public method starts an `Activity` span and emits `[LoggerMessage]` calls at key points (ingest start/complete, retrieve, ask). No new abstractions.

**Resiliency** — `RagPipelineBuilder` accepts a `ConfigureResilience(Action<ResiliencePipelineBuilder<object>>)` overload. Internally it wraps `IEmbeddingGenerator` calls and `IVectorStore` calls in a `ResiliencePipeline` (Polly v8). Default policy: 3 retries, exponential backoff, jitter. Users can override or disable.

**Idempotency** — `IngestAsync` gets an `IngestionOptions` parameter (new class, optional). When `IngestionOptions.Overwrite = true`, pipeline calls `vectorStore.DeleteByDocumentIdAsync` before storing. When `false` (default), behaviour is unchanged.

No new projects. All changes in `Rag.NET` (core) only.

---

## Section 2: Components

**New files:**

- `src/Rag.NET/Models/Options/IngestionOptions.cs` — `bool Overwrite = false`
- `src/Rag.NET/Telemetry/RagActivitySource.cs` — static `ActivitySource` instance (`"Rag.NET"`, version from assembly), plus constants for activity names (`"ingest"`, `"retrieve"`, `"ask"`)
- `src/Rag.NET/Logging/RagPipelineLog.cs` — static partial class with `[LoggerMessage]` methods: `IngestStarted`, `IngestCompleted`, `IngestFailed`, `RetrieveStarted`, `AskStarted`, `RetryAttempt`

**Modified files:**

- `src/Rag.NET/Pipeline/RagPipeline.cs` — add `ILogger<RagPipeline>` constructor param; wire `ActivitySource` and `[LoggerMessage]` calls; add `IngestionOptions?` param to `IngestAsync`; wrap embedding + store calls in resilience pipeline
- `src/Rag.NET/Pipeline/RagPipelineBuilder.cs` — add optional `ILogger<RagPipeline>` registration; add `ConfigureResilience(Action<ResiliencePipelineBuilder<object>>)` with default 3-retry exponential policy
- `src/Rag.NET/Abstractions/IRagPipeline.cs` — add `IngestionOptions?` param to `IngestAsync`

**No changes** to vector store providers — resiliency wraps at the pipeline level.

---

## Section 3: Data Flow

**IngestAsync:**
1. Start `Activity("ingest")` with tags `document_id`, `content_type`
2. Log `IngestStarted(documentId, contentType)`
3. If `options?.Overwrite == true` → call `vectorStore.DeleteByDocumentIdAsync` first
4. Parse → chunk → embed (embedding call wrapped in resilience pipeline) → store (store call wrapped)
5. Log `IngestCompleted(documentId, chunksStored)`
6. Set activity tag `chunks_stored`; stop activity

**RetrieveAsync:**
1. Start `Activity("retrieve")` with tags `top_k`, `use_hybrid_search`
2. Log `RetrieveStarted(query, topK)`
3. Embed query (wrapped) → search (wrapped)
4. Set activity tag `results_count`; stop activity

**AskAsync / AskStreamingAsync:**
1. Start `Activity("ask")` with tag `top_k`
2. Log `AskStarted(query)`
3. Delegate to `RetrieveAsync` (already traced)
4. Call `chatClient` (not wrapped — chat clients have their own retry semantics)
5. Stop activity

---

## Section 4: Error Handling

**Retry failures:** When Polly exhausts all retries, the original exception propagates unchanged. No wrapping in custom exception types.

**Activity on exception:** Each `Activity` has a `try/catch` that calls `activity.SetStatus(ActivityStatusCode.Error, exception.Message)` before rethrowing. Traces reflect failures in OTEL backends.

**Logging on exception:** `[LoggerMessage]` methods `IngestFailed`, `RetrieveFailed`, `AskFailed` logged at `LogLevel.Error` inside catch. Logger defaults to `NullLogger` — no null checks needed.

**Overwrite race:** `DeleteByDocumentIdAsync` followed by `StoreAsync` is not atomic. Concurrent ingestion of the same `documentId` with `Overwrite = true` is not safe. This is documented behavior — callers control concurrency.

**`IngestionOptions` null:** `null` treated as `new IngestionOptions()` (Overwrite = false), existing behavior preserved.

---

## Section 5: Testing

**Unit tests** (`tests/Rag.NET.Tests/`):

- `IngestionOptionsTests`
  - Default `Overwrite = false`
  - `IngestAsync` with `Overwrite = true` calls `DeleteByDocumentIdAsync` before `StoreAsync`
  - `IngestAsync` with `Overwrite = false` (or null options) skips delete

- `RagPipelineObservabilityTests`
  - Mock `ActivityListener` verifies activities are started/stopped with correct tags
  - Verify `IngestFailed` activity has `ActivityStatusCode.Error` on exception

- `RagPipelineResilienceTests`
  - Mock embedding generator throws twice then succeeds; verify 3rd call returns result
  - Verify exhausted retries (all 3 fail) propagate the original exception

**Integration tests** — no new integration tests; existing Qdrant/PgVector tests exercise the full pipeline path.

**Package additions:**
- `Microsoft.Extensions.Resilience` to `src/Rag.NET/Rag.NET.csproj`
- No new test packages needed
