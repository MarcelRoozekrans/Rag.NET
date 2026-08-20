# Rag.NET.Raptor.Store

Persistent leaf-chunk storage for corpus-level RAPTOR clustering.

RAPTOR normally clusters one document at a time, using whatever chunks the ingestion
context already holds. Clustering across the whole corpus needs somewhere to keep every
leaf chunk *with its embedding vector* between ingests — `IVectorStore` has no way to
enumerate what it holds, and `IChunkLookup` returns chunks without the embeddings that
clustering runs on. `IRaptorLeafStore` and its SQLite implementation,
`SqliteRaptorLeafStore`, fill that gap.

This package is only needed when `RaptorOptions.TreeScope` is `Corpus`. Under
`PerDocument`, the default, nothing is written here and nothing is paid for.

## Install

```bash
dotnet add package Rag.NET.Raptor.Store
```

## Setup

```csharp
using Rag.NET.Raptor.Store;

await using var leafStore = new SqliteRaptorLeafStore("raptor-leaves.db");
await leafStore.InitializeAsync();
```

`SqliteRaptorLeafStore(path)` opens or creates the backing SQLite file at `path` (or
`:memory:` for a transient, in-process store). `InitializeAsync` creates the schema if it
does not already exist.

## Full guide

- [Ingestion](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
