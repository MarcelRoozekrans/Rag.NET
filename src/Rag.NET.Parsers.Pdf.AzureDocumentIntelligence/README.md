# Rag.NET.Parsers.Pdf.AzureDocumentIntelligence

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.svg?logo=nuget&label=Rag.NET.Parsers.Pdf.AzureDocumentIntelligence)](https://www.nuget.org/packages/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Azure Document Intelligence OCR engine for Rag.NET's PDF parser: scanned or image-only
PDFs are sent to the `prebuilt-read` model as whole documents instead of being OCR'd
page-by-page locally.

## Install

```bash
dotnet add package Rag.NET.Parsers.Pdf.AzureDocumentIntelligence
```

This package extends `Rag.NET.Parsers.Pdf` (installed automatically) and registers into
the `AddRagNet(...)` builder from the core `Rag.NET` package.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Pdf;
using Rag.NET.Parsers.Pdf.AzureDocumentIntelligence;

// credential: an AzureKeyCredential or TokenCredential (Azure.Core)
rag.AddPdfParser(options => options.UseOcrFallback = true)
   .UseAzureDocumentIntelligenceOcr(
       new Uri("https://my-resource.cognitiveservices.azure.com/"),
       credential);
```

## Example

The options control model, cost guardrails and polling:

```csharp
rag.UseAzureDocumentIntelligenceOcr(
    new Uri("https://my-resource.cognitiveservices.azure.com/"),
    credential,
    configure: options =>
    {
        options.ModelId      = "prebuilt-read"; // default
        options.PricePerPage = 0.0015m;         // feeds the cost ledger when enabled
        options.Locale       = "en";
    });
```

The engine only runs for documents the PDF parser flags as needing OCR
(`UseOcrFallback = true` and fewer than `OcrMinCharacters` extractable characters).

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
