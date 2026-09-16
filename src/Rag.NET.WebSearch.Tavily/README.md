# Rag.NET.WebSearch.Tavily

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.WebSearch.Tavily.svg?logo=nuget&label=Rag.NET.WebSearch.Tavily)](https://www.nuget.org/packages/Rag.NET.WebSearch.Tavily)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.WebSearch.Tavily.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.WebSearch.Tavily)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Tavily web search for Rag.NET's corrective RAG (CRAG): when retrieval scores poorly
against your own corpus, the pipeline falls back to (or blends in) live web results
instead of answering from weak context.

## Install

```bash
dotnet add package Rag.NET.WebSearch.Tavily
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.WebSearch.Tavily;

services.AddTavilyWebSearch(
    apiKey: Environment.GetEnvironmentVariable("TAVILY_API_KEY")!);
```

## Example

With the search provider registered, CRAG is switched on per retrieval:

```csharp
using Rag.NET.Models;

var results = await pipeline.RetrieveAsync("latest stable Kubernetes release", new RetrievalOptions
{
    UseCrag            = true,
    CragScoreThreshold = 0.5f,                     // fall back below this score
    CragFallbackMode   = CragFallbackMode.Replace, // or Augment: blend web + corpus
});
```

Corpus answers stay authoritative when they score well; the web only fills the gaps.

## Full guide

- [Retrieval (corrective RAG)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
