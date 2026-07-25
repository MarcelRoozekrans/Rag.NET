# Resilience & Cost Controls — Design (Phase 1.4)

**Date:** 2026-07-25
**Milestone:** 1 — Feature Backlog, Phase 1.4
**Covers features.md rows:** LLM Fallback Chain; Rate Limiting & Cost Budgeting

## Scope decisions (agreed)

1. **Fallback chain**: `FallbackChatClient` already exists (implemented, tested, benchmarked);
   the deliverable is DI registration (`UseFallbackChain`), docs, and resolving the
   features.md contradiction (detail section says Done, table row unticked).
2. **Rate limiting** waits (throttles) rather than rejects, backed by
   `System.Threading.RateLimiting.TokenBucketRateLimiter` — no hand-rolled bucket math.
3. **Cost budgeting** persists to SQLite (`SqliteCostLedger`) so daily/monthly windows
   survive restarts; in-memory implementation ships alongside for tests/dev.
4. Prices are user-supplied via options — no built-in price table (prices churn).
5. Known pre-existing issue flagged, NOT fixed here: `RagBuilder.ConfigureResilience`
   registers a Polly pipeline ("rag-net") that nothing consumes.

## 1. LLM Fallback Chain (registration + docs)

**Package:** core `Rag.NET` (implementation exists at `src/Rag.NET/Resilience/FallbackChatClient.cs`)

- `FallbackChainOptions`: `IReadOnlyList<Func<IServiceProvider, IChatClient>> Clients`
  (ordered, >= 2 validated), `TimeSpan? PerClientTimeout` (maps to the existing ctor surface —
  verify against the actual FallbackChatClient ctor during planning).
- `UseFallbackChain<TBuilder>(Action<FallbackChainOptions> configure)`: required configure;
  registers `IChatClient` singleton = `FallbackChatClient` over the materialized clients.
  Supersedes any prior `IChatClient` registration (last-wins, documented — same convention
  as `UseFederatedSearch`). Individual clients are built from factories so they can wrap
  provider registrations without the chain wrapping itself.
- Docs: resilience section in the guide (where answer engines/extending are documented);
  features.md row ticked and detail Status corrected.

## 2. Rate Limiting

**Package:** core `Rag.NET` (`src/Rag.NET/Resilience/`)

```csharp
public interface IRateLimiter : IDisposable
{
    /// <summary>Waits until a permit is available. Throws only on cancellation.</summary>
    ValueTask AcquireAsync(int permits = 1, CancellationToken ct = default);
}
```

- `TokenBucketRateLimiterAdapter : IRateLimiter` — wraps
  `System.Threading.RateLimiting.TokenBucketRateLimiter` (tokens/period from options,
  unbounded queue by default with an optional `MaxQueuedRequests` → when exceeded the
  framework limiter rejects: surfaced as `InvalidOperationException` with guidance).
- Decorators:
  - `RateLimitedChatClient : IChatClient` — acquire 1 permit before each
    `GetResponseAsync`/`GetStreamingResponseAsync` (streaming acquires before the first token);
    `GetService` delegates; Dispose disposes nothing it doesn't own.
  - `RateLimitedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>` —
    acquire 1 permit per `GenerateAsync` call (per-request, not per-item; documented —
    chunk batching from Phase 1.3 makes calls the natural unit).
- `RateLimitingOptions`: `ChatRequestsPerMinute?`, `EmbeddingRequestsPerMinute?` (null =
  that surface unlimited; at least one must be set), `MaxQueuedRequests?`.
- `UseRateLimiting(Action<RateLimitingOptions> configure)`: decorates whatever
  `IChatClient` / `IEmbeddingGenerator` registration is present at build time using the
  Security package's concrete-type-then-interface pattern (resolve previous registration,
  wrap, re-register interface). Requires the underlying registration to exist (actionable
  error otherwise).
- Telemetry: `ragnet.ratelimit.wait.duration` histogram (ms, tagged surface=chat|embedding).
- Permits are request-count v1; token-weighted permits documented as future work.

## 3. Cost Budgeting

**Package:** core `Rag.NET` (`src/Rag.NET/Resilience/` + `src/Rag.NET/Storage/`)

### 3a. Ledger

