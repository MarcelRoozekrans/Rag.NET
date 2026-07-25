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
- For streaming responses the timeout spans the **whole per-client attempt** (first token through stream completion), not just time-to-first-token. If it fires mid-stream, the chain restarts the request against the next client (see logging below).

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

*Planned — lands with the rate-limiting feature (Phase 1.4, Part B): request-per-minute throttling for chat and embedding calls via `UseRateLimiting`.*

## Cost budgeting

*Planned — lands with the cost-budgeting feature (Phase 1.4, Part C): persisted daily/monthly spend tracking with budget enforcement via `UseCostBudgeting`.*
