# Rag.NET.VectorStores.Pinecone

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.VectorStores.Pinecone.svg?logo=nuget&label=Rag.NET.VectorStores.Pinecone)](https://www.nuget.org/packages/Rag.NET.VectorStores.Pinecone)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.VectorStores.Pinecone.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.VectorStores.Pinecone)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Pinecone vector store for Rag.NET: serverless managed vector search over the official
Pinecone .NET client, keyed by API key and index name.

## Install

```bash
dotnet add package Rag.NET.VectorStores.Pinecone
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Pinecone;

rag.UsePinecone(
    apiKey:           Environment.GetEnvironmentVariable("PINECONE_API_KEY")!,
    indexName:        "rag-index",
    vectorDimensions: 1536);
```

## Example

With the store registered, retrieval carries metadata filters into Pinecone:

```csharp
using Rag.NET.Models;

var results = await pipeline.RetrieveAsync("renewal terms", new RetrievalOptions
{
    TopK = 8,
    MetadataFilter = new Dictionary<string, MetadataValue>
    {
        ["contract_type"] = "enterprise",
    },
});
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
