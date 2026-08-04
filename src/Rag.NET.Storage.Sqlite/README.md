# Rag.NET.Storage.Sqlite

SQLite-backed persistence for Rag.NET's auxiliary stores: the BM25 index and parent-chunk
store, the document sidecar, the content-hash record manager that powers incremental
re-ingestion, the embedding-version store, and the persistent cost ledger.

## Install

```bash
dotnet add package Rag.NET.Storage.Sqlite
```

## Setup

```csharp
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag
    .UseSqlitePersistence("rag.db"));
```

`UseSqlitePersistence` moves the stores that are otherwise in-memory (BM25 postings,
parent chunks, document records) into one SQLite file, so hybrid search and
parent-document retrieval survive a restart.

## Example

Incremental re-ingestion: the content-hash record manager skips unchanged files, and
embedding versioning tracks which chunks were embedded with which model so stale ones can
be re-embedded after a model switch:

```csharp
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag
    .UseSqlitePersistence("rag.db")
    .UseContentHashRecordManager("rag.db")
    .UseEmbeddingVersioning());
```

The persistent cost ledger backs `UseCostBudgeting` from the core package:

```csharp
rag.UseSqliteCostLedger("rag-cost-ledger.db");
```

## Full guide

- [Ingestion](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
