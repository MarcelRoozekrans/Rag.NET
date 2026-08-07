# Parser Registration Ownership — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make content-type ownership declarable and enforced, so that two parsers claiming one type is either a loud error or an explicit, working override — never a silent one.

**Architecture:** `AddParser<T>(replaces:)` lands first as the vocabulary for a deliberate override; `UseQAPairsChunking()` adopts it; only then are the 11 missing `ParserClaim` declarations added, guarded by a convention test that probes every parser against `ContentTypeMap`.

**Tech Stack:** .NET 10, xUnit v3, Microsoft.Extensions.DependencyInjection.

**Design:** `docs/plans/2026-08-07-parser-registration-ownership-design.md`

---

## Context

`ParserClaim` exists so two parsers claiming one content type is a startup error. **It is declared by 6 parsers covering 8 content types; 11 parsers covering ~22 declare nothing** — including `CsvDocumentParser` and `JsonDocumentParser` in core.

Both live collisions go undetected:

| Content type | Claimants |
|---|---|
| `text/csv` | `CsvDocumentParser` (**core**) + `QAPairsDocumentParser` |
| `…spreadsheetml.sheet` | `ExcelDocumentParser` (Office) + `QAPairsDocumentParser` |

Selection takes the **first** registered parser whose `CanParse` matches, and built-in claims register before the user's `configure` delegate. So `UseQAPairsChunking()` most likely registers a CSV parser that never runs.

## The ordering is load-bearing — do not reorder these tasks

Declaring `CsvDocumentParser`'s `text/csv` claim **before** QA-pairs has a way to declare an override turns `UseQAPairsChunking()` into a guaranteed startup error for every user of that feature.

```
Task 1 (API)  →  Task 2 (QAPairs adopts it)  →  Task 3 (declare the other 11)
```

Task 3 before Task 2 breaks the build's own test suite. **If you find yourself doing Task 3 first, stop.**

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`.** MA0051 (≤60-line methods), MA0048, MA0061, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Subject under 100 characters** — commitlint enforces it.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- **An incremental build is not a measurement** — `--no-incremental` for any quoted count.
- A file watcher edits `.csproj`/`.slnx` concurrently — **`git status` before committing**; it has previously removed a project from the solution mid-rebase.

**Baselines:** `Rag.NET.Tests` **1184**, `Rag.NET.RepoConventions.Tests` **44 + 1 skip**.

---

## Task 1: `AddParser<T>(replaces:)` — the override vocabulary

**Files:**
- Modify: `src/Rag.NET.Abstractions/ParserClaim.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs` (the `AddParser<TParser>` method)
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` (`ValidateParserClaims`)
- Test: `tests/Rag.NET.Tests/DependencyInjection/ParserClaimValidationTests.cs`

**It must do two things, and the second is the one that matters.** Silencing the conflict is not enough: selection takes the first matching registration, and built-ins register first, so an override that only suppresses the error still loses. **`replaces:` must remove the replaced parser's `IDocumentParser` service descriptor and its `ParserClaim`, together.**

**Step 1: Write the failing test**

```csharp
[Fact]
public void AddParser_WithReplaces_RemovesTheReplacedParserAndItsClaim()
{
    var services = new ServiceCollection();
    services.AddRagNet(rag => rag.AddParser<FakeCsvParser>(replaces: typeof(CsvDocumentParser)));

    var provider = services.BuildServiceProvider();
    var parsers = provider.GetServices<IDocumentParser>().ToList();

    Assert.Contains(parsers, p => p is FakeCsvParser);
    Assert.DoesNotContain(parsers, p => p is CsvDocumentParser);
}

[Fact]
public void AddParser_WithReplaces_MakesTheReplacementWinSelection()
{
    // The point of the feature. Without descriptor removal this passes the claim check
    // and still loses, because selection takes the first match and built-ins register first.
    var services = new ServiceCollection();
    services.AddRagNet(rag => rag.AddParser<FakeCsvParser>(replaces: typeof(CsvDocumentParser)));

    var provider = services.BuildServiceProvider();
    var selected = provider.GetServices<IDocumentParser>().First(p => p.CanParse("text/csv"));

    Assert.IsType<FakeCsvParser>(selected);
}

[Fact]
public void AddParser_WithoutReplaces_StillConflictsWhenBothDeclare()
{
    // The escape hatch must not become a way to switch the guard off entirely.
    var services = new ServiceCollection();

    var ex = Assert.Throws<InvalidOperationException>(() =>
        services.AddRagNet(rag =>
        {
            rag.AddParser<FakeCsvParser>();
            rag.AddParser<SecondFakeCsvParser>();
        }));

    Assert.Contains("text/csv", ex.Message, StringComparison.Ordinal);
}
```

