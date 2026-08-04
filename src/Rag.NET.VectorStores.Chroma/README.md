# Rag.NET.VectorStores.Chroma

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