```csharp
public interface ICostLedger
{
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Adds a usage record to the current day's bucket.</summary>
    Task RecordAsync(CostEntry entry, CancellationToken ct = default);
    /// <summary>Total cost within the window ending today (inclusive).</summary>
    Task<decimal> GetSpendAsync(CostWindow window, CancellationToken ct = default);
}

public sealed record CostEntry
{
    public required CostKind Kind { get; init; }        // Chat | Embedding
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required decimal Cost { get; init; }         // computed by the caller from options
}

public enum CostWindow { Day, Month }
```

- `SqliteCostLedger` — table `cost_ledger(day TEXT, kind TEXT, tokens_in INTEGER,
  tokens_out INTEGER, cost TEXT, PRIMARY KEY(day, kind))` with accumulate-on-conflict
  (`INSERT ... ON CONFLICT DO UPDATE SET ... + excluded...`); cost stored as invariant
  string (SQLite has no decimal). Same conventions as `SqliteEmbeddingVersionStore`.
- `InMemoryCostLedger` — dictionary + lock, for tests/dev.
- Time: injectable `TimeProvider` (framework) so window rollover is testable; day key =
  UTC date.

### 3b. Budget enforcement + tracking

- `CostBudgetOptions`: `InputPricePerMTokens`, `OutputPricePerMTokens`,
  `EmbeddingPricePerMTokens` (decimals, >= 0), `DailyLimit?`, `MonthlyLimit?` (decimal,
  at least one set), `DatabasePath`.
- `BudgetExceededException : InvalidOperationException` — carries window, limit, spend.
- `CostTrackingChatClient : IChatClient` decorator:
  1. Before the call: `GetSpendAsync(Day/Month)` vs limits → throw `BudgetExceededException`
     when already at/over (check is pre-call; a single call may overshoot — documented).
  2. After the call: token counts from `ChatResponse.Usage` (`InputTokenCount`/
     `OutputTokenCount`) when present; else tiktoken estimation over the messages/response
     (cl100k, `ConversationMemoryPipeline` counting pattern; estimation flagged in the
     entry? no — keep the ledger simple, note estimation in docs). Compute cost from
     options; `RecordAsync`. Streaming: record after the stream completes using the final
     usage update when the provider emits one, else estimate from accumulated text.
  3. Ledger failure → log warning, call proceeds (degraded-never-broken; budget enforcement
     is best-effort under storage failure — documented).
- `CostTrackingEmbeddingGenerator` — same shape; input tokens estimated via tiktoken
  (providers rarely report embedding usage), output 0.
- `UseCostBudgeting(Action<CostBudgetOptions> configure)`: registers ledger + decorators
  (same decoration pattern as rate limiting).
- Telemetry: `ragnet.llm.tokens` counter (tagged direction=in|out, kind=chat|embedding),
  `ragnet.llm.cost` counter (unit "usd" — documented as "options currency").

### 3c. Stacking order

When multiple features are registered:
`CostTracking (budget gate) → RateLimited (throttle) → FallbackChain → provider(s)`.
Cheapest gate first: a blown budget never consumes a rate permit; a throttled call never
starts a fallback sequence. Registration-order independence achieved by each Use* wrapping
whatever is currently registered — the documented recommended order is
`UseFallbackChain → UseRateLimiting → UseCostBudgeting` (inner to outer). Docs show the
canonical composition snippet.

## Error handling summary

House posture: rate-limit waits honor cancellation; ledger failures degrade to untracked
calls with warnings; `BudgetExceededException` is deliberate and loud (the one place a
throw is the feature); fallback chain semantics unchanged (existing implementation).

## Testing

- Fallback: DI registration tests (chain resolves, order honored via scripted failures,
  supersedes prior registration); existing FallbackChatClientTests remain the behavior suite.
- Rate limiting: adapter over a tight bucket — second acquire waits until replenish
  (TimeProvider-driven, deterministic); decorator acquires before underlying call (order
  captured); cancellation during wait; wait-duration telemetry emitted; queue-limit rejection.
- Ledger: SQLite round-trip/accumulate/window math (day/month boundaries incl. month edges,
  UTC), in-memory parity; decimal round-trip through TEXT.
- Budget: pre-call gate (under → proceeds, at/over → BudgetExceededException with window+
  numbers), Usage-based recording vs estimation fallback (substitute with/without Usage),
  streaming recording, ledger-failure degradation, embedding estimation.
- Stacking: all three registered → call order proven via a recording provider client.

## Out of scope

- Wiring the dangling `ConfigureResilience` Polly pipeline (pre-existing; tracked as a
  known issue).
- Token-weighted rate permits; built-in price tables; per-user/multi-tenant budgets;
  budget alerts/webhooks.
