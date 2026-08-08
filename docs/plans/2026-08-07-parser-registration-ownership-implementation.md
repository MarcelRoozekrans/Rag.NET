# Parser Registration Ownership — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make content-type ownership declarable and enforced, so that two parsers claiming one type is either a loud error or an explicit, working override — never a silent one.

**Architecture:** `AddParser<T>(replaces:)` lands first as the vocabulary for a deliberate override; `UseQAPairsChunking()` adopts it; then parsers gain an opt-in way to enumerate their content types so `AddParser<T>()` declares claims itself, guarded by a convention test holding declaration and `CanParse` together.

**Tech Stack:** .NET 10, xUnit v3, Microsoft.Extensions.DependencyInjection.

**Design:** `docs/plans/2026-08-07-parser-registration-ownership-design.md`

---

## Context

`ParserClaim` exists so two parsers claiming one content type is a startup error. **`AddParser<T>()` cannot declare anything**, and `ParserClaim`'s own remarks say so: `CanParse` is a predicate, not an enumeration. Seven parsers register that way — Audio, Epub, Html, Office (x3), Pdf — and are invisible to the guard by design.

**One live collision:** `...spreadsheetml.sheet`, between `ExcelDocumentParser` (registered via `AddParser<T>()`, declares nothing) and `QAPairsDocumentParser` (declares it). The validator sees one claimant, says nothing, and selection order decides which parser actually runs.

**`CsvDocumentParser` is not registered by default** — no `[Singleton]` attribute, no extension registers it. Its `text/csv` overlap with QA-pairs is conditional on a user calling `AddParser<CsvDocumentParser>()` themselves.

**Vision is the one genuine oversight:** it registers two parsers through `AddSingleton<IDocumentParser>` — the same mechanism Archive, Email and Templates use *with* claims — and declares none.

**An earlier version of this plan claimed 11 undeclared parsers and two live collisions.** Both were overstated; see the design's SS1.1 for what was wrong and why. Do not restore those figures.

## The ordering is load-bearing — do not reorder these tasks

Closing the gap before QA-pairs can declare an override turns `UseQAPairsChunking()` into a startup error for anyone also using `Rag.NET.Parsers.Office`.

```
Task 1 (API)  ->  Task 2 (QAPairs adopts it)  ->  Task 3 (parsers enumerate)
```

Task 3 before Task 2 breaks the suite. **If you find yourself doing Task 3 first, stop.**

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

## Task 3: Let parsers enumerate their content types

**Files:**
- Create: an opt-in declaration alongside `IDocumentParser` in `src/Rag.NET.Abstractions/Abstractions/`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs` — `AddParser<TParser>` declares claims from it
- Modify: the parsers that can enumerate — Audio, Epub, Html, Office (x3), Pdf, Vision (x2)
- Test: `tests/Rag.NET.Tests/DependencyInjection/ParserClaimValidationTests.cs`

**Read the design's SS1.1 and SS4a before starting.** An earlier version of this plan asked you to
hand-declare claims at 11 registration sites. That was wrong: seven of those go through
`AddParser<T>()`, which `ParserClaim`'s own remarks document as structurally unable to declare
anything, so hand-declaring would leave the mechanism blind for every parser added later.

**The point is that `AddParser<T>()` stops being blind.**

Add an **opt-in, additive** declaration — a small interface a parser may implement alongside
`IDocumentParser`, exposing the content types it accepts. Then `AddParser<TParser>()` checks for
it and declares one `ParserClaim` per type.

Non-negotiables:

- **`IDocumentParser` does not change.** `CanParse` stays the predicate the pipeline calls. A
  parser that cannot enumerate its types simply does not opt in, and keeps today's behaviour and
  today's documented invisibility.
- **Nothing existing breaks.** Third-party parsers that implement only `IDocumentParser` must
  continue to work untouched.
- **`AddParser<T>(replaces:)` from Task 1 composes with it** — declaring types and replacing
  another parser must work together.

Adopt it in the nine parsers listed above. **Vision is the one that matters most**: it registers
via `AddSingleton<IDocumentParser>` like Archive/Email/Templates but declares no claims, which is
a genuine inconsistency rather than the documented gap. If Vision's registration path does not
route through `AddParser<T>()`, declare its claims explicitly there instead and **say so**.

Take the content types from each parser's `CanParse`/`SupportedTypes`. **If a parser's accepted
set cannot be enumerated honestly, leave it out and report which** - a wrong claim is worse than
no claim.

**Run the full suite after this task.** Anything that now throws at registration is a real
collision this phase should resolve; report it rather than working around it.

---

## Task 4: The convention test that holds declaration to behaviour

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/ParserClaimCoverageTests.cs`

Task 3's declaration can drift from `CanParse` - producing claims that do not match behaviour,
which is worse than no claims. **This test is the reason that risk is acceptable, so it is the
primary guard of this phase, not a nicety.**

Three assertions:

1. **Declaration implies behaviour** - every content type a parser enumerates is accepted by its
   own `CanParse`.
2. **Behaviour implies declaration** - for every parser that opts in, every type in
   `ContentTypeMap` its `CanParse` accepts is enumerated. `ContentTypeMap` is this library's own
   extension->MIME map, documented as covering "the content types handled by the Rag.NET parser
   packages" - it is not the "guessed list" `ParserClaim`'s remarks warn about.
3. **The octet-stream rule** - no parser claims `application/octet-stream`. `ContentTypeMap`'s
   remarks state the unknown-binary fallback assumes nothing claims it, and a parser that does is
   guessing.

**Instantiating parsers may be the hard part** - several need an `IChatClient` or options. If
reflection-instantiation is impractical for some, **say so and describe what you did instead**. A
source-scanning variant is acceptable; silently skipping parsers is not, because a coverage test
with holes is precisely what this task exists to prevent.

**Watch it go red.** Change one parser's enumerated list to include a type its `CanParse` rejects,
confirm the test fails naming that parser, then revert. Report that you did this.

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
