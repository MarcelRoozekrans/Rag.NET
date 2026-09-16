# Rag.NET.Storage.Sqlite

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Storage.Sqlite.svg?logo=nuget&label=Rag.NET.Storage.Sqlite)](https://www.nuget.org/packages/Rag.NET.Storage.Sqlite)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Storage.Sqlite.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Storage.Sqlite)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
