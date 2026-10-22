# Rag.NET.Parsers.AzureDocumentIntelligence

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.AzureDocumentIntelligence.svg?logo=nuget&label=Rag.NET.Parsers.AzureDocumentIntelligence)](https://www.nuget.org/packages/Rag.NET.Parsers.AzureDocumentIntelligence)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.AzureDocumentIntelligence.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.AzureDocumentIntelligence)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **74 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Azure AI Document Intelligence as a Rag.NET document parser: TIFF and HEIF scans, which no
other Rag.NET parser reads, are sent to a Document Intelligence model as whole documents, and
the result comes back as one section per page. PDF, image, Office and HTML formats are opt-in.

## Install

```bash
dotnet add package Rag.NET.Parsers.AzureDocumentIntelligence
```

This package registers into the `AddRagNet(...)` builder from the core `Rag.NET` package. It
does not depend on `Rag.NET.Parsers.Pdf`, so it brings in no PDF library.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Azure;
using Rag.NET.Parsers.AzureDocumentIntelligence;

Uri endpoint = new("https://example.cognitiveservices.azure.com/");
var credential = new AzureKeyCredential("<key>");

// Claims image/tiff, image/heif and image/heic.
rag.AddAzureDocumentIntelligenceParser(endpoint, credential);
```

The credential is an `AzureKeyCredential` or a `TokenCredential` (managed identity / OAuth);
there is an overload for each.

## Example

Formats, the size cap and the analyzer are configured in one callback:

```csharp
using Azure;
using Rag.NET.Parsers.AzureDocumentIntelligence;

Uri endpoint = new("https://example.cognitiveservices.azure.com/");
var credential = new AzureKeyCredential("<key>");

rag.AddAzureDocumentIntelligenceParser(endpoint, credential, options =>
{
    options.Formats |= AzureDocumentIntelligenceFormats.Pdf;  // opt in: also send PDFs
    options.MaxBytes = 500_000_000;                           // default: Azure's S0 limit
    options.Analyzer.ModelId = "prebuilt-layout";             // default is prebuilt-read
    options.Analyzer.PricePerPage = 0.0015m;                  // default; indicative only
    options.Analyzer.PollingInterval = TimeSpan.FromSeconds(1);
});
```

## Replacing the default formats

`Formats` starts as TIFF and HEIF. `|=` adds to that; `=` replaces it, so TIFF and HEIF are
dropped:

```csharp
using Azure;
using Rag.NET.Parsers.AzureDocumentIntelligence;

Uri endpoint = new("https://example.cognitiveservices.azure.com/");
var credential = new AzureKeyCredential("<key>");

rag.AddAzureDocumentIntelligenceParser(endpoint, credential, o =>
{
    o.Formats = AzureDocumentIntelligenceFormats.Word;  // Word only
});
```

## Taking a format over from another parser

Either remove the other `Add...Parser()` call, or keep it and set `ReplaceExistingParsers`:

```csharp
using Azure;
using Rag.NET.Parsers.AzureDocumentIntelligence;

Uri endpoint = new("https://example.cognitiveservices.azure.com/");
var credential = new AzureKeyCredential("<key>");

// A local .docx parser, such as AddWordParser() from Rag.NET.Parsers.Office, registered first.
rag.AddAzureDocumentIntelligenceParser(endpoint, credential, o =>
{
    o.Formats = AzureDocumentIntelligenceFormats.Word;
    o.ReplaceExistingParsers = true;                          // Azure now reads .docx
});
```

- **Register Azure last.** Only parsers registered before the Azure call are replaced. One
  registered after it still conflicts at `AddRagNet`.
- **Only declared claims are found.** The parsers to replace come from the `ParserClaim`s already
  declared for the enabled content types, compared case-insensitively. A parser registered without
  claims is invisible to the option.
- **Partial overlap throws.** If a parser Azure would replace also claims types `Formats` does not
  cover, registration throws `InvalidOperationException`, naming every such parser and its sorted
  leftover types, and adds nothing.
- **Images with `UseImageDescription()` always throws.** The vision parser also claims
  `image/gif` and `image/webp`, which Azure never emits, so PNG, JPEG and BMP cannot be taken over
  from it. Keep the vision parser for images, or drop `UseImageDescription()`.
- **Bundled services stay registered.** Only the parser is removed; the chunking strategy that
  `UseImageDescription()` registers, for example, remains.
- **The override is recorded.** With exactly one parser replaced, the claims Azure declares carry
  its type name in `ReplacesParserTypeName`.

## What it claims, and what Azure charges

By default the parser claims **TIFF and HEIF only**: `image/tiff`, `image/heif` and
`image/heic`. Every other format is an opt-in, because a local Rag.NET parser already reads it
for free. Per Microsoft's Read model page for API version `2024-11-30`:

| Format | Flag | Azure returns | Billed per | Rag.NET without Azure |
|---|---|---|---|---|
| TIFF | `Tiff` (default) | OCR text, one page per frame | frame | nothing |
| HEIF / HEIC | `Heif` (default) | OCR text | image | nothing |
| PDF | `Pdf` | OCR text, lines, paragraphs | page | the PDF parser, plus the PDF OCR engine for scanned pages |
| PNG, JPEG, BMP | `Images` | OCR text | image | the vision parser: an LLM description, optionally Tesseract first |
| DOCX | `Word` | the embedded text as is; images ignored | 3,000 characters | the Office parser, locally, free |
| XLSX | `Excel` | the same | worksheet | the Office parser, locally, free |
| PPTX | `PowerPoint` | the same | slide | the Office parser, locally, free |
| HTML | `Html` | the same | 3,000 characters | the HTML parser, locally, free |

For Office and HTML, Azure adds cost, not text. **Word and HTML are billed per 3,000
characters**, so their cost cannot be capped by a page count before the call.

**Conflicts fail at startup.** Each claimed content type is declared to `AddRagNet`'s claim
check, so opting in to a format another registered parser claims, such as `Word` alongside
`AddWordParser()`, throws `InvalidOperationException` naming both parsers. Choose one parser per
format. An empty `Formats` is a registration error.

## Limits, spend and failures

- **`MaxBytes` defaults to 500,000,000 bytes**, Azure's documented 500 MB S0 limit. It is checked
  before anything is sent, and an oversized or empty document throws
  `AzureDocumentIntelligenceException` without a request being made. The document is buffered in
  one array, so values above `Array.MaxLength` are rejected.
- **Budget roughly 2.5 to 3.5 times the document in memory per concurrent parse.** The SDK takes
  the document as one buffer, a stream without a length is buffered in a doubling array that can
  reach twice the document, and the request carries it base64-encoded in JSON, about 1.33 times
  the document again.
- **Spend is recorded, not capped.** With an `ICostLedger` registered, each call is recorded as a
  `CostKind.Ocr` entry with the analyzed page count and that count × `PricePerPage`. It counts
  toward the `UseCostBudgeting` window, but the parser checks no budget before submitting.
- **Failures throw** `AzureDocumentIntelligenceException`, naming the file; there is no local text
  to fall back to. Cancellation propagates unwrapped.
- **Headings need a layout model.** `prebuilt-read` assigns no paragraph roles; with
  `prebuilt-layout`, titles and section headings set the section heading. A heading partway
  down a page applies from the next section, the next page, onwards.
- **Office and HTML sections follow Azure's page units:** about 3,000 characters for Word and
  HTML, a worksheet for Excel, a slide for PowerPoint. Each paragraph goes to the page whose
  content range holds its start. Only a paragraph no page holds becomes its own section without
  a page number, after the paged ones.

## Not the PDF OCR engine

`Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` is unchanged and does something different: it
is an OCR engine for the PDF parser, called only for PDFs with pages below `OcrMinCharacters`.
It now uses this package's analyzer for the service call, but it does not register this parser.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md#azure-document-intelligence-as-a-parser)
