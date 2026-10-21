# Azure Document Intelligence Parser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Rag.NET.Parsers.AzureDocumentIntelligence`, an `IDocumentParser` that sends TIFF and HEIF (and, opt-in, PDF/images/Office/HTML) to Azure Document Intelligence. Also fix `.tif` / `.heif` / `.heic` being ingested as text.

**Architecture:** A new package holds a public `AzureDocumentIntelligenceAnalyzer` (client, polling, cost recording) and the parser on top of it. The existing `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` OCR engine is re-pointed at the same analyzer, with no change to its API or behaviour (design option B). Claims are declared per enabled format, so `AddRagNet`'s existing conflict guard prevents silent shadowing.

**Tech Stack:** .NET 10, `Azure.AI.DocumentIntelligence` (already centrally pinned), xunit v3 on MTP, WireMock.Net cassettes.

**Spec:** `docs/plans/2026-10-21-azure-document-intelligence-parser-design.md`. Read it before any task; section numbers below (§n) refer to it.

## Global Constraints

- Package id and root namespace are exactly `Rag.NET.Parsers.AzureDocumentIntelligence`.
- The new package references `Rag.NET.Abstractions`, `Azure.AI.DocumentIntelligence`, `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`. It must **not** reference `Rag.NET.Parsers.Pdf`.
- `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` keeps its package id and public API. Every existing test in `tests/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` passes **without edits**.
- The default `Formats` is `Tiff | Heif`, which claims exactly `image/tiff`, `image/heif` and `image/heic`.
- The default `ModelId` is `"prebuilt-read"`, the default `PricePerPage` is `0.0015m`, and the default `PollingInterval` is `TimeSpan.FromSeconds(1)`. These are the engine's defaults.
- The default `MaxBytes` is `500L * 1024 * 1024`, Azure's S0 limit.
- `<VerifiedBy>unit</VerifiedBy>` with a `<VerifiedByReason>` until a real capture is committed. No cassette may be described as recorded unless it was.
- Commit headers stay at 100 characters or fewer and follow conventional commits; commitlint runs on every commit. No nested parentheses in commit bodies.
- No `InternalsVisibleTo` across packages. Anything the old package uses from the new one is public.

## Review Focus

1. **A non-seekable stream larger than `MaxBytes`:** buffer at most `MaxBytes + 1` bytes and throw before any upload; never buffer the whole stream first. Test in Task 4.
2. **A paragraph whose `boundingRegions` span two pages** is attributed to the first region's page, once, never duplicated. Test in Task 3.
3. **A content type with different casing or parameters** (`IMAGE/TIFF`, `image/tiff; charset=x`): `CanParse` matches on the media type, case-insensitively. Test in Task 3.
4. **A successful analyze returning zero pages and zero paragraphs** yields zero sections and records no cost; it does not throw. Test in Task 3.
5. **A multi-frame TIFF** produces sections numbered 1…n in frame order, with `SectionIndex` monotonic across pages. Test in Task 3.

---

### Task 1: Stop ingesting `.tif` / `.heif` / `.heic` as text

**Files:**
- Modify: `src/Rag.NET.Abstractions/Containers/ContentTypeMap.cs` — the map around line 66.
- Test: `tests/Rag.NET.Tests/Ingestion/DocumentContentTypeResolverTests.cs`

**Interfaces:**
- Produces: `.tif` → `image/tiff`, `.heif` → `image/heif`, `.heic` → `image/heic`, all via `ContentTypeMap.FromFileName`.

- [ ] **Step 1: Write the failing test.** `[Theory]` `ResolveContentType_ImageScanExtensions_AreNotTreatedAsText`, with cases `("scan.tif", "image/tiff")`, `("scan.TIF", "image/tiff")`, `("photo.heif", "image/heif")` and `("photo.heic", "image/heic")`. Each builds `DocumentMetadata { FileName = …, DocumentId = … }` and asserts `metadata.ResolveContentType()` equals the expected type, and therefore not `"text/plain"`.
- [ ] **Step 2: Run it and confirm it fails.** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ImageScanExtensions"`. Expected: fails with `text/plain` for the `.tif` and `.heif` cases.
- [ ] **Step 3: Add the three map entries.** Update the comment above `.tiff`: it is still unclaimed unless the Azure parser is registered, and an unclaimed TIFF is warn-and-skipped instead of parsed as text.
- [ ] **Step 4: Rerun the test.** Expected: pass.
- [ ] **Step 5: Commit.** `fix(ingestion): map .tif, .heif and .heic so scans are not ingested as text`

