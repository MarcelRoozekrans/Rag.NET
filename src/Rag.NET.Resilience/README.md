# Rag.NET.Resilience

Resilience decorators for Rag.NET's outbound calls: a Polly retry pipeline over embedding
generators and vector stores, token-bucket rate limiting for chat and embedding calls,
and a multi-provider chat fallback chain.

## Install

```bash
dotnet add package Rag.NET.Resilience
```

## Setup

```csharp
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag
    .ConfigureResilience());  // 3 attempts, 1 s base delay, exponential back-off, jitter
```

## Example

Layer the decorators from the inside out — fallback chain innermost, then throttling,
with the budget gate from the core package outermost:

```csharp
using Rag.NET.DependencyInjection;

services.AddRagNet(rag =>
{
    // 1. Innermost: try providers in order until one answers.
    rag.UseFallbackChain(o =>
    {
        o.AddClient(sp => primaryChatClient);
        o.AddClient(sp => secondaryChatClient);
        o.PerClientTimeout = TimeSpan.FromSeconds(30);
    });

    // 2. Throttle outside the chain: one permit covers a whole fallback sequence.
    rag.UseRateLimiting(o =>
    {
        o.ChatRequestsPerMinute      = 300;
        o.EmbeddingRequestsPerMinute = 1200;
    });
});
```

## Full guide

- [Resilience](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/resilience.md)
