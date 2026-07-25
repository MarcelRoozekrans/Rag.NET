# Document Parsers — Design (Phase 1.5)

**Date:** 2026-07-25
**Milestone:** 1 — Feature Backlog, Phase 1.5
**Covers features.md rows:** EPUB Parser; Email File Parser (EML/MSG); PDF Table Extraction; OCR for Scanned PDFs

## Scope decisions (agreed)

1. **EPUB** is already fully implemented (`EpubDocumentParser`, matches spec) — deliverables are
   tests, fixtures, and features.md reconciliation (the table checkbox contradicts the detail
   section's "Done", same doc-drift class as prior phases).
2. **Email** is EML-complete; the remaining work is MSG support via MsgReader.
3. **OCR engine** is Tesseract behind the Vision package's `<EnableOcr>` compile-gate pattern,
   mirrored in `Rag.NET.Parsers.Pdf`. Azure Document Intelligence deferred (features.md offered
   either). OCR input = the page's embedded images extracted via PdfPig (`page.GetImages()`) —
   scanned pages are full-page images, so no PDF rasterizer dependency is needed.
4. **Table extraction defaults ON** (`ExtractTables = true`) — the feature exists because tables
   currently garble into prose; OCR defaults OFF (`UseOcrFallback = false`) — it needs the
   compile gate + tessdata.

## 1. EPUB (close-out)

**Package:** `Rag.NET.Parsers.Epub` (existing, complete)

- New `tests/Rag.NET.Parsers.Epub.Tests`: unit tests with an in-code-generated EPUB fixture
  (an EPUB is a zip: `mimetype` + `META-INF/container.xml` + minimal OPF + XHTML chapters via
  `System.IO.Compression`); chapter ordering, HTML delegation, SectionIndex re-stamping,
  CanParse matrix, non-EPUB stream error path.
- Embedded `sample.epub` in `tests/Rag.NET.Parsers.IntegrationTests/Resources/` + an
  integration test following the existing parser matrix.
- features.md: tick the row (detail Status already correct).

## 2. Email — MSG support

**Package:** `Rag.NET.Parsers.Email` (existing)

- Add `MsgReader` (`5.*` — verify current major) package reference.
- New `MsgDocumentParser : IDocumentParser` — `CanParse("application/vnd.ms-outlook")`;
  subject → `HeadingLevel = 1` section; body: text preferred, else HTML through the shared
  `HtmlDocumentParser`; attachments dispatched to registered parsers by content type.
- Extract the EML parser's attachment-dispatch loop (skip-self via `ReferenceEquals`) into a
  shared internal helper (`EmailAttachmentDispatcher`) used by both parsers — same semantics,
  one implementation.
- `AddEmailParser` registers both parsers (and the Html dependency, as today).
- New `tests/Rag.NET.Parsers.Email.Tests`: EML tests (none exist today — MimeKit can build
  fixtures in code) + MSG tests (MsgReader cannot write MSG; use a small embedded `sample.msg`
  fixture; unit-test the section-shaping logic against a seam if fixture-driven tests prove
  brittle — decided in planning).
- features.md: tick the row; Status updated (MSG delivered, no longer a follow-up).

## 3. PDF Table Extraction

**Package:** `Rag.NET.Parsers.Pdf` (existing, currently text-only)

- New `PdfParserOptions`: `ExtractTables = true`, `MinTableRows = 3`, `MinTableColumns = 2`,
  plus the OCR options from §4. `AddPdfParser(Action<PdfParserOptions>? configure)` overload
  beside the existing parameterless registration.
- New internal `PdfTableExtractor` — pure geometry heuristic over PdfPig word boxes:
  1. Cluster a page's words into rows by Y-coordinate bands (tolerance derived from median
     word height).
  2. Detect column boundaries from X-gaps that persist across >= `MinTableRows` vertically
     adjacent rows (>= `MinTableColumns` columns required).
  3. A maximal run of such rows = one table; emit as pipe-delimited Markdown
     (`| a | b |`, header separator after row 1) in its own `DocumentSection` with
     `Heading = "table"`, `PageNumber` set.
  4. Words consumed by a table are excluded from the page's prose section; prose and table
     sections keep document order via `SectionIndex`.
- The core clustering operates on a simple `(Text, X, Y, Width, Height)` word model —
  unit-testable with synthetic boxes, no PDF required.
- False-positive guards: min rows/columns; column-count consistency across the run
  (allow one ragged row for headers); bail out (prose as today) when heuristics disagree.
  Degraded-never-broken: extractor failure → log warning, page parses as prose exactly as today.

## 4. OCR for Scanned PDFs

**Package:** `Rag.NET.Parsers.Pdf`

- `PdfParserOptions` additions: `UseOcrFallback = false`, `OcrMinCharacters = 50` (per page),
  `TessDataPath = "./tessdata"`, `OcrLanguage = "eng"`.
- Per-page flow in `PdfDocumentParser`: when `UseOcrFallback` and
  `page.Text.Length < OcrMinCharacters`: extract the page's embedded images
  (`page.GetImages()`, largest-first), OCR each via the gated engine until text is produced;
  emit as the page's section with `Heading = "ocr"`, `PageNumber` set. No images or OCR
  failure → warning log + page skipped (degraded, never broken; matches the current
  empty-page behavior).
- **Compile gate:** mirror Vision's exact pattern in the Pdf csproj —
  `<EnableOcr>` property → `ENABLE_OCR` define + conditional `Tesseract 5.*` package ref;
  `#if ENABLE_OCR` around the engine; when `UseOcrFallback = true` without the gate compiled →
  instructive `InvalidOperationException` (Vision wording precedent).
- Duplication with Vision's embedded OCR is deliberate (no cross-parser-package coupling);
  a code note marks a future shared OCR package as the consolidation path.
- Azure Document Intelligence: documented as deferred (features.md Status).

## Error handling summary

House posture: parser failures inside table/OCR paths degrade to today's behavior (prose /
skipped page) with warnings; the only deliberate throw is OCR-requested-but-not-compiled
(misconfiguration should be loud). `NoParserFoundException` path unchanged.

## Testing

- EPUB: in-code zip fixtures (unit) + embedded sample.epub (integration).
- Email: MimeKit-built EML fixtures in code; embedded sample.msg for MSG; attachment-dispatch
  helper unit tests (fake parsers); DI test registers both parsers.
- PDF tables: pure-geometry unit tests on synthetic word boxes (clustering, column detection,
  ragged header tolerance, false-positive bails, Markdown rendering); embedded table-PDF
  fixture for end-to-end; existing prose behavior regression-pinned.
- OCR: gate-off actionable throw; option defaults; per-page threshold logic with a fake
  engine seam (`IPdfOcrEngine` internal — the Tesseract impl is the gated part, the seam is
  always compiled); embedded scanned-PDF fixture test env-gated like the Vision OCR precedent.
- features.md: all four rows ticked at the end.

## Out of scope

- Azure Document Intelligence OCR engine.
- PDF rasterization (vector-only scanned pages without embedded images are not OCR-able in
  this phase — documented limitation).
- Shared OCR package refactor (Vision + Pdf consolidation noted as future work).
- Table extraction for borderless/merged-cell edge cases beyond the heuristic guards.
