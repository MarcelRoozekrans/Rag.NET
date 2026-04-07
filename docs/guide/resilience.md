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

All other exceptions propagate immediately — the remaining clients are **not** tried.

### Registering in DI

`FallbackChatClient` requires no DI extension. Construct it directly in your service registration:

```csharp
services.AddSingleton<IChatClient>(sp => new FallbackChatClient(
[
    new OpenAIChatClient(sp.GetRequiredService<OpenAIClient>(), "gpt-4o"),
    new AnthropicChatClient(sp.GetRequiredService<AnthropicClient>(), "claude-3-5-sonnet-20241022"),
],
logger: sp.GetRequiredService<ILogger<FallbackChatClient>>()));
```

When the primary client (OpenAI) hits a rate limit, `FallbackChatClient` logs a warning and immediately retries the same request against the secondary (Anthropic), with no delay.

### Logging

One `LogWarning` is emitted per fallback attempt, including the client index and whether the failure occurred before or during streaming:

```
warn: Rag.NET.Resilience.FallbackChatClient
      Client 0 failed transiently; trying next client.
```

For streaming responses that fail mid-stream, the log includes how many tokens had been yielded before the failure:

```
warn: Rag.NET.Resilience.FallbackChatClient
      Streaming client 0 failed mid-stream after 12 token(s); restarting with next client.
```

### Out of scope

- Per-client timeout configuration
- Jitter or backoff between attempts (fallback is immediate)
- Retry limits within a single client (each client is tried once)
