# Rag.NET.VectorStores.Qdrant

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.VectorStores.Qdrant.svg?logo=nuget&label=Rag.NET.VectorStores.Qdrant)](https://www.nuget.org/packages/Rag.NET.VectorStores.Qdrant)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.VectorStores.Qdrant.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.VectorStores.Qdrant)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Qdrant vector store for Rag.NET over the gRPC client: dense cosine search, metadata
payload filtering, and optional named sparse vectors for hybrid retrieval.

## Install

```bash
dotnet add package Rag.NET.VectorStores.Qdrant
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback (port 6334 is Qdrant's gRPC port):

```csharp
using Rag.NET.Qdrant;

rag.UseQdrant(
    host:             "localhost",
    port:             6334,
    collectionName:   "my-collection",
    vectorDimensions: 1536);
```

## Example

Create the collection once at startup, then metadata filters map to Qdrant payload
fields:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Qdrant;

var store = provider.GetRequiredService<IVectorStore>() as QdrantVectorStore;
await store!.InitializeAsync();

var results = await pipeline.RetrieveAsync("open incidents", new RetrievalOptions
{
    MetadataFilter = new Dictionary<string, MetadataValue>
    {
        ["department"] = "finance",   // matches the meta_department payload field
    },
});
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
