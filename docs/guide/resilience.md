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
| `TaskCanceledException` or `TimeoutException` | Request timed out |
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

`UseCostBudgeting` wraps the registered `IChatClient` and/or `IEmbeddingGenerator<string, Embedding<float>>` with cost-tracking decorators backed by a SQLite ledger, giving you a persistent daily/monthly spend guardrail:

```csharp
services.AddRagNet(rag =>
{
    rag.Services.AddSingleton<IChatClient>(myProviderClient); // register the client FIRST
    rag.UseCostBudgeting(o =>
    {
        o.InputPricePerMTokens = 3m;        // your provider's price per 1M input tokens
        o.OutputPricePerMTokens = 15m;      // ... per 1M output tokens
        o.EmbeddingPricePerMTokens = 0.02m; // ... per 1M embedding input tokens
        o.DailyLimit = 25m;
        o.MonthlyLimit = 400m;              // at least one limit is required
        o.DatabasePath = "rag-cost-ledger.db";
    });
});
```

Before each call the decorator checks the recorded spend of the current UTC day and month against the configured limits and throws `BudgetExceededException` (carrying `Window`, `Limit`, and `Spend`) once a limit is reached. After each call it records token usage and cost to the ledger. For streaming calls the gate — like the rate limiter's permit — fires on **first enumeration**, not when `GetStreamingResponseAsync` returns. Every registered surface is decorated; at least one must be registered before the call. Repeat calls are idempotent (first configuration wins; decorators never stack).

Things to know:

- **Prices are user-supplied.** There is no built-in price table — provider prices churn too fast to ship. All monetary values (prices, limits, ledger totals, the `ragnet.llm.cost` counter) share whatever currency you quote the prices in.
- **Token counts are estimates unless the provider reports usage.** Chat calls use `ChatResponse.Usage` when the provider reports *both* input and output counts; otherwise both sides are estimated with the tiktoken `cl100k_base` tokenizer (over the request messages and response text). Embedding usage is always estimated (providers rarely report it). Treat the ledger as a close approximation, not an invoice.
- **Streaming records once, after the stream completes**, using the usage the provider emitted in the update stream (`UsageContent`) when present, else estimating from the accumulated text. A stream abandoned mid-way — cancelled or faulted — is deliberately **not recorded**: its true usage is unknown, and guessing would corrupt the ledger.
- **The gate is pre-call, so a budget can overshoot by one call.** A call admitted at spend 24.99 against a 25.00 limit still runs to completion and records its full cost; enforcement kicks in for the *next* call. Size limits with one call's worth of headroom in mind.
- **Ledger failures degrade, never break.** If the ledger cannot be read, the call proceeds ungated (with a warning); if it cannot be written, the call still succeeds (with a warning). Budget enforcement is best-effort under storage failure.
- **The ledger is replaceable.** `UseCostBudgeting` registers `SqliteCostLedger` with `TryAdd`, so an `ICostLedger` registered *before* the call — the shipped `InMemoryCostLedger`, or your own store — wins.
- Windows are UTC calendar windows: `Day` is the current UTC date, `Month` runs from the first of the current UTC month.

Usage is also observable via the `ragnet.llm.tokens` counter (tagged `direction=in|out`, `kind=chat|embedding`) and the `ragnet.llm.cost` counter (tagged `kind`; unit nominally "usd" — actually the options' currency).

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

The resolved `IChatClient` is `CostTracking(RateLimited(Fallback(providers)))`. **Why this order:** cheapest gate first — a blown budget throws before consuming a rate permit, and a throttled call waits before starting a fallback sequence (so retries against secondary providers don't multiply your request rate). Each decorator answers `GetService` for its own type, so the stack is probeable layer by layer.

## Known issue: `ConfigureResilience` registers a dangling pipeline

Pre-existing and not addressed by the features above: `RagBuilder.ConfigureResilience` registers a Polly resilience pipeline named `"rag-net"` that nothing in the library consumes. Calling it configures retry policy on a pipeline no code executes. The decorators on this page (`UseFallbackChain`, `UseRateLimiting`, `UseCostBudgeting`) are the supported resilience mechanisms; `ConfigureResilience` is tracked as a known issue.
