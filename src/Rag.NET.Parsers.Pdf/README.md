# Rag.NET.Parsers.Pdf

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
