# Rag.NET.VectorStores.Redis

Redis vector store for Rag.NET over RediSearch: dense cosine search on an HNSW index,
using the Redis you already run rather than a second datastore.

Requires the **RediSearch module** — Redis Stack, or Redis 8 and later, where it is
built in. Plain Redis answers `FT.CREATE` with an unknown-command error.

## Install

```bash
dotnet add package Rag.NET.VectorStores.Redis
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.VectorStores.Redis;

rag.UseRedis(
    configuration:    "localhost:6379",
    indexName:        "ragnet-idx",
    vectorDimensions: 1536);
```

If Redis is already in the application — the case this store exists for — hand it the
connection you have. The store does not dispose a multiplexer it did not create:

```csharp
var redis = ConnectionMultiplexer.Connect("localhost:6379");
rag.UseRedis(redis, "ragnet-idx", 1536);
```

## Example

Create the index once at startup. `InitializeAsync` is idempotent: an existing index is
left alone, because re-creating it would discard every stored vector.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.VectorStores.Redis;

var store = provider.GetRequiredService<IVectorStore>() as RedisVectorStore;
await store!.InitializeAsync();

var results = await pipeline.RetrieveAsync("open incidents", new RetrievalOptions
{
    TopK     = 5,
    MinScore = 0.6,
});
```

## Scores are similarities, not distances

RediSearch returns `vector_score` as a cosine **distance** in `[0, 2]` — 0 is identical,
larger is worse, the opposite direction from every score in this library. This store
converts it to `1 - distance` and reports ordinary cosine similarity, so `MinScore` means
here what it means everywhere else.

Hybrid search is deliberately **not** offered: RediSearch's text scoring is TF-IDF-shaped
rather than the BM25 the hybrid arm fuses, so the pipeline falls back to its own BM25 arm
instead of fusing a score the store cannot describe.

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md#redis-redisearch)
