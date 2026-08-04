# Rag.NET.Parsers.Html

HTML parser for the Rag.NET ingestion pipeline: AngleSharp-based extraction that strips
scripts, styles and navigation chrome and keeps the readable text — the parser behind
web-page and Confluence/Jira-style HTML ingestion.

## Install

```bash
dotnet add package Rag.NET.Parsers.Html
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Html;

rag.AddHtmlParser();
```

## Example

Registered, the parser claims `text/html` content:

```csharp
using var stream = File.OpenRead("release-notes.html");
var result = await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId  = new DocumentId("release-notes-2026-07"),
    FileName    = "release-notes.html",
    ContentType = "text/html",
});
```

The parser is also consumed by `Rag.NET.Parsers.Epub` and `Rag.NET.Parsers.Email`, which
delegate their embedded HTML bodies to it.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
