# Group B: Production Resilience — Design

## Goal

Add two production-readiness features: an LLM fallback chain and a batch ingestion parallelism option.

---

## Feature 1: LLM Fallback Chain

**Package:** `Rag.NET` (core)

### Component

`FallbackChatClient : IChatClient` — a decorator that wraps a prioritised list of `IChatClient` instances. On a transient failure from the primary client, it retries with the next client in the list. If all clients fail, the last exception is rethrown.

### Transient error classification

An exception is transient if it is:
- `HttpRequestException` with status code 429, 503, or no response
- `TaskCanceledException` or `TimeoutException`
- Any exception whose message contains `"rate limit"`, `"throttl"`, `"timeout"`, or `"unavailable"` (case-insensitive)

All other exceptions propagate immediately without trying the next client.

### Behavior

- Implements both `GetResponseAsync` and `GetStreamingResponseAsync`.
- Streaming: on transient failure during `MoveNextAsync`, re-issues the full request to the next client (streaming cannot be resumed mid-stream).
- Logs one `LogWarning` per fallback attempt: which client failed, the exception message, and which client is next.
- Constructor: `FallbackChatClient(IReadOnlyList<IChatClient> clients, ILogger<FallbackChatClient>? logger = null)`

### Registration

No DI extension needed — consumers construct and register directly:
```csharp
services.AddSingleton<IChatClient>(sp => new FallbackChatClient(
[
    sp.GetRequiredService<OpenAIChatClient>(),
    sp.GetRequiredService<AnthropicChatClient>(),
]));
```

### Testing

Unit tests using `NSubstitute`:
- Primary succeeds → secondary never called
- Primary fails transiently → secondary called, returns result
- Primary fails transiently, secondary fails transiently → exception from secondary rethrown
- Primary fails non-transiently → exception propagates immediately, secondary never called
- All clients fail → last exception rethrown
- Streaming: primary transient failure → secondary invoked

### Out of scope

- Per-client timeout configuration
- Jitter/backoff between retries (retries are immediate)
- Retry count limits (if all 3 clients fail once each, it stops)

---

## Feature 2: Batch Ingestion Optimiser

**Package:** `Rag.NET` (core)

### Change 1: `IngestionOptions.MaxDegreeOfParallelism`

Add one property to `src/Rag.NET.Abstractions/Models/Options/IngestionOptions.cs`:
```csharp
public int MaxDegreeOfParallelism { get; init; } = 1;
```

Default `1` preserves current sequential behaviour — no breaking change.

### Change 2: `IngestFromProviderAsync` parallelism

In `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`, replace the sequential `await foreach` loop over provider entries with `Parallel.ForEachAsync` when `MaxDegreeOfParallelism > 1`.

The `seenIds` set (used for cleanup) must be thread-safe: replace `HashSet<string>` with `ConcurrentDictionary<string, byte>` (used as a concurrent set).

The `ingested`/`skipped` counters must be thread-safe: use `Interlocked.Increment`.

The `errors` list must be thread-safe: replace `List<string>` with `ConcurrentBag<string>`.

Single-document `IngestAsync` is unchanged — parallelism only applies at the multi-document provider level.

### Thread-safety assumption

`IVectorStore.StoreAsync` is assumed to be thread-safe (all three existing implementations use connection pooling or HTTP clients that are inherently thread-safe).

### Testing

- `MaxDegreeOfParallelism = 1` (default): behaviour identical to current sequential ingestion
- `MaxDegreeOfParallelism = 4`: multiple documents ingested concurrently; all results present in output
- Error in one document does not prevent others from completing

### Out of scope

- Progress reporting across parallel documents
- Parallelism within a single document's embedding step
