# Rag.NET.Parsers.Pdf

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Pdf.svg?logo=nuget&label=Rag.NET.Parsers.Pdf)](https://www.nuget.org/packages/Rag.NET.Parsers.Pdf)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Pdf.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Pdf)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

PDF parser for the Rag.NET ingestion pipeline: PdfPig-based text extraction with table
detection, plus an opt-in OCR fallback for scanned pages.

## Install

```bash
dotnet add package Rag.NET.Parsers.Pdf
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Pdf;

rag.AddPdfParser();
```

## Example

Table extraction is on by default; OCR is opt-in because it needs an engine:

```csharp
using Rag.NET.Parsers.Pdf;

rag.AddPdfParser(options =>
{
    options.ExtractTables    = true;  // default: tables become Markdown in the chunk text
    options.MinTableRows     = 3;     // default
    options.UseOcrFallback   = true;  // pages under OcrMinCharacters go through OCR
    options.OcrMinCharacters = 50;    // default: the OCR trigger threshold
});
```

The built-in fallback uses Tesseract (compile-time opt-in). For managed document-level
OCR, chain `UseAzureDocumentIntelligenceOcr` from the
`Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` package instead — `AddPdfParser` dispatches
to whichever `IDocumentOcrEngine` is registered.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