### Task 2: New package with the shared analyzer

**Files:**
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/Rag.NET.Parsers.AzureDocumentIntelligence.csproj`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceAnalyzer.cs`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceAnalyzerSettings.cs`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceLog.cs`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/README.md` — shared header, copied from the existing package's README and adapted.
- Create: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/` — csproj modelled on the existing ADI test project: `RequiresSecrets`, WireMock, `Rag.NET.Testing`.
- Modify: `Rag.NET.slnx` — add both projects.
- Modify: `tests/Rag.NET.PackageValidation.Tests/ProducedPackageTests.cs:67` — 73 → 74, with a dated `<para>` in the established style naming #824.
- Test: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/AzureDocumentIntelligenceAnalyzerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AzureDocumentIntelligenceAnalyzerSettings` with `string ModelId`, `decimal PricePerPage`, `TimeSpan PollingInterval` and `string? Locale`, using the defaults from Global Constraints. It also has `public static AzureDocumentIntelligenceAnalyzerSettings Validate(AzureDocumentIntelligenceAnalyzerSettings settings, string paramName)`, which has the same three checks and messages as today's engine `Validate`, with the type name in the messages parameterised by the caller.
  - `public sealed class AzureDocumentIntelligenceAnalyzer`. Constructors:
    - `(Uri endpoint, AzureKeyCredential credential, AzureDocumentIntelligenceAnalyzerSettings settings, ICostLedger? costLedger, ILogger? logger, DocumentIntelligenceClientOptions? clientOptions)`
    - the same with `TokenCredential` in place of `AzureKeyCredential`.
  - `public async Task<AnalyzeResult> AnalyzeAsync(BinaryData content, CancellationToken cancellationToken)`, which:
    - starts with `WaitUntil.Started` and waits with `PollingInterval`;
    - records `CostEntry { Kind = CostKind.Ocr, Pages = result.Pages?.Count ?? 0, Cost = pages * PricePerPage }` when the ledger is non-null and pages > 0;
    - swallows and logs a ledger failure, but never an `OperationCanceledException`;
    - lets `RequestFailedException` and `OperationCanceledException` propagate.
  - `ILogger` is non-generic on purpose: the OCR engine passes its own `ILogger<AzureDocumentIntelligenceOcrEngine>`, so log categories do not change.

- [ ] **Step 1: Write the failing tests** against a WireMock server, using hand-written cassettes in `Cassettes/` that copy the existing ADI cassettes' envelope:
  - `AnalyzeAsync_TwoPageResult_RecordsTwoPagesAtThePricePerPage` asserts `ledger.Entries.Single().Pages == 2` and `Cost == 2 * 0.0015m`.
  - `AnalyzeAsync_LedgerThrows_StillReturnsTheResult`.
  - `AnalyzeAsync_ServiceReturns401_ThrowsRequestFailedException`.
  - `Validate_NegativePrice_Throws` and `Validate_BlankModelId_Throws`.
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests`. Expected: a compile failure, because the types do not exist yet.
- [ ] **Step 3: Implement the analyzer and settings**, moving the logic out of `AzureDocumentIntelligenceOcrEngine.RecognizeAsync` and `RecordCostAsync` rather than rewriting it. The csproj carries:
  - `<VerifiedBy>unit</VerifiedBy>`
  - `<VerifiedByReason>`: cassettes are hand-written from the documented response shape; the env-gated live test runs nightly; a captured cassette is owed. Name `RAGNET_DOCINTEL_CAPTURE`.
  - a unique `<Description>`.
