# Rag.NET.Parsers.Epub

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Epub.svg?logo=nuget&label=Rag.NET.Parsers.Epub)](https://www.nuget.org/packages/Rag.NET.Parsers.Epub)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Epub.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Epub)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

EPUB parser for the Rag.NET ingestion pipeline: reads e-books with VersOne.Epub and
extracts each chapter's readable text (via the HTML parser) in spine order.

## Install

```bash
dotnet add package Rag.NET.Parsers.Epub
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Epub;

rag.AddEpubParser();
```

`AddEpubParser()` also registers the underlying `HtmlDocumentParser` it delegates chapter
bodies to.

## Example

```csharp
using var stream = File.OpenRead("clean-architecture.epub");
var result = await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId  = new DocumentId("clean-architecture"),
    FileName    = "clean-architecture.epub",
    ContentType = "application/epub+zip",
});
```

Pair it with `UseBookChunking()` from `Rag.NET.Chunking.Templates` to chunk along chapter
and section boundaries instead of fixed sizes.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
