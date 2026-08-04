# Rag.NET.Caching

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
