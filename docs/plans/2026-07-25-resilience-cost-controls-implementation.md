# Resilience & Cost Controls Implementation Plan (Phase 1.4)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the two backlog resilience features: DI-wired LLM fallback chain (with per-client timeout) and rate limiting + SQLite-persisted cost budgeting as `IChatClient`/`IEmbeddingGenerator` decorators.

**Architecture:** Per `docs/plans/2026-07-25-resilience-cost-controls-design.md`. Part A wires the existing `FallbackChatClient` (adding the missing `PerClientTimeout`); Part B adds `IRateLimiter` (framework token bucket) + decorators; Part C adds the cost ledger (SQLite + in-memory), budget-enforcing decorators, stacking docs, and roadmap close-out. A shared internal `ServiceDecorationHelper` lets B/C decorate whatever `IChatClient`/`IEmbeddingGenerator` the user registered.

**Tech Stack:** .NET 10, xUnit v3 + NSubstitute, System.Threading.RateLimiting, Microsoft.Data.Sqlite, `TimeProvider`, Microsoft.Extensions.AI (`ChatResponse.Usage`), Microsoft.ML.Tokenizers (estimation fallback).

**Conventions:** as previous phases — options POCOs in Abstractions, LoggerMessage, OCE-first, MA0051/MA0015/ZA0601/ZA0501/EPS05/HLQ warnings-as-errors, commit trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`, filtered tests during work + one `dotnet build Rag.NET.slnx` per part. Test precedent: `tests/Rag.NET.Tests/Resilience/FallbackChatClientTests.cs`.

---

## Part A — Fallback chain wiring

### Task A1: `PerClientTimeout` on `FallbackChatClient`

**Files:** Modify `src/Rag.NET/Resilience/FallbackChatClient.cs` (read fully — 160 lines; ctor is `(IReadOnlyList<IChatClient>, ILogger?)`); Test append `tests/Rag.NET.Tests/Resilience/FallbackChatClientTests.cs`.

- New optional ctor param `TimeSpan? perClientTimeout = null` (validated > 0 when set). In both `GetResponseAsync` and the per-client streaming attempt: when set, wrap the client call with `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` + `CancelAfter(timeout)`; a timeout-triggered OCE (linked token fired, caller's token NOT cancelled) is treated as TRANSIENT (falls to the next client — reuse the Part-1.2/1.3 `when` filter discipline: `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` → transient path; caller cancellation still rethrows immediately). Mind the existing OCE-rethrow ordering.
- Tests: (1) primary hangs (TaskCompletionSource never completes) + 50ms timeout → secondary serves; (2) caller cancellation during a hung primary → OCE propagates, secondary untouched; (3) no timeout set → existing behavior (hung primary hangs — assert via a not-completed task, then cancel to clean up); (4) streaming variant of (1).

**Commit** `feat(resilience): per-client timeout on FallbackChatClient`

### Task A2: `UseFallbackChain` + docs + tick

**Files:** Create `src/Rag.NET.Abstractions/Models/Options/FallbackChainOptions.cs` (`List<Func<IServiceProvider, IChatClient>> Clients` + `AddClient(...)` convenience, `TimeSpan? PerClientTimeout`); Modify `src/Rag.NET/DependencyInjection/RagBuilderExtensions.cs` — `UseFallbackChain<TBuilder>(Action<FallbackChainOptions> configure)`: required configure, >= 2 clients validated, `AddSingleton<IChatClient>(sp => new FallbackChatClient(options.Clients.Select(f => f(sp)).ToList(), timeout, logger))` (materialize factories once; ZA0601 — use a loop not LINQ). xmldoc: supersedes prior `IChatClient` registrations (last-wins), factories let per-provider clients be built without self-wrapping. Test `tests/Rag.NET.Tests/DependencyInjection/UseFallbackChainTests.cs`: resolves FallbackChatClient; order honored end-to-end (scripted first-client transient failure → second serves); < 2 clients throws; supersedes a prior AddSingleton<IChatClient>. Docs: new `docs/guide/resilience.md` (fallback section; rate-limit/budget sections land in B/C) linked from wherever the guide index lives (check docs/guide structure); features.md: tick "LLM Fallback Chain" row + correct the detail Status ("Done" → delivered incl. registration + per-client timeout).

**Commit** `feat(resilience): UseFallbackChain DI extension + resilience guide; tick feature`

---

## Part B — Rate limiting

### Task B1: `IRateLimiter` + adapter + decorators

**Files:** Create `src/Rag.NET.Abstractions/Abstractions/IRateLimiter.cs` (design §2 shape); Create `src/Rag.NET/Resilience/TokenBucketRateLimiterAdapter.cs` — wraps `TokenBucketRateLimiter` (`TokensPerPeriod`/`ReplenishmentPeriod` derived from per-minute option: tokens = rpm, period = 1 min? NO — smoother: tokens = max(1, rpm/60), period = 1s, bucket limit = rpm burst; document the derivation and pin with tests), `QueueLimit` from `MaxQueuedRequests ?? int.MaxValue`, `AcquireAsync` → `limiter.AcquireAsync(permits, ct)`; lease not acquired (queue full) → `InvalidOperationException` with guidance; records `ragnet.ratelimit.wait.duration` (new histogram in `src/Rag.NET/Telemetry/RagTelemetry.cs`, tag surface) measured around the acquire; IDisposable disposes the inner limiter. **TimeProvider:** `TokenBucketRateLimiterOptions` has `AutoReplenishment`; for deterministic tests construct with auto-replenishment ON for production and expose an internal ctor seam taking a pre-built `RateLimiter` for tests (simplest deterministic route — tests use a manual-replenishment `TokenBucketRateLimiter` and call `TryReplenish`).
- Create `src/Rag.NET/Resilience/RateLimitedChatClient.cs` + `RateLimitedEmbeddingGenerator.cs` — acquire 1 permit before each call (streaming: before iteration starts, outside the try-with-yield constraint), delegate everything else (`GetService`, Dispose non-owning).
- Test `tests/Rag.NET.Tests/Resilience/RateLimiterTests.cs` + `RateLimitedDecoratorTests.cs`: bucket math derivation pinned; deterministic wait via manual-replenish seam (acquire 2 on a 1-token bucket → second waits until TryReplenish); cancellation during wait; queue-full rejection message; decorators: underlying called only after acquire (order captured), streaming acquires once, GetService delegation.

**Commit** `feat(resilience): IRateLimiter token-bucket adapter + rate-limited decorators`

### Task B2: `ServiceDecorationHelper` + `UseRateLimiting`

**Files:** Create `src/Rag.NET/DependencyInjection/ServiceDecorationHelper.cs` — internal static `Decorate<TService>(IServiceCollection services, Func<TService, IServiceProvider, TService> decorate)`: find the LAST descriptor for `TService` (throw actionable if none: "register your IChatClient before UseRateLimiting"), remove it, re-add a singleton factory resolving the original (handle ImplementationInstance / ImplementationFactory / ImplementationType via `ActivatorUtilities`) then applying `decorate`. Unit-test all three descriptor shapes directly (`ServiceDecorationHelperTests`).
- `RateLimitingOptions` (Abstractions): `ChatRequestsPerMinute?`, `EmbeddingRequestsPerMinute?`, `MaxQueuedRequests?`; validate at least one surface set, values > 0.
- `UseRateLimiting<TBuilder>(Action<RateLimitingOptions> configure)` in core RagBuilderExtensions: required configure; when chat rpm set → `Decorate<IChatClient>` with `RateLimitedChatClient` sharing one chat `IRateLimiter` singleton; embedding likewise with its own limiter. Missing underlying registration → the helper's actionable throw surfaces at registration time (descriptor lookup is registration-time — good).
- Tests `UseRateLimitingTests`: decorates prior registration (resolved IChatClient is RateLimitedChatClient wrapping the original — verify by invoking through and capturing); chat-only config leaves embeddings undecorated; no prior IChatClient + chat rpm set → actionable throw; both surfaces get independent limiters.

**Commit** `feat(resilience): UseRateLimiting with service decoration helper`

---

## Part C — Cost budgeting

### Task C1: ledger contracts + implementations

**Files:** Create `src/Rag.NET.Abstractions/Abstractions/ICostLedger.cs` + `Models/CostEntry.cs` + `Models/CostWindow.cs` (design §3a shapes); Create `src/Rag.NET/Storage/SqliteCostLedger.cs` — conventions per `SqliteEmbeddingVersionStore` (read it), schema per design (accumulate-on-conflict upsert; cost as invariant-culture TEXT decimal; day key from injected `TimeProvider.GetUtcNow().Date` "yyyy-MM-dd"); `GetSpendAsync(Day)` = today's rows; `(Month)` = rows where day >= first of current UTC month. Create `src/Rag.NET/Resilience/InMemoryCostLedger.cs` (dictionary + lock, same semantics).
- Tests: `SqliteCostLedgerTests` (temp db): record/accumulate (two records same day+kind sum), day vs month windows incl. month-boundary case via a fake `TimeProvider` (record on Jul 31, query on Aug 1 → Day=0, Month=only-Aug), decimal round-trip precision (e.g. 0.000123m), restart persistence. `InMemoryCostLedgerTests` parity subset.

**Commit** `feat(storage): cost ledger contracts + SQLite and in-memory implementations`

### Task C2: budget decorators + estimation

**Files:** Create `src/Rag.NET.Abstractions/Models/Options/CostBudgetOptions.cs` (prices >= 0, at least one of DailyLimit/MonthlyLimit, DatabasePath default "rag-cost-ledger.db"); Create `src/Rag.NET.Abstractions/Models/BudgetExceededException.cs` (`: InvalidOperationException`, carries Window/Limit/Spend properties, message includes all three); Create `src/Rag.NET/Resilience/CostTrackingChatClient.cs` + `CostTrackingEmbeddingGenerator.cs` per design §3b:
  - Pre-call gate: check Day then Month spend vs configured limits → `BudgetExceededException`. Ledger read failure → log warning (`RagPipelineLog` new entries) + proceed ungated.
  - Post-call record: chat — `response.Usage?.InputTokenCount/OutputTokenCount` when BOTH present, else tiktoken cl100k estimation (messages concat for input, response text for output; static readonly tokenizer, `ConversationMemoryPipeline` counting pattern); cost = tokens/1_000_000m * price. Streaming: accumulate text + capture a final `ChatResponseUpdate` usage if the provider emits one (check `ChatResponseUpdate` surface for usage content — verify against Microsoft.Extensions.AI 10.x; if usage arrives via `UsageContent` in updates, use it, else estimate); record once after enumeration completes (finally-style — but do NOT record on cancellation mid-stream; document). Embedding — estimate input tokens, output 0.
  - Record failure → warning, never fails the call. Telemetry: `ragnet.llm.tokens` + `ragnet.llm.cost` counters (new instruments in RagTelemetry, tags per design).
- Tests `CostTrackingChatClientTests` + `CostTrackingEmbeddingGeneratorTests`: gate under/at/over for both windows (fake ledger returning canned spend; exception carries window+limit+spend); Usage-based recording (substitute returns ChatResponse with UsageDetails) vs estimation (no Usage — assert recorded tokens equal tiktoken counts computed in the test); streaming record-once; ledger-failure degradation both directions; embedding estimation.

**Commit** `feat(resilience): cost-tracking decorators with budget enforcement`

### Task C3: `UseCostBudgeting` + stacking + docs + roadmap

**Files:** `UseCostBudgeting<TBuilder>(Action<CostBudgetOptions> configure)` in core RagBuilderExtensions: required configure + validation; registers `ICostLedger` (Sqlite, TryAdd so a custom/in-memory ledger registered earlier wins — document) + `Decorate<IChatClient>`/`Decorate<IEmbeddingGenerator<...>>` with the cost decorators.
- Stacking test `ResilienceStackingTests`: register a recording provider client, then `UseFallbackChain` (chain of the recorder), `UseRateLimiting`, `UseCostBudgeting` in the documented order → resolved IChatClient unwraps as CostTracking(RateLimited(Fallback(recorder))) — prove by call-order capture (budget ledger read → limiter acquire → recorder called) and by GetService type probing if the decorators expose inner types via GetService (implement GetService delegation consistently).
- Docs: complete `docs/guide/resilience.md` (rate limiting + budgeting sections, canonical composition snippet with the recommended order and WHY; estimation caveat; ledger degradation; budget overshoot-by-one-call caveat; ConfigureResilience dangling-pipeline known-issue note). features.md: tick "Rate Limiting & Cost Budgeting" row + Status. `docs/planning/ROADMAP.md` + `MILESTONE.md`: Phase 1.4 complete (2026-07-25).

**Commit** `feat(resilience): UseCostBudgeting + stacking docs; tick feature; complete phase 1.4`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Full `dotnet test tests/Rag.NET.Tests` green (+ DataProviders/Api suites for regression).
3. features.md: both rows ticked, fallback contradiction resolved.
4. Final whole-phase review over the branch range; merge decision.
