# Rag.NET.Parsers.Epub

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