`FakeCsvParser`/`SecondFakeCsvParser` claim `text/csv`. **They must declare claims** to be seen by the validator — follow how the existing tests in this file build their doubles rather than inventing a new pattern.

**Step 2: Run it and watch it fail**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ParserClaimValidationTests"
```
Expected: FAIL — no `replaces` parameter exists.

**Step 3: Implement**

Add to `ParserClaim` a `ReplacesParserTypeName` (nullable, full type name), so a claim records what it overrode and the validator can report it. Then in `AddParser<TParser>`:

- when `replaces` is non-null, remove from `Services` every descriptor whose `ServiceType` is `IDocumentParser` and whose implementation is the replaced type, **and** every `ParserClaim` instance whose `ParserTypeName` equals the replaced type's `FullName`
- register `TParser` as normal

**Removal must be by `FullName`, not short name** — `ParserClaim`'s own remarks explain why, and `TwoParsersSharingAShortName_StillConflict` exists to hold that line.

**Step 4: Run to green, then run the whole suite**

```bash
dotnet test tests/Rag.NET.Tests
```
Expected: 1184 + 3 = **1187**.

**Step 5: Commit**

---

## Task 2: `UseQAPairsChunking()` declares its overrides

**Files:**
- Modify: `src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs` (around line 104)
- Test: `tests/Rag.NET.Chunking.Templates.Tests/` — follow the existing registration tests

**Do this before Task 3.** After Task 3 declares `CsvDocumentParser`'s claim, this is the only thing standing between `UseQAPairsChunking()` and a startup error for every user.

`QAPairsDocumentParser` claims `text/csv`, `application/vnd.ms-excel` and `…spreadsheetml.sheet`. Two of those genuinely collide, and the override is legitimate: a caller who asked for QA-pairs chunking wants that parser to win.

**Declare the override for `text/csv` against `CsvDocumentParser`, and for `…spreadsheetml.sheet` against `ExcelDocumentParser`.**

**The Excel one is conditional and that is the subtlety.** `ExcelDocumentParser` lives in `Rag.NET.Parsers.Office`, which may not be installed. Replacing a type that was never registered must be a **no-op, not an error** — `Rag.NET.Chunking.Templates` must not take a dependency on Office to say this. Prefer expressing the replacement by type *name* where the type may be absent, or make removal tolerant of a missing descriptor. **Say which you chose and why.**

**Write a test for both cases**: with Office registered, and without.

**State the behaviour change in the commit body:** enabling QA-pairs chunking now means plain CSVs are parsed as QA pairs, because that is what the override says. Today's behaviour is the reverse *and silent* — `CsvDocumentParser` wins and `QAPairsDocumentParser` never runs.

---

## Task 3: Declare the 11 missing claims

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` — `DeclareBuiltInParserClaims`, add `CsvDocumentParser` (`text/csv`) and `JsonDocumentParser` (`application/json`)
- Modify each satellite's registration extension: Audio, Epub, Html, Office (×3), Pdf, Vision (×2)

Follow the existing shape exactly — `Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs:72` is the reference. Every claim needs a truthful `RegistrationMethod` (the call a user would recognise, e.g. `AddPdfParser()`) and a `ParserOptOut` **only where one genuinely exists** — `null` where removing the call is the only way out.

The full set to declare:

| Parser | Content types |
|---|---|
| `CsvDocumentParser` | `text/csv` |
| `JsonDocumentParser` | `application/json` |
| `AudioDocumentParser` | `audio/flac`, `audio/mpeg`, `audio/wav` |
| `EpubDocumentParser` | `application/epub+zip` |
| `HtmlDocumentParser` | `text/html` |
| `ExcelDocumentParser` | `…spreadsheetml.sheet` |
| `PowerPointDocumentParser` | `…presentationml.presentation` |
| `WordDocumentParser` | `…wordprocessingml.document` |
| `PdfDocumentParser` | `application/pdf` |
| `ImageDocumentParser` | `image/bmp`, `image/gif`, `image/jpeg`, `image/jpg`, `image/png`, `image/webp` |
| `VideoDocumentParser` | `video/mp4`, `video/quicktime`, `video/webm`, `video/x-matroska`, `video/x-msvideo` |

