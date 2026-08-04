# Rag.NET.VectorStores.PgVector

PostgreSQL + pgvector vector store for Rag.NET: dense cosine search over a `vector`
column, with optional learned sparse vectors (SPLADE-style) for hybrid retrieval — your
RAG index lives in the database you already run.

## Install

```bash
dotnet add package Rag.NET.VectorStores.PgVector
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.PgVector;

rag.UsePgVector(
    connectionString: "Host=localhost;Database=ragdb;Username=postgres;Password=secret",
    vectorDimensions: 1536);
```

## Example

Create the table and indexes once at startup, then ingest as usual:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.PgVector;

var store = provider.GetRequiredService<IVectorStore>() as PgVectorStore;
await store!.InitializeAsync();
```

Hybrid dense + learned-sparse search stores both vector kinds side by side:

```csharp
rag.UsePgVector(
    connectionString:     "Host=localhost;Database=ragdb;Username=postgres;Password=secret",
    vectorDimensions:     1536,
    enableSparseVectors:  true,
    sparseVocabularySize: PgVectorSparseVectorStore.DefaultSparseVocabularySize); // 30522
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
