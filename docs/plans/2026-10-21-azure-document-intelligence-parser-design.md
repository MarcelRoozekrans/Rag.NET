# Azure Document Intelligence as a parser — design for #824

**Date:** 2026-10-21
**Origin:** [#824](https://github.com/MarcelRoozekrans/Rag.NET/issues/824) (StefH): "`Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` can only handle PDF?"
**Builds on:** [`2026-07-27-azure-document-intelligence-design.md`](2026-07-27-azure-document-intelligence-design.md) (Phase 2.4)

## 0. What #824 actually is

The issue is filed as a bug. It is a feature request. `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` is not a parser: it is an `IDocumentOcrEngine`, and its only consumer is `PdfDocumentParser`. That parser calls it only for PDFs where some page falls below `OcrMinCharacters` (`IDocumentOcrEngine.cs:26-49`, `PdfDocumentParser.cs`). It is PDF-only by design, and the Phase 2.4 design says so.

What the reporter is asking for is a parser that sends any format Azure accepts to Azure.

## 1. What `prebuilt-read` actually returns, per format

This is taken from Microsoft's Read model page for v4.0, `2024-11-30` GA ([source](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/read?view=doc-intel-4.0.0), fetched 2026-10-21). It is not repeated from the issue.

| Format | Azure returns | Billing unit | Rag.NET today |
|---|---|---|---|
| PDF | OCR text, lines, paragraphs | each page | `PdfDocumentParser`, plus this OCR engine for scanned pages |
| PNG, JPEG, BMP | OCR text | each image | Vision parser: LLM description, optionally Tesseract first |
| **TIFF** | OCR text, one page per frame | each frame | **nothing** — see §2 |
| **HEIF** | OCR text | each image | **nothing** — see §2 |
| DOCX | embedded text "as is"; embedded images ignored; **no `lines`**, no page range | 3,000 characters | Office parser (`AddWordParser`), locally, free |
| XLSX | the same | each worksheet | Office parser (`AddExcelParser`) |
| PPTX | the same | each slide | Office parser (`AddPowerPointParser`) |
| HTML | the same | 3,000 characters | HTML parser |

Two consequences shape the design:

- **For Office and HTML, Azure adds cost, not text.** It extracts the same embedded text the local parsers already do, and it ignores images. The only reason to route Office files through it is a single pipeline, which is a legitimate choice but must be opt-in.
- **Office results have no `lines`.** The existing engine builds page text from `lines` and falls back to joining `words` with spaces (`AzureDocumentIntelligenceOcrEngine.AppendPageContent`). On Office input that would flatten every paragraph into one run of words. The parser must build text from `paragraphs`, which every format returns.

## 2. A pre-existing defect: TIFF and HEIF are ingested as text

`ContentTypeMap` maps `.tiff` to `image/tiff`, but it does not map `.tif`, `.heif` or `.heic`. `DocumentContentTypeResolver.ResolveContentType` turns any unmapped extension into `text/plain` — deliberately so for `.log` and `.cfg` files. A `.tif` scan from any data provider therefore reaches the text parser, and its bytes are ingested as text, today, without a warning.

This holds whether or not #824 ships. The fix is three map entries: `.tif` → `image/tiff`, and `.heif` / `.heic` → `image/heif` / `image/heic`. A test pins that a `.tif` file resolves to `image/tiff`, not `text/plain`. Once mapped, an unclaimed TIFF is warn-and-skipped like any other unclaimed binary type, which is the correct behaviour when no TIFF parser is registered.

## 3. The new package

**`Rag.NET.Parsers.AzureDocumentIntelligence`**, the name the issue proposes. It references `Rag.NET.Abstractions` and `Azure.AI.DocumentIntelligence` unconditionally, as Phase 2.4 §4 established for a managed REST client. It does **not** reference `Rag.NET.Parsers.Pdf`: a user who wants TIFF scans must not have to install PdfPig.

```csharp
rag.AddAzureDocumentIntelligenceParser(endpoint, credential);               // TIFF, HEIF
rag.AddAzureDocumentIntelligenceParser(endpoint, credential, o =>
{
    o.Formats |= AzureDocumentIntelligenceFormats.Office;                   // opt in
});
```

- **Both credential types**, `AzureKeyCredential` and `TokenCredential`, matching Phase 2.4 and the AzureBlob precedent.
- **Formats are an explicit flags enum**: `Tiff`, `Heif`, `Pdf`, `Images` (PNG/JPEG/BMP), `Word`, `Excel`, `PowerPoint`, `Html`, plus an `Office` convenience. Each maps to the content types it claims. The default is `Tiff | Heif`, claiming `image/tiff`, `image/heif` and `image/heic`.
- **Claims are declared**, so `ValidateParserClaims` sees them. The claims depend on options, so registration is by factory with claims declared explicitly, the way `AddPdfParser(configure)` and the Vision registrations do it. Enabling `Word` alongside `AddWordParser` is a startup `InvalidOperationException` naming both parsers. That is the guard working: one parser per format, chosen by the user, never a silent first-registered-wins.
- **An empty `Formats` is a registration error**, not a parser that claims nothing.

## 4. Output

- **One `DocumentSection` per analyzed page**, with `PageNumber` set. For Office input, a "page" is Azure's page unit (3,000 characters, a worksheet or a slide), and the docs say so.
- **Text is built from `paragraphs`**, grouped per page through their `boundingRegions` page numbers. Office and HTML paragraphs carry no bounding regions, so paragraphs without a page are emitted in document order as page-less sections. If `paragraphs` is absent, text falls back to `lines`, then to `words`.
- **Headings, only when a model provides them:** `prebuilt-read` returns paragraphs without a `Role`. `prebuilt-layout` fills `ParagraphRole.Title` and `ParagraphRole.SectionHeading`. When `ModelId` is a layout model, those set `Heading` / `HeadingLevel`, and `PageHeader`, `PageFooter` and `PageNumber` paragraphs are dropped so running headers do not repeat into every chunk. With the default `prebuilt-read`, no headings are inferred; the parser does not guess at structure from font size. Tables, key-value pairs and selection marks stay out of scope, as in Phase 2.4.

## 5. Cost and limits

- **Spend goes to `ICostLedger`** as `CostKind.Ocr` with `Pages = analyzed page count`, exactly like the engine. It shares the `UseCostBudgeting` window, as Phase 2.4 §5 decided.
- **`PricePerPage`** is configurable with the same indicative default and the same caveat.
- **`MaxPages`:** the analyzed page count is only known after the call, so it cannot bound spend in advance for every format. It is replaced by a **`MaxBytes` cap checked before submission**, defaulting to Azure's own 500 MB S0 limit, taken as 500,000,000 bytes because Microsoft documents it in decimal megabytes and a binary 500 MiB cap would let through files Azure then rejects, so an oversized file fails fast locally instead of being uploaded to fail remotely. The docs must state that Office spend is per 3,000 characters and cannot be capped by page count beforehand.

## 6. Failure posture

A parser cannot degrade the way the OCR engine does: the engine falls back to PdfPig's text, but a TIFF parser has no local text to fall back to. The precedent is the Vision parser, which throws a typed `VisionDescriptionException` rather than ingesting an empty document.

- **Service failure** (throttling, auth, unsupported content) throws `AzureDocumentIntelligenceException`, wrapping the `RequestFailedException`, and names the document. An empty section list is never returned for a failed call.
- **Cancellation propagates unwrapped.** The parser records spend to `ICostLedger` but does not enforce a budget, as with the Phase 2.4 engine.
- **Configuration errors are validated at registration**, as `AzureDocumentIntelligenceBuilderExtensions.BuildOptions` already does for the engine.

## 7. Shared code with the existing engine

The engine and the parser need the same client construction, polling loop, cost recording and options validation. There are three ways to share it:

| Option | Cost |
|---|---|
| **A. Duplicate** (~80 lines) | two copies of cost and polling logic, which will drift |
| **B. The OCR package references the new parser package** and calls a public `AzureDocumentIntelligenceAnalyzer` that both use | one implementation; a small public type; the dependency points from the PDF-specific package to the general one, which is the right direction |
| C. Merge the engine into the new package and deprecate the old package id | breaking for current users; retired ids remain real namespaces |

**Recommended: B.** The old package keeps its id, API and behaviour. The engine's existing tests must pass unchanged, which is the proof that nothing moved. `DependencyClosureTests` needs no change. It guards core's extracted clusters and the merged packages, not every package reference.

## 8. Testing

- **WireMock cassettes** for the long-running operation, as Phase 2.4 §7 established. At least one TIFF, one multi-frame TIFF (page numbering) and one DOCX, the DOCX proving that paragraphs, not lines, reach the output. Cassettes are recorded live once and committed, never edited to match the code.
- **Claim tests:** the default claims exactly `image/tiff`, `image/heif` and `image/heic`; enabling `Word` alongside `AddWordParser` throws at `AddRagNet`; an empty `Formats` throws.
- **Content-type map test** for `.tif`, `.heif` and `.heic` (§2).
- **Env-gated live test**, skipped without an endpoint and key, matching the engine's.
- **The engine's existing tests pass unchanged** under option B.

## 9. Repository bookkeeping

Each item is enforced by a test the agent should expect to fail first:

- `ExpectedPackageCount` 73 → 74, with a dated note (`ProducedPackageTests.cs`).
- The new assembly in `ParserClaimCoverageTests.s_parserAssemblies`, with `FewestPlausibleOptedInParsers` raised if the count moves.
- Its own `README.md` with the shared header, a unique `<Description>`, one `<VerifiedBy>recorded</VerifiedBy>`, the same as the existing engine, the solution entries, and central version pins.
- Docs: `docs/guide/ingestion.md` gains the parser and the per-format table from §1; `docs/reference/features.md` gets a row.

## Out of scope

- Tables, key-value pairs, selection marks and the richer `prebuilt-layout` model. `ModelId` is configurable, but nothing beyond paragraphs is mapped.
- Searchable-PDF output.
- Changing the Vision parser's image claims. Azure only takes PNG, JPEG and BMP when the user opts in to `Images` and does not register Vision.
