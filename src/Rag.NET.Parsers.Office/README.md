# Rag.NET.Parsers.Office

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Office.svg?logo=nuget&label=Rag.NET.Parsers.Office)](https://www.nuget.org/packages/Rag.NET.Parsers.Office)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Office.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Office)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Office document parsers for the Rag.NET ingestion pipeline: Word (`.docx`), Excel
(`.xlsx`) and PowerPoint (`.pptx`), read with DocumentFormat.OpenXml — no Office
installation required.

## Install

```bash
dotnet add package Rag.NET.Parsers.Office
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parsers register into.

## Setup

Each format has its own registration — add only what you ingest. Inside your
`AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Excel;
using Rag.NET.Parsers.PowerPoint;
using Rag.NET.Parsers.Word;

rag.AddWordParser()
   .AddExcelParser()
   .AddPowerPointParser();
```

## Example

With the parsers registered, Office files flow through the same ingest call as everything
else — the pipeline picks the parser by content type:

```csharp
using var stream = File.OpenRead("hr-policy.docx");
var result = await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId  = new DocumentId("policy-hr-001"),
    FileName    = "hr-policy.docx",
    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
});
```

Word paragraphs and headings become structured text, Excel sheets become row-wise text
per sheet, and PowerPoint slides are emitted slide-by-slide with their notes.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
