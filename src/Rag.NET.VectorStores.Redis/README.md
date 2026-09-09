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

## Metadata filtering needs keys declared up front

Every other vector store in this library accepts a `MetadataFilter` and just filters on
whatever keys are in it. Redis cannot: RediSearch only filters on attributes its schema
declares, and a hash field is not one unless the index was told about it at creation time.
So `UseRedis` takes an extra, optional argument naming the metadata keys you intend to
filter on:

```csharp
rag.UseRedis(
    configuration:          "localhost:6379",
    indexName:              "ragnet-idx",
    vectorDimensions:       1536,
    filterableMetadataKeys: ["tenant"]);
```

Each declared key becomes a **case-sensitive** `md_<key>` TAG attribute on the index (e.g.
`tenant` → `md_tenant`). Case-sensitive because metadata values are Base64Url-encoded before
they are stored as tags, and Base64Url's alphabet uses both letter cases — a case-folding TAG
field would match two genuinely different values whose tokens happen to be case-variants of
one another.

Two consequences follow directly from keys being fixed at creation time:

- **Filtering on an undeclared key throws** `InvalidOperationException` naming the key,
  rather than silently returning an unfiltered page. `MetadataFilter = { ["tenant"] = "acme" }`
  against a store constructed without `"tenant"` in `filterableMetadataKeys` fails loudly, at
  query time.
- **An index built before a key was declared fails `InitializeAsync`.** Adding a key to
  `filterableMetadataKeys` does not retroactively add the TAG attribute to an existing index —
  `InitializeAsync` checks every declared key against the live schema and throws
  `InvalidOperationException` if one is missing. The index must be dropped and recreated (and
  its documents re-ingested) before that key can be filtered on.

This is Redis-only. Every other backend in this library either has a native document/map
type it can filter against directly (PgVector, Weaviate, Chroma) or applies the filter as a
metadata predicate outside a fixed schema (Qdrant, Pinecone, Azure AI Search); RediSearch is
the one query engine here that refuses to match against an attribute its schema never named.

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
