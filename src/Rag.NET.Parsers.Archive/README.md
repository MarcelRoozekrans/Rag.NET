# Rag.NET.Parsers.Archive

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Archive.svg?logo=nuget&label=Rag.NET.Parsers.Archive)](https://www.nuget.org/packages/Rag.NET.Parsers.Archive)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Archive.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Archive)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

ZIP archive parser for the Rag.NET ingestion pipeline: an ingested archive is unpacked
in-memory and every entry is dispatched to the parser registered for its type, with
nesting-depth and entry-count limits guarding against zip bombs.

## Install

```bash
dotnet add package Rag.NET.Parsers.Archive
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Archive;

rag.AddArchiveParser();
```

## Example

The limits are configurable; exceeding them throws `ArchiveLimitExceededException`
instead of silently exhausting memory:

```csharp
using Rag.NET.Parsers.Archive;

rag.AddArchiveParser(options =>
{
    options.MaxNestingDepth     = 3;
    options.MaxNestedContainers = 10;
});
```

Entries only become chunks when a parser for their content type is registered — an
archive full of `.docx` files needs `Rag.NET.Parsers.Office` beside this package.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
