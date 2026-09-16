# Rag.NET.Api.Client

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Api.Client.svg?logo=nuget&label=Rag.NET.Api.Client)](https://www.nuget.org/packages/Rag.NET.Api.Client)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Api.Client.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Api.Client)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Typed HTTP client for a remote `Rag.NET.Api` host: registers an `IRagPipeline`
implementation that forwards every call over HTTP, so application code cannot tell whether
the pipeline runs in-process or behind a REST API.

## Install

```bash
dotnet add package Rag.NET.Api.Client
```

## Setup

```csharp
using Rag.NET.Api.Client.DependencyInjection;

services.AddRagNetApiClient(o =>
{
    o.BaseUrl = "https://rag-backend.internal";
    o.ApiKey  = "your-api-key";
});
```

## Example

The registered pipeline is the same `IRagPipeline` abstraction the in-process pipeline
implements — swap the backend without touching call sites:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

var pipeline = provider.GetRequiredService<IRagPipeline>();

var response = await pipeline.AskAsync("What changed in the Q4 report?");
Console.WriteLine(response.Answer);
```

## Full guide

- [Hosting patterns (REST proxy)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
