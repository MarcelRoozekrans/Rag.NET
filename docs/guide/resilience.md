---
id: resilience
title: Production Resilience
sidebar_position: 10
---

# Production Resilience

## LLM Fallback Chain

`FallbackChatClient` is an `IChatClient` decorator that tries a prioritised list of clients in order. When the active client raises a **transient** error, the next client in the list is tried automatically. If all clients fail, the last exception is rethrown.

### What counts as transient

An exception is transient if any of the following are true:

| Condition | Examples |
|-----------|---------|
| `HttpRequestException` with HTTP 429 | Rate limit hit |
| `HttpRequestException` with HTTP 503 | Service temporarily unavailable |
| `HttpRequestException` with no status code | Network failure, DNS error |
| `TimeoutException` | Request timed out |
| `TaskCanceledException` — **only when the configured `PerClientTimeout` fired** | Per-client timeout elapsed. A client's *own* cancellation (e.g. a raw `HttpClient` internal timeout surfacing as `TaskCanceledException`) propagates without fallback — configure `PerClientTimeout` to bound hung providers |
| Exception message contains `"rate limit"`, `"throttl"`, `"timeout"`, or `"unavailable"` (case-insensitive) | Provider-specific error text |
| Per-client timeout elapsed (when `PerClientTimeout` is configured) | Hung or slow provider |

All other exceptions propagate immediately — the remaining clients are **not** tried.

### Registering in DI

Use `UseFallbackChain` to register the chain as the pipeline's `IChatClient`:

```csharp
services.AddRagNet(rag => rag
    .UseFallbackChain(o =>
    {
        o.AddClient(sp => new OpenAIChatClient(sp.GetRequiredService<OpenAIClient>(), "gpt-4o"));
        o.AddClient(sp => new AnthropicChatClient(sp.GetRequiredService<AnthropicClient>(), "claude-3-5-sonnet-20241022"));
        o.PerClientTimeout = TimeSpan.FromSeconds(30); // optional
    }));
```

At least 2 clients are required (validated at registration). When the primary client (OpenAI) hits a rate limit, `FallbackChatClient` logs a warning and immediately retries the same request against the secondary (Anthropic), with no delay.

Two things to know about the registration:

- **It supersedes any prior `IChatClient` registration** (standard last-wins container semantics — the same convention as `UseFederatedSearch`). Call `UseFallbackChain` after your provider registrations.
- **Clients are supplied as factories** (`Func<IServiceProvider, IChatClient>`) so each per-provider client can be built from DI without the chain wrapping itself. Do **not** resolve `IChatClient` inside a factory — because the chain *is* the `IChatClient` registration, that would recurse into the chain. Construct the provider client directly, as in the snippet above.

### Per-client timeout

`FallbackChainOptions.PerClientTimeout` (default: unset = unbounded) puts an upper bound on each per-client attempt:

- When the timeout elapses and the **caller's** token has not been cancelled, the attempt counts as a transient failure and the next client is tried — a hung provider no longer stalls the whole chain.
- Caller cancellation always propagates immediately; it is never rerouted to a fallback client.
- If **every** client times out, the caller receives a `TimeoutException` (with the last cancellation as its inner exception) rather than an `OperationCanceledException` — a total provider outage must not masquerade as caller cancellation (e.g. ASP.NET treats OCE as a client disconnect).
- For streaming responses the timeout spans the **whole per-client attempt** (first token through stream completion), not just time-to-first-token. If it fires mid-stream, the chain restarts the request against the next client (see logging below).
- Mid-stream restarts affect consumers: **already-yielded updates are not retracted**. The next client streams the response from the beginning, so a consumer sees the failed client's prefix followed by the full restarted stream — discard accumulated output when a restart is logged if duplicates matter for your use case.
- Because the streaming timeout covers the whole attempt, the clock **keeps running while your consumer processes updates** — a slow consumer eats into the provider's time budget. Size `PerClientTimeout` for end-to-end stream consumption, not just provider latency.

The timeout must be greater than zero when set; this is validated both at registration and by the `FallbackChatClient` constructor.

### Logging

One `LogWarning` is emitted per fallback attempt, including the client index and whether the failure occurred before or during streaming:

```
warn: Rag.NET.Resilience.FallbackChatClient
      Client 0 failed transiently; trying next client.
```

A per-client timeout gets its own message on the non-streaming path:

```
warn: Rag.NET.Resilience.FallbackChatClient
      Client 0 timed out after 00:00:30; trying next client.
```

For streaming responses that fail mid-stream, the log includes how many tokens had been yielded before the failure:

```
warn: Rag.NET.Resilience.FallbackChatClient
      Streaming client 0 failed mid-stream after 12 token(s); restarting with next client.
```

### Out of scope

- Jitter or backoff between attempts (fallback is immediate)
- Retry limits within a single client (each client is tried once)

