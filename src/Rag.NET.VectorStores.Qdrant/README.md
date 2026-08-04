# Rag.NET.VectorStores.Qdrant

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
using Rag.NET.Qdrant;

var store = provider.GetRequiredService<IVectorStore>() as QdrantVectorStore;
await store!.InitializeAsync();

var results = await pipeline.RetrieveAsync("open incidents", new RetrievalOptions
{
    MetadataFilter = new Dictionary<string, string>
    {
        ["department"] = "finance",   // matches the meta_department payload field
    },
});
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
