---
id: choosing-packages
title: Choosing Packages
sidebar_position: 4
---

# Choosing Packages

Rag.NET ships as 69 packages, and a working pipeline needs two or three of them. This page
exists because the catalogue does not say which — the packages compose transitively, so most
of what a pipeline uses arrives on its own, and the only decisions you actually make are the
ones this page walks through.

The rule for all of it: **install the package whose builder method you call.** Everything
that package needs comes with it.

## The baseline is one package

```bash
dotnet add package Rag.NET
```

`Rag.NET` brings `Rag.NET.Abstractions` (the interfaces and model types) and
`Rag.NET.QueryTechniques` (HyDE, multi-query expansion, contextual compression)
transitively — never install either alongside it. `Rag.NET.Abstractions` is a direct install only when you are writing a
library against the interfaces — a custom `IDocumentParser` or `IVectorStore` in its own
package — and do not want the pipeline itself.

Out of the box, core parses text and Markdown, chunks with `RecursiveChunkingStrategy`, and
can run against an in-memory vector store. **The default chunker is in core** — you do not
install a chunking package to get chunking. `Rag.NET.Chunking` is for the *other*
strategies (token-aware, semantic, late, proposition, hierarchical-merge, code-aware);
install it when you call `UseTokenAwareChunking()`, `UseSemanticChunking()` or their
siblings, not before.

## Decision 1: a vector store

The in-memory store evaporates with the process, so a real pipeline picks exactly one store
package: `Rag.NET.VectorStores.PgVector`, `.Qdrant`, `.AzureAISearch`, `.Pinecone`,
`.Chroma` or `.Weaviate`. Each carries its own client library and registers with one
builder call (`UsePgVector(...)`, `UseQdrant(...)`, …). Nothing else changes with the
choice — every store implements the same `IVectorStore` from Abstractions.

## Decision 2: parsers for your formats

Text and Markdown are built in. Every other format is a parser package you add per format
you ingest: `Rag.NET.Parsers.Pdf`, `.Html`, `.Office` (Word, Excel and PowerPoint in one
package), `.Email`, `.Epub`, `.Archive` (ZIP), `.Audio`, `.Vision`. Parser packages sit on
`Rag.NET.Abstractions`, not on core, so each adds just its own format library (PdfPig,
AngleSharp, OpenXml, MimeKit, …) to your build.

## Decision 3 (optional): a data source connector

If you pull documents from a SaaS source instead of pushing streams yourself, add that
source's connector: `Rag.NET.DataProviders.Microsoft365` (SharePoint, OneDrive, Teams,
Exchange — one package), `.GitHub`, `.Slack`, `.Confluence`, `.Jira`, `.Notion`, `.Gmail`,
`.GoogleDrive`, `.Dropbox`, `.Box`, `.AzureBlob`, and so on. Every connector brings the
shared `Rag.NET.DataProviders` base — OAuth, polling, watermarks — transitively, and the
base brings the core pipeline with it, so a connector reference alone gives you everything
except your chosen store and parsers. (`Rag.NET.DataProviders.Web`, the crawler, is the one
exception: it sits directly on core with no OAuth base.)

## Opt-in features name their own package

Everything the pipeline does only when you switch it on lives in the package named for it,
and installing the package is how you get the builder method:

| You want | You call | You install |
|---|---|---|
| Chunks, hashes, parent chunks, BM25 surviving restarts | `UseSqlitePersistence()`, `UseContentHashRecordManager()`, `UseEmbeddingVersioning()` | `Rag.NET.Storage.Sqlite` |
| Spend limits that survive restarts | `UseSqliteCostLedger()` | `Rag.NET.Storage.Sqlite` |
| Retry/circuit-breaker, rate limiting, model fallback | `ConfigureResilience()`, `UseRateLimiting()`, `UseFallbackChain()` | `Rag.NET.Resilience` |
| Result and embedding caching | `UseCaching()` | `Rag.NET.Caching` |

`UseCostBudgeting()` itself stays in core with an in-memory ledger — its recorded spend
resets when the process restarts, and it logs a warning saying so. Add
`Rag.NET.Storage.Sqlite` and call `UseSqliteCostLedger()` before it for a ledger that
persists.

If you never call these methods, your build never downloads SQLite's native binaries,
Polly, or the HybridCache implementation.

## Worked example: SharePoint into Qdrant

Two genuine decisions — the source is SharePoint, the store is Qdrant — so two packages:

```bash
dotnet add package Rag.NET.DataProviders.Microsoft365
dotnet add package Rag.NET.VectorStores.Qdrant
```

`Rag.NET` itself, `Rag.NET.Abstractions`, `Rag.NET.QueryTechniques` and
`Rag.NET.DataProviders` all arrive transitively with the connector. (Referencing `Rag.NET`
explicitly as a third line is good practice, since your own code calls `AddRagNet()` — but
it is not a decision, and it changes nothing about what is downloaded.) No chunking
package: the default chunker is in core. No storage, resilience or caching package unless
you switch those features on.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.DataProviders.SharePoint;
using Rag.NET.Qdrant;

services.AddRagNet(rag => rag
    .UseQdrant("localhost", 6334, "sharepoint-docs", vectorDimensions: 1536));

services.AddSharePointDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET")!,
    siteId:       "contoso.sharepoint.com,site-guid,web-guid",
    driveId:      "drive-guid");
```

Before the package decomposition, answering "what do I install?" for this pipeline meant
reasoning about seven near-identical catalogue entries — `Rag.NET`, `Rag.NET.Abstractions`,
`Rag.NET.DataProviders`, the standalone SharePoint connector, the Qdrant store, and whether
chunking needed `Rag.NET.Chunking` or one of its two sibling packages — when only the
connector and the store were ever real choices. The transitive wiring was always there;
this page is where it gets said.

## What you never install directly

- `Rag.NET.Abstractions`, `Rag.NET.QueryTechniques` — arrive with core.
- `Rag.NET.DataProviders` — arrives with any connector.
- A chunking package for the default path — `RecursiveChunkingStrategy` is in core.

Every package's own README (on nuget.org and in `src/`) carries its install line, its
builder call and a working example, so once you know which packages are yours, each one
tells you the rest.

## Next steps

- [Getting started](../getting-started.md) — the end-to-end setup with the packages chosen
- [Vector stores](vector-stores.md) — choosing and configuring a store
- [Data providers](data-providers.md) — every connector's options
- [Chunking](chunking.md) — when the non-default strategies earn their package
