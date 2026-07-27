# Azure Document Intelligence OCR Implementation Plan (Phase 2.4)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A document-level OCR path where Azure Document Intelligence receives the whole PDF and returns every page, shipping as its own ungated package, with OCR spend visible in `ICostLedger` and bounded by a page cap.

**Architecture:** Per `docs/plans/2026-07-27-azure-document-intelligence-design.md`. Part A extends `ICostLedger` so a per-page API can be represented honestly — it gates everything else because the engine records through it. Part B adds the public `IDocumentOcrEngine` seam and wires `PdfDocumentParser` to it. Part C is the Azure package. Part D is docs. **A → B → C → D, strictly sequential**: each depends on the previous.

**Tech Stack:** .NET 10, `Azure.AI.DocumentIntelligence` (pin `1.*`, matching the repo's floating-major convention for Azure SDKs), PdfPig, xUnit v3, WireMock.Net for cassettes.

**Conventions:** MA0051 (≤60-line methods), MA0015, ZA0601/ZA0501 (no LINQ/boxing in hot loops), EPS05/EPS06, HLQ012/HLQ013 — all warnings-as-errors, build must end 0/0. **HLQ012 will bite**: `PdfDocumentParser.ParseAsync` is an async iterator and already carries the comment *"Index loop: spans (CollectionsMarshal.AsSpan) cannot cross yield boundaries"* — build collections in synchronous helpers, and do not add a pragma (the repo has two, both justified, and the standing instruction is not to add more). Logging goes through `PdfParserLog` (LoggerMessage source-gen), never `logger.LogWarning` directly. Conventional commits ending with a blank line then `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Never stage `.lucent/*` or `.claude/worktrees/*`.**

**Read before starting:** the design doc in full; `src/Rag.NET.Parsers.Pdf/PdfDocumentParser.cs` (especially `ParsePage` at ~:74 and `OcrSections` at ~:116-165); `src/Rag.NET.Parsers.Pdf/Ocr/IPdfOcrEngine.cs`; `src/Rag.NET.Abstractions/Abstractions/ICostLedger.cs` and `Models/CostEntry.cs`/`CostKind.cs`; `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs:27-42` (the `clientOptions` test seam); `src/Rag.NET.DataProviders.AzureBlob/AzureBlobDataProviderExtensions.cs:12-43` (dual-credential overloads); `tests/Rag.NET.Testing/WireMockServerFixture.cs`.

---

## Part A — Make `ICostLedger` able to represent a per-page API

### Task A1: `CostKind` + `CostEntry`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/CostKind.cs` — add an OCR member. Its doc comment currently says *"The kind of LLM call a cost entry was produced by"*; OCR is not an LLM call, so the summary needs widening too.
- Modify: `src/Rag.NET.Abstractions/Models/CostEntry.cs` — drop `required` from `InputTokens`/`OutputTokens` (defaulting to 0) and add `int Pages { get; init; }`.
- Test: `tests/Rag.NET.Tests/` — wherever cost-ledger tests live (grep `CostEntry`).

Dropping `required` is source-compatible: existing object initialisers that set both still compile. Verify that claim by building, not by assuming.

An OCR entry carries `Pages` and zero tokens. Do **not** fabricate a token count — that is the whole reason this change exists.

```csharp
// 1. CostEntry_OcrEntry_CarriesPagesAndZeroTokens
// 2. CostEntry_ExistingTokenEntries_StillCompileAndRoundTrip — guards the `required` removal
// 3. SqliteCostLedger round-trips an OCR entry (Pages survives persistence)
// 4. Legacy table without the pages column migrates, preserving existing spend
// 5. Concurrent construction against a legacy table does not throw (the TOCTOU guard)
```

**Schema migration — resolved, decision recorded.** `SqliteCostLedger` persists entries and its schema *is* column-per-field (`day, kind, tokens_in, tokens_out, cost`, PK `(day, kind)`), so `pages` needs a column and a table created by an earlier version will not have one — `CREATE TABLE IF NOT EXISTS` leaves it alone, after which every insert naming `pages` fails.

This instruction originally read "fail fast on an old schema rather than silently dropping the column". **Fail-fast is superseded**; the implementation probes `pragma_table_info` and runs an additive `ALTER TABLE cost_ledger ADD COLUMN pages INTEGER NOT NULL DEFAULT 0`, documented in the class remarks. Reasoning:

- SQLite's `ADD COLUMN` with a **constant** default is a metadata-only, O(1) operation — no table rewrite, even on a large ledger.
- `DEFAULT 0` is not a guess but the **true value** for pre-existing `Chat`/`Embedding` rows: they were never billed pages.
- There is **no irreconcilable shape conflict**, unlike Phase 2.3's `sparsevec(100)`-vs-`sparsevec(30522)` case that fail-fast exists to catch. `pages` cannot pre-exist at a wrong shape, so the failure mode fail-fast protects against does not exist here.
- Failing fast on a budget-accounting side table would turn a routine library upgrade into a hard startup failure requiring manual DDL, for a change that provably cannot lose data.

Silently dropping the column remains rejected — it defeats the column's purpose. Silently ALTERing without saying so also remains rejected: the class remarks state in bold that the statement runs automatically against the caller's database.

**The migration must be concurrency-safe.** Probing alone is TOCTOU: SQLite serialises the two ALTER *statements*, but nothing guards the gap between the probe reading "absent" and this process issuing its ALTER, so concurrent openers of one ledger file all probe absent, all ALTER, and the losers get `duplicate column name` — out of the **constructor**, which `RagBuilderExtensions.cs:354-355` calls from a `TryAddSingleton` factory, i.e. a host startup crash on the first start after upgrade. Guard the ALTER with `catch (SqliteException) when (HasPagesColumn(conn))`: the re-probe (rather than message-matching) keeps unrelated DDL failures fatal. Probe with `COLLATE NOCASE` — SQLite column names are case-insensitive.

**Commit:** `feat(abstractions): let CostEntry represent per-page costs`

---

## Part B — The document-level seam

### Task B1: `IDocumentOcrEngine` + `DocumentOcrResult`

**Files:**
- Create: `src/Rag.NET.Parsers.Pdf/Ocr/IDocumentOcrEngine.cs` (**public**)
- Create: `src/Rag.NET.Parsers.Pdf/Ocr/DocumentOcrResult.cs` (**public**)

```csharp
ValueTask<DocumentOcrResult> RecognizeAsync(Stream pdf, CancellationToken cancellationToken);
```

`DocumentOcrResult` carries per-page text keyed by **1-based** page number (PdfPig's `Page.Number` is 1-based — confirm) plus the page count the provider billed for.

Public because the Azure package is a separate assembly. Async and cancellable from the outset: the existing `IPdfOcrEngine` being sync and token-less is the mistake this seam exists not to repeat — say so in the XML doc.

`IPdfOcrEngine` is **not** touched. Its summary calls itself the seam for *the* compile-gated engine; correct that wording to acknowledge a sibling seam now exists.

**Commit:** `feat(parsers): document-level OCR seam for whole-PDF engines`

### Task B2: wire `PdfDocumentParser`

**Files:**
- Modify: `src/Rag.NET.Parsers.Pdf/PdfDocumentParser.cs`
- Modify: `src/Rag.NET.Parsers.Pdf/PdfParserOptions.cs` — add `MaxOcrPages` (see below)
- Modify: `src/Rag.NET.Parsers.Pdf/PdfParserBuilderExtensions.cs` — `ValidateOptions` at ~:47-51 throws on empty `TessDataPath`/`OcrLanguage` whenever `UseOcrFallback` is set, which an Azure user would hit spuriously. Make it engine-conditional.
- Test: `tests/Rag.NET.Parsers.Pdf.Tests/`

**The flow.** PdfPig parses first, as today. If **any** page's text is below `OcrMinCharacters` and a document-level engine is configured, make **one** `RecognizeAsync` call with the whole stream, then use the returned text **only for the sub-threshold pages**. Pages PdfPig read successfully keep PdfPig's text — it is exact.

**`MaxOcrPages`.** Azure bills every page of the submitted document, so a 500-page PDF with one scanned page costs 500 pages. Above the cap, skip OCR entirely and log a warning naming the page count and the cap (new `PdfParserLog` entry). Pick a default that is generous but not unbounded and justify it in the XML doc.

**Stream handling.** `ParseAsync` gets one `Stream` that PdfPig already consumed. Seekable → rewind. Non-seekable → buffer to memory. `RagError.NonSeekableStream` is the repo's precedent for refusing; decide and document which you do.

**Two engines configured is a registration-time error**, not a silent precedence rule.

**Do not serialize through `_ocrLock`.** That lock exists because Tesseract is not thread-safe (see its comment at `PdfDocumentParser.cs:19-22`); a network client must not inherit it, and `lock` cannot span `await` anyway. The document-level path must not touch it. `ConcurrentParse_TwoDocuments_NoTornEngineState` pins the Tesseract behaviour — keep it passing.

```csharp
// 1. DocumentOcr_SubThresholdPages_UseOcrText_OtherPagesKeepPdfPigText
// 2. DocumentOcr_NoSubThresholdPages_EngineNeverCalled — the cost-critical assertion
// 3. DocumentOcr_CalledExactlyOncePerDocument — not once per page
// 4. DocumentOcr_DocumentAboveMaxOcrPages_SkipsWithWarning
// 5. DocumentOcr_EngineThrows_FallsBackToPlainTextLosslessly (degraded-never-broken)
// 6. DocumentOcr_Cancellation_Propagates
// 7. BothEnginesConfigured_ThrowsAtRegistration
// 8. NonSeekableStream_HandledPerTheDocumentedChoice
```

Use a hand-written fake `IDocumentOcrEngine` (the PDF test project uses hand-written fakes, not NSubstitute — keep that). Test 2 and 3 are the ones that stop this being expensive; make them unambiguous.

**Commit:** `feat(parsers): route whole-PDF OCR through the document-level engine`

---

## Part C — The Azure package

### Task C1: project + client + engine

**Files:**
- Create: `src/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence/` — csproj referencing `Rag.NET.Parsers.Pdf` and `Azure.AI.DocumentIntelligence` (`1.*`), **unconditional, no compile gate**. Add to `Rag.NET.slnx`.
- Create: the engine implementing `IDocumentOcrEngine`.
- Create: builder extensions with **dual credential overloads** — `AzureKeyCredential` and `TokenCredential` — following `AzureBlobDataProviderExtensions.cs:12-43`, including its `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` guards on every argument.

**No `EnableOcr` gate.** That gate exists for Tesseract's native binaries and traineddata files; a managed REST client has neither, and reusing it would force Azure-only users to pull Tesseract's native payload.

**Test seam:** take an optional `DocumentIntelligenceClientOptions?` in the constructor and do **not** expose it on the builder extension — exactly what `AzureAISearchVectorStore.cs:27-42` does so tests can inject a transport.

**Cost recording.** The engine resolves `ICostLedger?` from DI and records per call with `Pages` set from the response. The parser stays unaware of billing. If no ledger is registered, recording is a no-op — not an error.

**Commit:** `feat(parsers): Azure Document Intelligence OCR engine`

### Task C2: cassette tests

**Files:**
- Create: `tests/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests/`
- Cassettes under the established `Cassettes/{name}/` convention.

**The long-running-operation shape is the hard part.** `AnalyzeDocument` returns **202 + an `Operation-Location` header**, and the SDK polls until terminal. A cassette must capture the 202, the header, and the poll responses. **Neuter retry and polling delays** through `DocumentIntelligenceClientOptions` or the suite will sit for tens of seconds — verify the actual wall-clock time and report it.

Note `[CollectionDefinition]` resolves only within the declaring assembly (see the comment in `tests/.../WireMockCollection.cs`), so this project needs its own collection definition.

```csharp
// 1. RecognizeAsync_ReturnsPerPageText — keyed by 1-based page number
// 2. RecognizeAsync_RecordsCostWithPages — against a fake ICostLedger
// 3. RecognizeAsync_NoLedgerRegistered_StillSucceeds
// 4. RecognizeAsync_Cancellation_StopsPolling
// 5. RecognizeAsync_ServiceError_SurfacesForTheParserToDegrade
// 6. DI: both credential overloads resolve the engine
```

Plus an **env-gated live test** mirroring `RAGNET_TESSDATA` — `Assert.SkipWhen` on a missing endpoint/key, so a real call is possible but never required in CI.

**Commit:** `test(parsers): Azure Document Intelligence cassette and live-gated tests`

---

## Part D — Docs

**Files:**
- `docs/guide/ingestion.md` — the OCR limitations block becomes **engine-conditional**. Three of its four bullets (vector-only pages, CCITT/JBIG2, Tesseract thread-safety) are false for the Azure path. Add whole-document billing and `MaxOcrPages`.
- `docs/reference/features.md` — the OCR row (~:1062) and its Status paragraph (~:533) both say "Azure Doc Intelligence deferred".
- `PdfParserOptions.UseOcrFallback`'s XML doc hard-codes the `<EnableOcr>` requirement — no longer true with a second engine.
- Document the cost behaviour change: **OCR spend now counts toward the same budget window `UseCostBudgeting` enforces for chat and embedding**, so enabling OCR can trip those gates.

**Commit:** `docs(parsers): Azure Document Intelligence OCR and the engine-conditional limits`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Green, with exact counts: `tests/Rag.NET.Parsers.Pdf.Tests`, the new Azure test project, `tests/Rag.NET.Parsers.IntegrationTests`, `tests/Rag.NET.Tests`.
3. Confirm the Tesseract path is unchanged — build with `/p:EnableOcr=true` and run the gated suite, since Part B touches the file both paths share.
4. `docs/planning/ROADMAP.md` + `MILESTONE.md` — **at close-out, after the whole-phase review, not per part.**
5. Whole-phase review; merge decision.
