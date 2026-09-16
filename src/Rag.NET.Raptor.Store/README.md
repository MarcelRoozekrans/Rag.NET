# Rag.NET.Raptor.Store

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Raptor.Store.svg?logo=nuget&label=Rag.NET.Raptor.Store)](https://www.nuget.org/packages/Rag.NET.Raptor.Store)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Raptor.Store.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Raptor.Store)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Persistent leaf-chunk storage for corpus-level RAPTOR clustering.

RAPTOR normally clusters one document at a time, using whatever chunks the ingestion
context already holds. Clustering across the whole corpus needs somewhere to keep every
leaf chunk *with its embedding vector* between ingests — `IVectorStore` has no way to
enumerate what it holds, and `IChunkLookup` returns chunks without the embeddings that
clustering runs on. `IRaptorLeafStore` and its SQLite implementation,
`SqliteRaptorLeafStore`, fill that gap.

This package is only *used* when `RaptorOptions.TreeScope` is `Corpus` — the default as of
v1.0. Under `PerDocument`, nothing is written here and nothing is paid for at runtime, but
the assembly still arrives: `Rag.NET.Raptor` references it unconditionally (`IRaptorLeafStore`
appears in `RaptorIngestionBehavior`'s public constructor), so installing `Rag.NET.Raptor`
alone already brings this package and its `Microsoft.Data.Sqlite` dependency in transitively.

## Install

Installing `Rag.NET.Raptor` is enough — this package arrives as its transitive dependency. A
direct `dotnet add package Rag.NET.Raptor.Store` reference is only needed if you implement
`IRaptorLeafStore` yourself or want the dependency pinned explicitly:

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