## Rate limiting

`UseRateLimiting` wraps the registered `IChatClient` and/or `IEmbeddingGenerator<string, Embedding<float>>` with token-bucket rate limiters. Callers over the configured per-minute budget **wait** for a permit rather than being rejected — the throttle smooths bursts instead of surfacing 429-style failures locally.

```csharp
services.AddRagNet(rag =>
{
    rag.Services.AddSingleton<IChatClient>(myProviderClient); // register the client FIRST
    rag.UseRateLimiting(o =>
    {
        o.ChatRequestsPerMinute = 300;
        o.EmbeddingRequestsPerMinute = 1200;
        o.MaxQueuedRequests = 100; // optional: bound the wait queue (unbounded by default)
    });
});
```

Each configured surface gets its own independent limiter. A surface whose budget is left `null` stays unlimited and undecorated. `UseRateLimiting` decorates whatever is registered when it runs, so the underlying client/generator must be registered first (a configured surface with no registration fails at registration time). Repeat calls are idempotent per surface — the first configuration wins; budgets never stack.

### Bucket derivation

The per-minute budget is spread over 1-second replenishment periods (`TokensPerPeriod = max(1, rpm / 60)`), so waits are short and steady instead of a once-a-minute thundering herd. The bucket capacity is the full per-minute budget, letting an idle limiter absorb a burst of up to one minute's worth of calls. Two consequences:

- **Budgets below 60 rpm over-admit**: replenishment floors at 1 token per second, so the *sustained* rate of a sub-60-rpm budget can exceed the configured value (bursts stay bounded by the bucket capacity). Budgets that are not a multiple of 60 floor to the next lower per-second rate.
- Budget-blocked callers wait indefinitely unless `MaxQueuedRequests` is set, in which case overflow calls are rejected with an `InvalidOperationException` (deliberately worded so that a saturated local limiter nested inside a fallback chain is **not** classified as a transient provider failure).

### What a permit covers

- **Permits are per request, not per duration.** A streaming chat call acquires exactly one permit *before the stream starts* and holds nothing while streaming — N concurrent long-lived streams consume N permits at their start times, then stream permit-free. Requests-per-minute is the throttled quantity, not concurrency and not tokens (token-weighted permits are future work).
- **Streaming acquires on first enumeration, not at call time.** `GetStreamingResponseAsync` returns an `IAsyncEnumerable` immediately; the permit is acquired when iteration begins. Code that requests many streams but enumerates them later effectively defers its rate limiting to enumeration time.
- An embedding call acquires one permit per *call*, not per embedded value — chunk batching makes the call the natural unit of provider load.
- **Under the documented stacking** (rate limiter outside the fallback chain — see below), one permit covers the *entire* fallback sequence: a request that falls through three providers consumed one permit, not three.

Wait time is observable via the `ragnet.ratelimit.wait.duration` histogram (ms; tagged `surface=chat|embedding`, `outcome=granted|rejected|cancelled|faulted`).

## Cost budgeting

`UseCostBudgeting` wraps the registered `IChatClient` and/or `IEmbeddingGenerator<string, Embedding<float>>` with cost-tracking decorators backed by a cost ledger, giving you a daily/monthly spend guardrail. The default ledger is **in-memory** — recorded spend resets when the process restarts — so for a guardrail that survives restarts, call `UseSqliteCostLedger()` from the `Rag.NET.Storage.Sqlite` package *before* `UseCostBudgeting`:

```csharp
services.AddRagNet(rag =>
{
    rag.Services.AddSingleton<IChatClient>(myProviderClient); // register the client FIRST
    rag.UseSqliteCostLedger("rag-cost-ledger.db"); // optional: persistent ledger (Rag.NET.Storage.Sqlite)
    rag.UseCostBudgeting(o =>
    {
        o.InputPricePerMTokens = 3m;        // your provider's price per 1M input tokens
        o.OutputPricePerMTokens = 15m;      // ... per 1M output tokens
        o.EmbeddingPricePerMTokens = 0.02m; // ... per 1M embedding input tokens
        o.DailyLimit = 25m;
        o.MonthlyLimit = 400m;              // at least one limit is required
    });
});
```

> **Migrating from a version where the SQLite ledger was built in?** `CostBudgetOptions` has no `DatabasePath` property — the ledger path is configured on `UseSqliteCostLedger(path)` itself, from the `Rag.NET.Storage.Sqlite` package. Call it before `UseCostBudgeting()` to get a persistent ledger; without it, spend is tracked in memory and resets on restart.

