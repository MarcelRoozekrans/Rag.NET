# Azure Document Intelligence OCR — Design (Phase 2.4)

**Date:** 2026-07-27
**Milestone:** 2 — Deferred Items & Technical Debt, Phase 2.4
**Covers:** the "Azure Document Intelligence OCR" deferral from Phase 1.5

## The roadmap's framing is wrong, and that matters

`ROADMAP.md` describes this as a "second `IPdfOcrEngine` implementation alongside Tesseract".
That is not what fits.

`IPdfOcrEngine` (`src/Rag.NET.Parsers.Pdf/Ocr/IPdfOcrEngine.cs`) is `internal`, synchronous,
takes `byte[]` of **one embedded image**, returns `string?`, and has no `CancellationToken`.
Its own summary calls it a *"seam for the compile-gated OCR engine"* — it was designed around a
local native library.

Azure Document Intelligence is the inverse: it accepts the **whole PDF**, rasterizes
server-side, and returns every page in one response. Forcing it through the existing seam would
mean sync-over-async on a network call inside `ParseAsync`, cropped images instead of pages, and
per-image billing on a per-page-priced API.

More importantly, feeding Azure the document **dissolves three limitations** that
`docs/guide/ingestion.md` currently states as permanent:

| Limitation today | Under a document-level Azure path |
|---|---|
| Vector-only scanned pages can't be OCR-ed without a rasterizer | Azure rasterizes server-side — they work |
| CCITT G4 / JBIG2 scans don't decode via PdfPig/Leptonica | Azure accepts the PDF itself — they work |
| Tesseract isn't thread-safe, so OCR calls are serialized | Network client, no serialization needed |

## Scope decisions (agreed)

1. **Document-level seam**, not a second `IPdfOcrEngine`.
2. **Separate package, no compile gate.**
3. **Extend `ICostLedger`** so OCR spend is visible — not deferred.
4. **Both `AzureKeyCredential` and `TokenCredential`.**

---

## 1. The new seam

`IPdfOcrEngine` is untouched: still `internal`, still per-image, still Tesseract's.

Alongside it, a new **public** `IDocumentOcrEngine` in `Rag.NET.Parsers.Pdf` — public because
the Azure package is a separate assembly, and public API is a deliberate commitment rather than
an `InternalsVisibleTo` back door:

```csharp
ValueTask<DocumentOcrResult> RecognizeAsync(Stream pdf, CancellationToken cancellationToken);
```

`DocumentOcrResult` carries per-page text keyed by 1-based page number plus the page count
Azure billed for. Async and cancellable from the start — the mistake the original seam made is
not repeated.

The two seams never interact. `PdfDocumentParser` uses whichever is configured; configuring
both is a registration-time error rather than a silent precedence rule.

## 2. When it fires, and what it costs

PdfPig parses first, as today. If **any** page falls below `OcrMinCharacters`, the parser makes
**one** Azure call for the whole document and uses the returned text **only for the
sub-threshold pages**. PdfPig's extraction is exact where it works; discarding it would trade
accuracy for nothing.

**The cost consequence is unavoidable and must be documented, not buried.** Azure bills every
page of the submitted document, so a 500-page PDF containing one scanned page costs 500 pages of
OCR. Splitting out just the needed pages would require writing PDFs, which this repo has no
dependency for and which is not worth acquiring here.

Mitigation: a **hard `MaxOcrPages` cap**. A document above it skips OCR entirely with a logged
warning naming the page count and the cap — spending is bounded by configuration rather than by
whatever a user happens to ingest.

## 3. Stream handling

`ParseAsync` receives one `Stream`, which PdfPig already consumes. Azure needs to read it again.
Seekable streams are rewound. Non-seekable streams are buffered to memory, bounded by the same
`MaxOcrPages`-derived reasoning — and where buffering is refused, the existing
`RagError.NonSeekableStream` is the precedent for how this repo signals it.

## 4. Packaging

New package `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence`, referencing `Rag.NET.Parsers.Pdf`,
with an **unconditional** `Azure.AI.DocumentIntelligence` reference and **no compile gate**.

The `<EnableOcr>` gate exists for Tesseract's native binaries and out-of-band traineddata files
(`docs/plans/2026-04-03-vision-parser-design.md:129`). Neither applies to a managed REST client,
and every other cloud SDK in this repo — Azure AI Search, Azure Blob, Azure Identity, Cohere —
is an unconditional reference inside its own package. Reusing `EnableOcr` would be actively
wrong: it would force a user who wants *only* Azure to also pull Tesseract's native payload,
since the `Tesseract` reference sits under the same condition.

