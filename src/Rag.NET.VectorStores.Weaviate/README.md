# Rag.NET.VectorStores.Weaviate

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