Before each call the decorator checks the recorded spend of the current UTC day and month against the configured limits and throws `BudgetExceededException` (carrying `Window`, `Limit`, and `Spend`) once a limit is reached. After each call it records token usage and cost to the ledger. For streaming calls the gate — like the rate limiter's permit — fires on **first enumeration**, not when `GetStreamingResponseAsync` returns. Every registered surface is decorated; at least one must be registered before the call. Repeat calls are idempotent (first configuration wins; decorators never stack).

Things to know:

- **Prices are user-supplied.** There is no built-in price table — provider prices churn too fast to ship. All monetary values (prices, limits, ledger totals, the `ragnet.llm.cost` counter) share whatever currency you quote the prices in.
- **Token counts are estimates unless the provider reports usage.** Chat calls use `ChatResponse.Usage` when the provider reports *both* input and output counts; otherwise both sides are estimated with the tiktoken `cl100k_base` tokenizer (over the request messages and response text). Embedding usage is always estimated (providers rarely report it). Note that `cl100k_base` is an **OpenAI** tokenizer — estimates for non-OpenAI models (Anthropic, Mistral, local models, …) can deviate systematically from the provider's own accounting. Estimated and provider-reported entries are **indistinguishable in the ledger** (an accepted trade-off to keep the schema simple). Treat the ledger as a close approximation, not an invoice.
- **Streaming records once, after the stream completes**, using the usage the provider emitted in the update stream (`UsageContent`) when present, else estimating from the accumulated text. A stream abandoned mid-way — cancelled or faulted — is deliberately **not recorded**: its true usage is unknown, and guessing would corrupt the ledger.
- **The gate is pre-call, so a budget can overshoot by all in-flight calls.** Every call admitted before the limit is reached runs to completion and records its full cost — and under concurrency that is *several* calls, not one: parallel ingestion routinely has N embedding batches in flight (`IngestionOptions.MaxConcurrentEmbeddingBatches` × `MaxDegreeOfParallelism`), all of which pass the gate before any of them records. Size limits with headroom for your concurrency level, not just one call's worth.
- **Ledger failures degrade, never break.** If the ledger cannot be read, the call proceeds ungated (with a warning); if it cannot be written, the call still succeeds (with a warning). Budget enforcement is best-effort under storage failure.
- **The ledger is replaceable.** `UseCostBudgeting` registers the in-memory default with `TryAdd`, so an `ICostLedger` registered *before* the call — `UseSqliteCostLedger()`, or your own store — wins. When the in-memory default is the one in effect, a warning is logged naming `UseSqliteCostLedger()`, because spend limits are then only enforced within a single process lifetime.
- **`SqliteCostLedger` migrates an existing `cost_ledger` table on first open after upgrade.** The `pages` column (for per-page kinds such as `CostKind.Ocr`) was added after the initial release, and `CREATE TABLE IF NOT EXISTS` will not add it to a table an earlier version created — so the ledger probes the table and, when the column is absent, runs `ALTER TABLE cost_ledger ADD COLUMN pages INTEGER NOT NULL DEFAULT 0` **against your database, automatically**, from the constructor. It is additive and metadata-only: no row is rewritten, no column is dropped, and `0` is the true value for pre-existing chat and embedding rows, which were never billed pages. The ALTER is race-guarded, so concurrent openers of one ledger file (scaled-out workers on a shared volume) do not crash on `duplicate column name`.
- **Not everything in the ledger is an LLM call.** The Azure Document Intelligence OCR engine (see [ingestion](ingestion.md#ocr-spend-the-cost-ledger-and-your-budget)) records `CostKind.Ocr` entries carrying `Pages` and zero tokens. Those entries count toward the same daily/monthly window enforced here, so **enabling OCR can trip the chat and embedding gates** — though the OCR call itself is never gated: the engine records spend but is not decorated, so a blown budget stops chat and embedding, not OCR (`PdfParserOptions.MaxOcrPages` is what bounds OCR). They do *not* emit the `ragnet.llm.cost` / `ragnet.llm.tokens` meters below — dashboards built on those meters under-report total spend by exactly the OCR portion; query the ledger for the whole picture.
- Windows are UTC calendar windows: `Day` is the current UTC date, `Month` runs from the first of the current UTC month.
- Every gated call currently reads the ledger (once per configured window). Caching gate reads for a short interval is noted as future work should ledger reads ever become a bottleneck.

Usage is also observable via the `ragnet.llm.tokens` counter (tagged `direction=in|out`, `surface=chat|embedding`) and the `ragnet.llm.cost` counter (tagged `surface`; unit nominally "usd" — actually the options' currency). The `surface` tag name is shared with the rate-limit histogram.

## Composing the resilience features

When you use more than one feature, register them in this order — each `Use*` wraps whatever is registered at that point, so registration order *is* nesting order:

```csharp
services.AddRagNet(rag =>
{
    // 1. Innermost: the provider clients, via the fallback chain.
    rag.UseFallbackChain(o =>
    {
        o.AddClient(sp => new OpenAIChatClient(sp.GetRequiredService<OpenAIClient>(), "gpt-4o"));
        o.AddClient(sp => new AnthropicChatClient(sp.GetRequiredService<AnthropicClient>(), "claude-sonnet-4-5"));
        o.PerClientTimeout = TimeSpan.FromSeconds(30);
    });

    // 2. Throttle outside the chain: one permit covers a whole fallback sequence.
    rag.UseRateLimiting(o => o.ChatRequestsPerMinute = 300);

    // 3. Outermost: the budget gate — the cheapest check runs first.
    rag.UseCostBudgeting(o =>
    {
        o.InputPricePerMTokens = 3m;
        o.OutputPricePerMTokens = 15m;
        o.DailyLimit = 25m;
    });
});
```

The resolved `IChatClient` is `CostTracking(RateLimited(Fallback(providers)))`. **Why this order:** cheapest gate first — a blown budget throws before consuming a rate permit, and a throttled call waits before starting a fallback sequence (so retries against secondary providers don't multiply your request rate). A `BudgetExceededException` is pinned as **non-transient** by type, so even a fallback chain nested *outside* the budget gate would never treat a blown budget as a provider failure to retry. Each decorator answers `GetService` for its own type, so the stack is probeable layer by layer.

One estimation caveat specific to this example: the chain mixes OpenAI and Anthropic providers, but estimation (used whenever a provider omits usage counts) is always `cl100k_base` — an OpenAI tokenizer — so estimated entries for the Anthropic fallback leg can deviate systematically from Anthropic's own token accounting.

## Retrying embedding and vector-store calls: `ConfigureResilience`

The three decorators above cover the chat surface and the spend/rate gates. Retry lives in a fourth: `RagBuilder.ConfigureResilience` registers a Polly resilience pipeline named `"rag-net"` and wraps the registered `IEmbeddingGenerator<string, Embedding<float>>` and `IVectorStore` with decorators that execute every call through it. Both surfaces genuinely lack retry otherwise — embedding providers have none, and Qdrant (gRPC), Pinecone (SDK) and PgVector (Npgsql) are not HTTP-typed clients.

```csharp
services.AddRagNet(rag => rag
    .UseQdrant("http://localhost:6334")
    .ConfigureResilience());   // 3 attempts, 1 s base delay, exponential, jitter
```

It follows the same ordering rule as the decorators above — it wraps whatever is registered at that point, so register the store and embedding generator first. Calling it with neither registered throws with an actionable message instead of silently doing nothing. Repeated calls re-configure the pipeline (last wins) but never stack a second decorator layer.

Cancellation is never retried: the caller's token flows into every attempt and the default retry predicate excludes `OperationCanceledException`, so a cancelled call fails on its first attempt. `BudgetExceededException` is excluded for the same reason — it is a deliberate kill switch, not a provider blip. Supply a `configure` delegate to replace the default policy; a custom pipeline owns its own predicates and should exclude both too.

**Where it sits in the stack.** `UseRateLimiting` and `UseCostBudgeting` decorate the same embedding surface, so call `ConfigureResilience` *after* them to make retry the outermost layer:

```csharp
services.AddRagNet(rag =>
{
    rag.UseQdrant("http://localhost:6334");
    rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(/* your provider */);
    rag.UseRateLimiting(o => o.EmbeddingRequestsPerMinute = 300);
    rag.UseCostBudgeting(o => { o.EmbeddingPricePerMTokens = 0.13m; o.DailyLimit = 25m; });
    rag.ConfigureResilience();   // outermost: a retried attempt re-acquires a permit and is billed
});
```

That ordering is deliberate — a retry is a real second API call, so it should consume a rate permit and land in the ledger. Calling `ConfigureResilience` first would nest retry *inside* the gates, letting retries bypass both.

> **Warning — double retry with Weaviate and Chroma:** those two stores hand-build a **retry-only** `ResilienceHandler` on their own `HttpClient` — a bare `AddRetry(new HttpRetryStrategyOptions())` pipeline, *not* `AddStandardResilienceHandler`, so there is no transport-level timeout, circuit breaker or concurrency limiter. `ConfigureResilience` therefore stacks **on top of** transport-level retries and the attempt counts multiply. Both layers default to `MaxRetryAttempts = 3`, and Polly counts *retries*, not attempts: 1 initial call + 3 retries = 4 attempts per layer, so the worst case is 4 × 4 = **up to 16 requests**, each with its own back-off. Configure one layer or the other: either skip `ConfigureResilience` for those stores and tune the HTTP handler, or keep it and accept the multiplication knowingly. Qdrant, Pinecone and PgVector have no transport-level retry, so for them the decorator is the only layer.

Full details — coverage, capability probes, custom policies — are in the [observability guide](observability.md#polly-resilience-pipeline).
