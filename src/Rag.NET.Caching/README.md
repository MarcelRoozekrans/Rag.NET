# Rag.NET.Caching

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Caching.svg?logo=nuget&label=Rag.NET.Caching)](https://www.nuget.org/packages/Rag.NET.Caching)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Caching.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Caching)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Retrieval caching for Rag.NET on `HybridCache`: `UseCaching()` switches on the embedding
cache (identical queries stop paying for embedding calls) and the result cache (repeated
questions skip the vector store entirely).

## Install

```bash
dotnet add package Rag.NET.Caching
```

## Setup

```csharp
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag.UseCaching());
```

## Example

TTLs are the tuning surface — embeddings are stable (cache long), results go stale with
every ingest (cache short):

```csharp
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag.UseCaching(o =>
{
    o.EmbeddingTtl = TimeSpan.FromMinutes(30);  // default
    o.ResultTtl    = TimeSpan.FromMinutes(5);   // default
}));
```

Per request, `RetrievalOptions.UseCacheEmbedding` and `UseCacheResult` (both default
`true`) opt individual calls out.

## Full guide

- [Retrieval](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
