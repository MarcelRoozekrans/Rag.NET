# Rag.NET.VectorStores.Weaviate

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.VectorStores.Weaviate.svg?logo=nuget&label=Rag.NET.VectorStores.Weaviate)](https://www.nuget.org/packages/Rag.NET.VectorStores.Weaviate)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.VectorStores.Weaviate.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.VectorStores.Weaviate)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Weaviate vector store for Rag.NET over the REST API: chunks are stored as objects of a
configurable class, with API-key auth and multi-tenancy support for Weaviate Cloud.

## Install

```bash
dotnet add package Rag.NET.VectorStores.Weaviate
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Weaviate;

rag.UseWeaviate(
    endpoint:         new Uri("http://localhost:8080"),
    className:        "RagChunks",   // capital letter + letters/digits/underscores
    vectorDimensions: 1536);
```

## Example

Weaviate Cloud with an API key and a tenant:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Weaviate;

rag.UseWeaviate(new Uri("https://my-cluster.weaviate.cloud"), "RagChunks", 1536, options =>
{
    options.ApiKey = "wcs-api-key";   // sent as Authorization: Bearer
    options.Tenant = "customer_a";    // opt into multi-tenancy
});

// Once at startup: create the class schema if it does not exist.
var store = provider.GetRequiredService<IVectorStore>() as WeaviateVectorStore;
await store!.InitializeAsync();
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