Registration mirrors the house idiom, with dual credentials following the AzureBlob precedent:

```
.AddPdfParser(...).UseAzureDocumentIntelligenceOcr(endpoint, AzureKeyCredential, configure?)
.AddPdfParser(...).UseAzureDocumentIntelligenceOcr(endpoint, TokenCredential,    configure?)
```

Managed identity matters for an Azure-native service; key-only would be a worse default than
AzureAISearch's, which predates that lesson.

A `DocumentIntelligenceClientOptions?` constructor parameter exists for transport injection and
is **not** exposed on the builder extension — precisely the shape `AzureAISearchVectorStore`
uses so its tests can drive a custom transport.

## 5. Cost

`ICostLedger` cannot represent an OCR call today: `CostKind` is `{ Chat, Embedding }` and
`CostEntry` requires `InputTokens`/`OutputTokens`, which are meaningless for a per-page API.

**Changes to `Rag.NET.Abstractions`:**
- `CostKind` gains an OCR member.
- `CostEntry`'s token counts stop being `required` (source-compatible; existing callers still
  compile) and a `Pages` field is added. An OCR entry then carries pages and zero tokens
  honestly, instead of fabricating a token count.

**The engine records, not the parser.** The Azure engine resolves `ICostLedger?` from DI and
records per call, so `PdfDocumentParser` stays unaware of billing entirely and no cost plumbing
threads through the parser's constructor.

**A behaviour change worth stating plainly:** OCR spend now counts toward the same budget window
`UseCostBudgeting` enforces for chat and embedding, so enabling OCR can cause *those* gates to
trip. That is correct — it is one budget — but it is a change users will notice.

**Deliberately out of scope:** active enforcement at the OCR call site (checking the budget and
refusing to spend). Recording gives visibility; blocking is a separate decision with its own
failure-mode questions, and `MaxOcrPages` already bounds the exposure.

## 6. Error handling

House parser posture is degraded-never-broken: an OCR failure logs a warning and falls back to
the plain-text path losslessly, exactly as the Tesseract path does today. Azure-specific
failures — throttling, auth, unsupported content — are warnings, not exceptions.

Configuration errors are the exception and stay loud: registering both engines, or an
unreachable endpoint at startup, fails fast.

Cancellation propagates. The long-running-operation polling honours the caller's token.

## 7. Testing

- **WireMock cassettes** are the only viable automated coverage. There is no Testcontainers
  module for Document Intelligence, and Microsoft's Cognitive Services container requires an
  approved access request, a live resource endpoint, and bills against it — unusable in CI.
  Azurite covers storage only.
- The cassettes must capture the **long-running-operation shape**: the 202, the
  `Operation-Location` header, and the poll responses. Retry and polling delays must be neutered
  through `DocumentIntelligenceClientOptions`, or the suite sits for tens of seconds.
- An **env-gated live test** mirroring the existing `RAGNET_TESSDATA` precedent
  (`Assert.SkipWhen` on a missing endpoint/key), so a real call is possible but never required.
- Parser-level tests use a fake `IDocumentOcrEngine` — the same seam-with-a-fake strategy that
  already makes the Tesseract fallback testable with the gate off.
- The `sample-scanned.pdf` fixture is currently excluded from the integration parser matrix
  because "OCR needs the EnableOcr compile gate". An ungated engine removes that excuse.

## 8. Documentation

- `docs/guide/ingestion.md` — the OCR limitations block becomes **engine-conditional**; three of
  its four bullets are Tesseract-specific and false for the Azure path. The `MaxOcrPages` cap and
  the whole-document billing model need stating explicitly.
- `docs/reference/features.md` — the OCR row and its Status paragraph both say "Azure Document
  Intelligence deferred".
- `PdfParserOptions.UseOcrFallback`'s XML doc hard-codes the `<EnableOcr>` requirement, which
  stops being true the moment a second engine exists. Same for `IPdfOcrEngine`'s summary, which
  calls itself the seam for *the* compile-gated engine.
- `ValidateOptions` throws on empty `TessDataPath`/`OcrLanguage` whenever `UseOcrFallback` is
  set — an Azure user would hit that spuriously. It becomes engine-conditional.

## Out of scope

- Azure's structured output beyond text: tables, key/value pairs, selection marks. The PDF
  parser already has its own table extractor, and merging two table sources is a separate
  question.
- Active budget enforcement at the OCR call site (§5).
- Rasterizing PDFs locally — Azure removes the need on its path; the Tesseract path keeps the
  limitation.
- Consolidating the Vision and Pdf OCR seams into a shared package. Noted as future work since
  Phase 1.5 and still not this phase.