- [ ] **Step 4: Rerun those tests, then the repo-convention tests.** Run `dotnet test tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests` and `dotnet test tests/Rag.NET.RepoConventions.Tests`. Expected: pass. The README header, description, VerifiedBy, solution membership and central pins are all checked there.
- [ ] **Step 5: Commit.** `feat(parsers): add the Azure Document Intelligence analyzer package`

### Task 3: The parser

**Files:**
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceFormats.cs`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceParserOptions.cs`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceException.cs`
- Test: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/AzureDocumentIntelligenceDocumentParserTests.cs`, plus cassettes `analyze-tiff-three-frames.json`, `analyze-docx-paragraphs.json`, `analyze-layout-roles.json`, `analyze-empty.json` and `analyze-paragraph-spans-pages.json`.

**Interfaces:**
- Consumes: `AzureDocumentIntelligenceAnalyzer` and `AzureDocumentIntelligenceAnalyzerSettings` (Task 2).
- Produces:
  - `[Flags] public enum AzureDocumentIntelligenceFormats`, with `None = 0`, `Tiff`, `Heif`, `Pdf`, `Images`, `Word`, `Excel`, `PowerPoint` and `Html`, plus composite `Office = Word | Excel | PowerPoint`.
  - `public static class AzureDocumentIntelligenceFormatsExtensions`, with `IReadOnlyList<string> ContentTypes(this AzureDocumentIntelligenceFormats formats)`. The mapping:
    - `Tiff` → `image/tiff`
    - `Heif` → `image/heif`, `image/heic`
    - `Pdf` → `application/pdf`
    - `Images` → `image/png`, `image/jpeg`, `image/jpg`, `image/bmp`
    - `Word`, `Excel`, `PowerPoint` → the three OOXML types the Office parsers declare
    - `Html` → `text/html`
  - `public sealed class AzureDocumentIntelligenceParserOptions`, which holds `AzureDocumentIntelligenceAnalyzerSettings Analyzer { get; } = new()`, `AzureDocumentIntelligenceFormats Formats { get; set; } = Tiff | Heif` and `long MaxBytes { get; set; } = 500L * 1024 * 1024`.
  - `public sealed class AzureDocumentIntelligenceDocumentParser : IDocumentParser`, with:
    - constructor `(AzureDocumentIntelligenceAnalyzer analyzer, AzureDocumentIntelligenceParserOptions options)`
    - `CanParse(string contentType)`, matching the media type before any `;`, case-insensitively, against `options.Formats.ContentTypes()`.
  - `public sealed class AzureDocumentIntelligenceException(string fileName, string message, Exception inner) : Exception`, modelled on `VisionDescriptionException`.

**Section mapping, the algorithm the tests pin (§4):**
- Paragraphs that have `BoundingRegions` are grouped by `BoundingRegions[0].PageNumber`. One `DocumentSection` is emitted per page, ascending, with `Text` = that page's paragraphs joined by `"\n\n"` and `PageNumber` set.
- Paragraphs without `BoundingRegions` (Office, HTML) are emitted after the paged ones, one section each, in document order, with `PageNumber = null`.
- If `Paragraphs` is null or empty, fall back to page `Lines`, then `Words`, per page, which is today's engine behaviour.
- Roles:
  - `Title` sets `HeadingLevel = 1`; `SectionHeading` sets `HeadingLevel = 2`. The paragraph's text becomes `Heading` for the sections that follow it, until the next heading.
  - `PageHeader`, `PageFooter` and `PageNumber` paragraphs are dropped.
  - A paragraph with no role passes through unchanged.
- `SectionIndex` is 0-based and strictly increasing across everything emitted.

