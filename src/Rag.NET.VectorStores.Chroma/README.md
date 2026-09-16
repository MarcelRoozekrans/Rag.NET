# Rag.NET.VectorStores.Chroma

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.VectorStores.Chroma.svg?logo=nuget&label=Rag.NET.VectorStores.Chroma)](https://www.nuget.org/packages/Rag.NET.VectorStores.Chroma)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.VectorStores.Chroma.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.VectorStores.Chroma)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Chroma vector store for Rag.NET over the REST API — a low-friction store for local
development and small deployments: run `chroma run` in a container, point the pipeline at
it, done.

## Install

```bash
dotnet add package Rag.NET.VectorStores.Chroma
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Chroma;

rag.UseChroma(
    endpoint:       new Uri("http://localhost:8000"),
    collectionName: "rag-chunks");
```

## Example

The store implements collection management, so startup code can create the collection on
first run:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

var manageable = provider.GetRequiredService<ICollectionManageable>();
if (!await manageable.CollectionExistsAsync("rag-chunks"))
    await manageable.CreateCollectionAsync("rag-chunks", vectorDimensions: 1536);
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
