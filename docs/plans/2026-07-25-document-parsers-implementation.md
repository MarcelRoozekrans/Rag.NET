# Document Parsers Implementation Plan (Phase 1.5)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close out the four parser backlog rows: EPUB tests/reconciliation, MSG email support, PdfPig-geometry table extraction, and compile-gated Tesseract OCR fallback.

**Architecture:** Per `docs/plans/2026-07-25-document-parsers-design.md`. Part A (EPUB) and Part B (Email/MSG) are close-out/extension work on existing complete parsers; Parts C (tables) and D (OCR) rebuild `PdfDocumentParser` around a new `PdfParserOptions` with a pure-geometry table extractor and a seam-based OCR engine mirrored from the Vision package's `<EnableOcr>` pattern.

**Tech Stack:** .NET 10, xUnit v3 + NSubstitute, VersOne.Epub (existing), MimeKit (existing) + MsgReader (new), UglyToad.PdfPig (existing), Tesseract 5.* (conditional), System.IO.Compression (EPUB fixtures).

**Conventions:** as previous phases — options POCOs, LoggerMessage, OCE-first, MA0051/MA0015/ZA0601/ZA0501/EPS05/HLQ warnings-as-errors, commit trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`, filtered tests during work, one `dotnet build Rag.NET.slnx` per part, TDD throughout. Parser test conventions: per-parser test project mirroring `tests/Rag.NET.Parsers.Word.Tests` csproj shape; integration fixtures as embedded resources in `tests/Rag.NET.Parsers.IntegrationTests/Resources/`.

---

## Part A — EPUB close-out

### Task A1: test project + fixtures + reconciliation

**Files:**
- Create: `tests/Rag.NET.Parsers.Epub.Tests/Rag.NET.Parsers.Epub.Tests.csproj` (copy the Word.Tests csproj shape; project-ref Epub + Html parsers) + add to `Rag.NET.slnx`.
- Create: `tests/Rag.NET.Parsers.Epub.Tests/EpubDocumentParserTests.cs` — in-code EPUB builder helper (`CreateEpub(params (string title, string xhtml)[] chapters)`: `ZipArchive` with `mimetype` (stored, first entry, content `application/epub+zip`), `META-INF/container.xml`, `OEBPS/content.opf` (manifest + spine in order), chapter XHTML files). Read `src/Rag.NET.Parsers.Epub/EpubDocumentParser.cs` first — verify VersOne.Epub's RELAXED reader accepts the minimal fixture; iterate the fixture format until it parses (this is the risky bit — timebox and fall back to a small embedded .epub asset if VersOne rejects hand-built zips; note the choice).

```csharp
// 1. Parse_TwoChapters_EmitsSectionsInSpineOrder (chapter text present, order correct).
// 2. Parse_SectionIndexes_AreSequentialAcrossChapters.
// 3. CanParse_Matrix ("application/epub+zip" true; "application/pdf", "text/html" false).
// 4. Parse_NonEpubStream_Throws (garbage bytes → whatever VersOne throws — pin the type).
// 5. Parse_HtmlDelegation_StripsMarkup (chapter with <h1>/<p> → heading/paragraph sections via HtmlDocumentParser).
// 6. DI: AddEpubParser registers parser + Html dependency (in tests/Rag.NET.Tests/DependencyInjection if the sibling parser DI tests live there — check AddEmailParser/AddEpubParser test precedent; create following it).
```

- Integration: embedded `sample.epub` in `tests/Rag.NET.Parsers.IntegrationTests/Resources/` (generate one with the same in-code builder written to disk once, or a minimal public-domain sample; keep it < 50KB) + a matrix test in `DocumentParserTests.cs` following the existing pattern.
- `docs/reference/features.md`: tick the EPUB row (detail Status already says Done — verify wording, align).

**Commit** `test(parsers): EPUB parser test coverage + fixtures; tick feature`

---

## Part B — Email MSG support

### Task B1: attachment-dispatch helper extraction

**Files:**
- Create: `src/Rag.NET.Parsers.Email/EmailAttachmentDispatcher.cs` — internal static (or small internal class): given `IEnumerable<IDocumentParser> parsers`, `IDocumentParser self`, attachment `(string fileName, string mimeType, Stream content)`, `DocumentMetadata`, `ILogger?`, `ct` → finds the first non-self parser whose CanParse matches (the exact loop from `EmailDocumentParser.ParseAttachmentsAsync` lines 67-76 incl. the ReferenceEquals skip and no-parser warning), streams the parsed sections. Refactor `EmailDocumentParser` to use it (behavior identical — its tests come in B3 and must pass against both parsers).
- Test: covered via B3's parser tests (the helper is internal plumbing; direct tests optional if the parser tests pin both consumers).

**Commit** `refactor(parsers): extract shared email attachment dispatcher`

### Task B2: `MsgDocumentParser`

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/Rag.NET.Parsers.Email.csproj` — add `MsgReader` (check latest major on nuget.org via `dotnet package search MsgReader --exact-match`; pin floating per repo convention e.g. `8.*` — VERIFY, don't guess).
- Create: `src/Rag.NET.Parsers.Email/MsgDocumentParser.cs` — `CanParse("application/vnd.ms-outlook")`; MsgReader: `new MsgReader.Outlook.Storage.Message(stream)` (VERIFY the API surface against the installed package — inspect metadata; do not guess member names): subject → HeadingLevel-1 section; body: `BodyText` preferred, else `BodyHtml` through `HtmlDocumentParser`; attachments (`Message.Attachments` — files with `FileName`/`Data`) through `EmailAttachmentDispatcher` (content type inferred from the attachment's mime type property if MsgReader exposes one, else from the file extension via a small internal extension→MIME map covering the parser registry's known types; document the inference).
- Modify: `src/Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs` — `AddEmailParser` also registers `MsgDocumentParser`.

**Commit** `feat(parsers): MSG email parser via MsgReader`

### Task B3: email tests + fixtures + reconciliation

**Files:**
- Create: `tests/Rag.NET.Parsers.Email.Tests/` project (+ slnx):

```csharp
// EML (MimeKit builds fixtures in code — new MimeMessage + BodyBuilder, WriteToAsync to MemoryStream):
// 1. Parse_SubjectAndTextBody_Sections. 2. Parse_HtmlBody_DelegatesToHtml.
// 3. Parse_TextAttachment_DispatchedToTextParser (register a fake parser; assert its sections appear).
// 4. Parse_UnparseableAttachment_WarnsAndSkips. 5. CanParse matrix.
// MSG (embedded sample.msg — MsgReader cannot write; source a tiny fixture: build one via
// Outlook-free tooling if available, else craft the smallest valid CFB container — if neither
// is feasible within the timebox, unit-test MsgDocumentParser's section-shaping through an
// internal seam (IMsgMessage adapter interface wrapping MsgReader's Storage.Message) and
// leave the embedded-fixture test to integration with a documented TODO; NOTE the choice in
// your report):
// 6. Parse_Msg_SubjectBodyAttachments (fixture or seam). 7. CanParse matrix.
// DI: AddEmailParser registers BOTH parsers + Html.
```

- Integration: `sample.eml` embedded resource (in-code-generated once) + matrix entry; `sample.msg` if a fixture was obtained.
- features.md: tick the Email row; detail Status updated (MSG delivered).

**Commit** `test(parsers): email parser tests incl. MSG; tick feature`

---

## Part C — PDF table extraction

### Task C1: options + word model + clustering core

**Files:**
- Create: `src/Rag.NET.Parsers.Pdf/PdfParserOptions.cs` — `ExtractTables = true`, `MinTableRows = 3`, `MinTableColumns = 2`, `UseOcrFallback = false`, `OcrMinCharacters = 50`, `TessDataPath = "./tessdata"`, `OcrLanguage = "eng"` (all `get; set;`; validation in the Add extension: counts > 0, chars >= 0, paths/lang non-empty when OCR enabled).
- Create: `src/Rag.NET.Parsers.Pdf/TableExtraction/WordBox.cs` — `readonly record struct WordBox(string Text, double X, double Y, double Width, double Height)` (internal).
- Create: `src/Rag.NET.Parsers.Pdf/TableExtraction/PdfTableExtractor.cs` — internal static, PURE (no PdfPig types): `Extract(IReadOnlyList<WordBox> words, PdfParserOptions options)` → `(IReadOnlyList<DetectedTable> tables, IReadOnlyList<WordBox> proseWords)`; `DetectedTable` = rows of cell strings + source Y-range. Algorithm per design §3: Y-band row clustering (tolerance = median word height * 0.6 — pin the constant with tests), persistent-X-gap column detection across >= MinTableRows adjacent rows, ragged-header tolerance (one row may miss <= 1 column), maximal runs, bail-to-prose when column counts disagree beyond tolerance. `RenderMarkdown(DetectedTable)` → pipe table with header separator.
- Test: `tests/Rag.NET.Parsers.Pdf.Tests/` NEW project (+ slnx) — `PdfTableExtractorTests.cs`, all synthetic `WordBox` data, hand-computed:

```csharp
// 1. ThreeByThreeGrid_DetectedAsOneTable (exact cells).
// 2. ProseParagraph_NoTable (varying X positions → all words prose).
// 3. TwoColumnsOnly_MinColumnsRespected (MinTableColumns=3 → no table).
// 4. TwoRowsOnly_MinRowsRespected.
// 5. RaggedHeaderRow_Tolerated (header spans 2 of 3 columns → still one table).
// 6. MixedPageProseAboveTableBelow_SplitCorrectly (prose words + grid → both outputs).
// 7. RenderMarkdown_PipeFormat (exact string incl. header separator + pipe escaping in cells).
// 8. InconsistentColumns_BailsToProse.
```

**Commit** `feat(parsers): PdfPig-geometry table extraction core (pure clustering)`

### Task C2: parser integration

**Files:**
- Modify: `src/Rag.NET.Parsers.Pdf/PdfDocumentParser.cs` (43 lines today — read above; rebuild): ctor gains `PdfParserOptions? options = null` + `ILogger<PdfDocumentParser>? logger = null` (defaults preserve today's behavior except tables-on); per page: map `page.GetWords()` (PdfPig `Word` → `WordBox`; PdfPig Y-axis is bottom-up — normalize so clustering sees top-down consistently, document) → `PdfTableExtractor.Extract` when `ExtractTables` → emit prose section (from prose words, reading order: sort by normalized Y then X — approximates today's `page.Text` ordering; ACCEPT minor whitespace diffs but pin the new behavior with tests) + one section per table (`Heading = "table"`, Markdown text, `PageNumber`); extractor exception → LoggerMessage warning + whole page as prose via `page.Text` exactly as today (degraded).
- Modify: `src/Rag.NET.Parsers.Pdf/PdfParserBuilderExtensions.cs` (read first) — `AddPdfParser(Action<PdfParserOptions>? configure)` overload; parameterless keeps working (defaults).
- Test: `PdfDocumentParserTests.cs` in the new test project — embedded fixtures: keep `sample.pdf` prose regression (copy the resource or reference the integration asset); NEW `sample-table.pdf` embedded fixture (a small PDF containing a real 3x3 table — source: generate once with any tool e.g. a printed markdown table via a headless browser, or craft with PdfPig's writer if it has one — PdfPig CAN write simple PDFs via `PdfDocumentBuilder` with text at coordinates: use THAT to generate the fixture in a test-utility or check it in; VERIFY PdfDocumentBuilder exists in the installed version):

```csharp
// 1. Parse_TablePdf_EmitsTableSection (Heading == "table", Markdown contains expected cells).
// 2. Parse_TablePdf_ProseExcludesTableWords.
// 3. Parse_ProsePdf_BehaviorPreserved (sample.pdf → same section count/pages as before).
// 4. Parse_ExtractTablesFalse_AllProse.
// 5. DI: AddPdfParser(configure) applies options; parameterless still registers.
```

- Integration: matrix entry for the table PDF.

**Commit** `feat(parsers): wire table extraction into PdfDocumentParser`

---

## Part D — OCR fallback

### Task D1: seam + gated engine

**Files:**
- Create: `src/Rag.NET.Parsers.Pdf/Ocr/IPdfOcrEngine.cs` — internal: `string? Recognize(byte[] imageBytes)` (null/empty = no text). Always compiled.
- Create: `src/Rag.NET.Parsers.Pdf/Ocr/TesseractOcrEngine.cs` — `#if ENABLE_OCR` implementation (TesseractEngine + Pix.LoadFromMemory, options-driven tessdata path + language — mirror `src/Rag.NET.Parsers.Vision/ImageDocumentParser.cs` TryOcr, read it first); `#else` a stub whose ctor throws the instructive InvalidOperationException (Vision wording: add `<EnableOcr>true</EnableOcr>` to the project + tessdata guidance). Factory: `PdfOcrEngineFactory.Create(PdfParserOptions)` internal.
- Modify: `src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj` — the Vision `<EnableOcr>` gate block verbatim (DefineConstants + conditional `Tesseract 5.*`), plus a comment noting the deliberate Vision duplication + future shared-package consolidation (design §4).
- Test: gate-off behavior — `OcrDisabled_UseOcrFallbackTrue_ThrowsInstructive` (construct parser with UseOcrFallback=true in a non-ENABLE_OCR compilation → the factory throw surfaces at first OCR-needed page OR at construction — pick construction-time (fail fast, matches misconfiguration-loud posture), pin it).

**Commit** `feat(parsers): compile-gated Tesseract OCR engine seam for PDF`

### Task D2: per-page fallback + fixtures + reconciliation

**Files:**
- Modify: `PdfDocumentParser` — per-page: when `UseOcrFallback` && `page.Text.Length < OcrMinCharacters`: `page.GetImages()` (largest area first — VERIFY the PdfPig image API: `IPdfImage.RawBytes`/`TryGetPng` — inspect the installed version's surface; prefer PNG bytes when available else raw), run the engine per image until non-empty text; emit `Heading = "ocr"` section with `PageNumber`; no images / all-empty / engine exception (non-OCE) → LoggerMessage warning + skip page (matches today's empty-page skip). Engine constructed once per parser (thread-safe? Tesseract engines are NOT thread-safe — parser is a DI singleton used sequentially per document but documents parse in parallel under Phase 1.3: guard with a lock around Recognize, document why).
- Test (fake engine via the internal seam — InternalsVisibleTo the new test project):

```csharp
// 1. OcrFallback_ShortPage_EngineInvoked_SectionEmitted (fake engine returns text; Heading "ocr").
// 2. OcrFallback_LongPage_EngineNotInvoked.
// 3. OcrFallback_NoImages_WarnsAndSkips.
// 4. OcrFallback_EngineReturnsEmpty_SkipsPage.
// 5. OcrFallback_Disabled_NeverInvoked (default options).
// 6. ConcurrentParse_TwoDocuments_NoTornEngineState (lock smoke — parallel parse with fake engine asserting no overlapping Recognize via Interlocked).
```

- Real-Tesseract integration test: env-gated (`RAGNET_TESSDATA` pointing at a tessdata dir → else `Assert.Skip`) in the Pdf test project under `#if ENABLE_OCR` — NOTE: CI/local default build has the gate OFF, so this test compiles away; document in the test file header. Embedded `sample-scanned.pdf` fixture (generate: render text to a PNG in-code via ImageSharp? NO new deps — craft with PdfPig's builder embedding a pre-made tiny PNG of text checked in as a resource).
- features.md: tick BOTH remaining rows (PDF Table Extraction, OCR) + detail Status lines (OCR: Tesseract via EnableOcr gate; Azure Doc Intelligence deferred; vector-only-scanned-pages limitation).
- `docs/planning/ROADMAP.md` + `MILESTONE.md`: Phase 1.5 complete (2026-07-25).
- Parser guide (grep docs/guide for where parsers are documented — likely a parsers section): table extraction + OCR setup (EnableOcr, tessdata, language), limitations.

**Commit** `feat(parsers): per-page OCR fallback + docs; tick features; complete phase 1.5`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors (gate OFF — the default).
2. Full `dotnet test tests/Rag.NET.Tests` + new parser test projects + `tests/Rag.NET.Parsers.IntegrationTests` green.
3. features.md: all four rows ticked; EPUB/Email checkbox-vs-Status contradictions resolved.
4. Final whole-phase review over the branch range; merge decision.