- [ ] **Step 1: Write the failing tests.** Each test feeds a cassette through the parser and asserts on the emitted sections.
  - `ParseAsync_ThreeFrameTiff_EmitsPagesOneToThreeInOrder`
  - `ParseAsync_DocxWithoutLines_UsesParagraphsNotWords`: asserts the text contains `"\n\n"` and a paragraph's full sentence intact.
  - `ParseAsync_LayoutRoles_SetHeadingsAndDropPageFurniture`
  - `ParseAsync_EmptyResult_EmitsNothingAndRecordsNoCost`
  - `ParseAsync_ParagraphSpanningTwoPages_AppearsOnceOnTheFirst`
  - `CanParse_MatchesMediaTypeIgnoringCaseAndParameters`: `"IMAGE/TIFF"` and `"image/tiff; x=y"` give true; `"application/pdf"` with default formats gives false.
  - `ParseAsync_ServiceFails_ThrowsAzureDocumentIntelligenceExceptionNamingTheFile`
  - `ParseAsync_Cancelled_PropagatesOperationCanceled`
- [ ] **Step 2: Run them and confirm they fail.** Expected: compile failure.
- [ ] **Step 3: Implement the types.** The parser buffers the stream into `BinaryData` (see Task 4 for the size cap), calls `AnalyzeAsync`, and maps the result as above. It wraps `RequestFailedException` in `AzureDocumentIntelligenceException`. `BudgetExceededException` and cancellation propagate.
- [ ] **Step 4: Rerun the parser tests.** Expected: all pass.
- [ ] **Step 5: Commit.** `feat(parsers): parse TIFF and HEIF through Azure Document Intelligence`

### Task 4: Registration, claims and the size cap