**Take these from each parser's `CanParse`/`SupportedTypes`, not from this table.** The table is a checklist, not a source of truth — if it disagrees with the code, the code wins and **report the difference**.

**Run the full suite after this task specifically.** If anything now throws at registration, you have found a real collision this phase should resolve — report it rather than working around it.

---

## Task 4: The convention test that stops it rotting again

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/ParserClaimCoverageTests.cs`

`ParserClaim`'s remarks say a parser's accepted types cannot be discovered "without probing it against a guessed list". **`ContentTypeMap` is not a guessed list** — it is this library's own extension→MIME map, documented as covering "the content types handled by the Rag.NET parser packages". Probe against it.

Two assertions:

1. **Coverage** — for every registered `IDocumentParser`, every content type in `ContentTypeMap` that its `CanParse` accepts has a matching `ParserClaim`.
2. **The octet-stream rule** — no parser claims `application/octet-stream`. `ContentTypeMap`'s own remarks state the unknown-binary fallback assumes nothing claims it, and that a parser which does is guessing.

**Instantiating parsers may be the hard part** — several take an `IChatClient` or options. If reflection-instantiation is impractical for some, **say so and describe what you did instead** (a source-scanning variant is acceptable; silently skipping parsers is not — a coverage test with holes is the thing this task exists to prevent).

**Watch it go red.** Temporarily delete one claim added in Task 3, confirm the test fails naming that parser, restore it. **A guard nobody has seen fail is not a guard, and this repository has shipped three of those.** Report that you did this.

Expected: `RepoConventions` 44 → **46 + 1 skip**.

---

## Task 5: Retire `EmailTemplateDocumentParser`

**Files:**
- Delete: `src/Rag.NET.Chunking.Templates/EmailTemplateDocumentParser.cs`
- Modify: `src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs` — `UseEmailChunking`: remove the `registerParser` parameter and the parser/claim registration
- Modify: `src/Rag.NET.Chunking.Templates/Rag.NET.Chunking.Templates.csproj` — remove `MimeKit`
- Modify/delete affected tests

`Rag.NET.Parsers.Email`'s `EmailDocumentParser` is strictly more capable and `UseEmailChunking`'s own remarks already record that the chunking strategy "does not care which parser produced" its sections.

**Do not touch `QAPairsDocumentParser`.** `QAPairsChunkingStrategy` reads the answer out of `DocumentSection.Heading` as a documented internal contract with it — they are a matched pair. **CsvHelper and ClosedXML stay.**

**Verify MimeKit is actually gone** from the packed nuspec, not just the csproj:

```bash
dotnet pack src/Rag.NET.Chunking.Templates -c Release -p:Version=0.0.1-check -o <scratch>
```

Then read the nuspec's `<dependencies>`. **Phase 4.7 learned that a floating reference freezes into the nuspec; check the artefact, not the intent.**

---

## Task 6: Remove `CostBudgetOptions.DatabasePath`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/Options/CostBudgetOptions.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilderExtensions.cs:219-222` — the guard that throws
- Modify: affected tests

The property does nothing when left alone and throws when set. Remove it, `DefaultDatabasePath`, and the guard together — after removal the compiler is the error, which is strictly better than a runtime one.

**Check for XML `<see cref="CostBudgetOptions.DatabasePath"/>` references** (there are at least two in `RagBuilderExtensions.cs`) — a dangling cref is a CS1574 build failure waiting for the documentation phase to turn generation on.

---

## Task 7: Documentation

**Files:**
- `docs/guide/` — wherever parsers and chunking templates are documented (find it; do not create a new page)
- `docs/planning/ROADMAP.md` — close the entries this phase owned

Document:

- **The claim model**, and that `AddParser<T>(replaces:)` is how a deliberate override is declared — including that it *removes* the replaced parser rather than merely silencing the error.
- **The two-package story for email chunking** — `UseEmailChunking()` no longer brings a parser.
- **That enabling QA-pairs chunking makes plain CSVs parse as QA pairs**, which is the override doing its job.

In `ROADMAP.md`, record: the coverage gap as measured (6 parsers/8 types declared, 11/~22 not), that both live collisions were silent, the `image/jpeg` false positive and why it happened, and that **Phase 4.7's Task 10 is now partly complete** — MimeKit dropped, CsvHelper and ClosedXML deliberately retained.

**Do not tick a DoD box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release --no-incremental
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.Chunking.Templates.Tests
```

State every count with arithmetic against the baselines. **The deliverable is that a content-type collision is either impossible or explicit — never silent.**
