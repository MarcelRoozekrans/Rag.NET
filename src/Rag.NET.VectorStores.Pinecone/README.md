# Rag.NET.VectorStores.Pinecone

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
    MetadataFilter = new Dictionary<string, string>
    {
        ["contract_type"] = "enterprise",
    },
});
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