**Files:**
- Create: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceParserBuilderExtensions.cs`
- Modify: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceDocumentParser.cs` — enforce `MaxBytes`.
- Modify: `tests/Rag.NET.RepoConventions.Tests/ParserClaimCoverageTests.cs:62-79` — add the assembly; raise `FewestPlausibleOptedInParsers` if the count moves.
- Test: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/AzureDocumentIntelligenceParserBuilderExtensionsTests.cs`

**Interfaces:**
- Consumes: Tasks 2 and 3.
- Produces two overloads:
  - `public static TBuilder AddAzureDocumentIntelligenceParser<TBuilder>(this TBuilder builder, Uri endpoint, AzureKeyCredential credential, Action<AzureDocumentIntelligenceParserOptions>? configure = null) where TBuilder : IRagBuilder`
  - the same with `TokenCredential`.
- Both validate at registration: `Formats == None` throws `ArgumentException`, `MaxBytes <= 0` throws `ArgumentOutOfRangeException`, and analyzer settings are checked with `AzureDocumentIntelligenceAnalyzerSettings.Validate`. They register by factory, resolving `ICostLedger?` and a logger from the provider, and declare `ParserClaim.For<AzureDocumentIntelligenceDocumentParser>(contentType, "AddAzureDocumentIntelligenceParser")` for each `Formats.ContentTypes()` entry. This mirrors `AddPdfParser(configure)` (`PdfParserBuilderExtensions.cs:41-57`).

- [ ] **Step 1: Write the failing tests.**
  - `Register_Defaults_ClaimsExactlyTiffAndHeif`: collects the `ParserClaim` services and asserts the set equals `{ image/tiff, image/heif, image/heic }`.
  - `Register_WordAlongsideAddWordParser_ThrowsAtAddRagNet`: asserts the `InvalidOperationException` message names both parser types.
  - `Register_NoFormats_Throws`
  - `Register_NonPositiveMaxBytes_Throws`
  - `ParseAsync_NonSeekableStreamOverMaxBytes_ThrowsBeforeAnyRequest`: uses `MaxBytes = 16`, a 17-byte non-seekable stream, and asserts WireMock received **zero** requests.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement the extensions and the cap.** Read with a bounded copy into a buffer of at most `MaxBytes + 1` bytes. Throw `AzureDocumentIntelligenceException` naming the file, the cap and "Azure's limit" when the extra byte arrives.
- [ ] **Step 4: Rerun this project's tests and the repo-convention tests.** Run `dotnet test tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests` and `dotnet test tests/Rag.NET.RepoConventions.Tests`. Expected: pass.
- [ ] **Step 5: Commit.** `feat(parsers): register the Azure Document Intelligence parser with explicit format claims`

### Task 5: Re-point the OCR engine at the shared analyzer

**Files:**
- Modify: `src/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.csproj` — add a `ProjectReference` to the new package.
- Modify: `src/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence/AzureDocumentIntelligenceOcrEngine.cs` — `RecognizeAsync` delegates to `AzureDocumentIntelligenceAnalyzer.AnalyzeAsync`. Delete `RecordCostAsync`. Keep `BuildPageText` and `AppendPageContent`, which are PDF-line semantics. `Validate` maps `AzureDocumentIntelligenceOcrOptions` onto `AzureDocumentIntelligenceAnalyzerSettings` and keeps throwing the same messages, naming `AzureDocumentIntelligenceOcrOptions`.
- Modify: `src/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence/AzureDocumentIntelligenceLog.cs` — remove the moved log message only if nothing still uses it.

**Interfaces:**
- Consumes: `AzureDocumentIntelligenceAnalyzer` (Task 2).
- Produces: no public API change. Verify with `find_breaking_changes` on the old assembly, or by diffing its public API surface before and after.

- [ ] **Step 1: Record the baseline.** Run `dotnet test tests/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` and note the pass count.
- [ ] **Step 2: Refactor** as described, without touching the test project.
- [ ] **Step 3: Rerun the same command.** Expected: the same pass count with **zero test-file changes**; `git diff --stat tests/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` is empty.
- [ ] **Step 4: Check the old package's public API is unchanged** with roslyn-codelens `find_breaking_changes`. Expected: none.
- [ ] **Step 5: Commit.** `refactor(parsers): share the Azure analyzer between the OCR engine and the parser`

### Task 6: Live test, capture hook and docs

**Files:**
- Create: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/AzureDocumentIntelligenceParserLiveTests.cs` — mirrors `AzureDocumentIntelligenceLiveTests.cs`: skipped without `RAGNET_DOCINTEL_ENDPOINT` / `RAGNET_DOCINTEL_KEY`, and captures through `ResponseCapturePolicy` when `RAGNET_DOCINTEL_CAPTURE` is set. Add `ResponseCapturePolicy` to `Rag.NET.Testing` if it can move there without duplication; otherwise link the file.
- Create: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/Resources/sample-scan.tif` — a small single-frame TIFF with known text, generated once from the existing `sample-scanned.pdf` page and committed.
- Modify: `docs/guide/ingestion.md` — new subsection with the §1 format table, the default claims, the conflict behaviour, Office billing per 3,000 characters, and the `MaxBytes` caveat.
- Modify: `docs/reference/features.md` — add a row for the parser.
- Modify: `src/Rag.NET.Parsers.AzureDocumentIntelligence/README.md` — Install, Setup and Example, all C# compiling against the package.

- [ ] **Step 1: Write the live test** `ParseAsync_AgainstTheRealService_ReturnsTheScansText`. It asserts a known word from the fixture appears and that `PageNumber == 1`.
- [ ] **Step 2: Run it locally and confirm it reports skipped**, not passed or failed: `dotnet test tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests --filter "FullyQualifiedName~Live"`.
- [ ] **Step 3: Write the docs.** Every package name cited must exist under `src/`, which `DocumentationPackageReferenceTests` enforces.
- [ ] **Step 4: Run full validation.** Every test project, enumerated rather than listed by hand. Then `rm -rf artifacts/packages`, `dotnet pack` with the GitVersion SemVer, run `Rag.NET.PackageValidation.Tests` (74 packages, README and docs fences compile), and run `npm run build`. Expected: all green.
- [ ] **Step 5: Commit.** `docs(parsers): document the Azure Document Intelligence parser`

---

## After the plan

- **Open the PR** referencing #824 and the design note.
- **Owed after merge, and the user's step:** one capture run with a real key, `RAGNET_DOCINTEL_CAPTURE=<dir>`. Then commit the captured bodies as a `*-recorded.json` cassette, flip `<VerifiedBy>` to `recorded`, and drop the reason. Until then the package states what it is.
