# Rag.NET.Resilience

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Resilience.svg?logo=nuget&label=Rag.NET.Resilience)](https://www.nuget.org/packages/Rag.NET.Resilience)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Resilience.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Resilience)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
