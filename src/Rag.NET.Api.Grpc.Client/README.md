# Rag.NET.Api.Grpc.Client

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Api.Grpc.Client.svg?logo=nuget&label=Rag.NET.Api.Grpc.Client)](https://www.nuget.org/packages/Rag.NET.Api.Grpc.Client)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Api.Grpc.Client.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Api.Grpc.Client)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

gRPC client for a remote `Rag.NET.Api.Grpc` host: registers an `IRagPipeline`
implementation that forwards every call over gRPC — the low-latency counterpart to the
REST-based `Rag.NET.Api.Client`.

## Install

```bash
dotnet add package Rag.NET.Api.Grpc.Client
```

## Setup

```csharp
using Rag.NET.Api.Grpc.Client.DependencyInjection;

services.AddRagNetGrpcClient(o =>
{
    o.BaseUrl = "https://rag-backend.internal:5001";
    o.ApiKey  = "your-api-key";
});
```

## Example

Application code resolves the standard `IRagPipeline`; streaming answers arrive over
gRPC server streaming:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

var pipeline = provider.GetRequiredService<IRagPipeline>();

await foreach (var update in pipeline.AskStreamingAsync("Summarise the incident report"))
{
    if (update.TextDelta is not null)
        Console.Write(update.TextDelta);
}
```

## Full guide

- [Hosting patterns (gRPC proxy)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
