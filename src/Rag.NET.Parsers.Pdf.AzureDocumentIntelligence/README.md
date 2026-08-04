# Rag.NET.Parsers.Pdf.AzureDocumentIntelligence

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
