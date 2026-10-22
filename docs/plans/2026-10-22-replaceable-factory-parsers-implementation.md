# Replaceable factory-registered parsers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `replaces:` reach parsers registered through a factory, migrate every in-repo factory parser onto the new mechanism, and give the Azure Document Intelligence parser a `ReplaceExistingParsers` option, all without a breaking change.

**Architecture:** A public `ParserRegistration` marker record stored in the service collection names the parser type a factory builds. An `AddParserFactory<TParser>` extension on `IRagBuilder` writes the factory and the marker together. Removal logic moves from `RagBuilder` into an internal Abstractions helper that matches by implementation type or marker, and throws when claims exist but no registration can be found. The Azure parser finds the parsers to replace from existing `ParserClaim`s and removes them through the same helper.

**Tech Stack:** .NET 10, Microsoft.Extensions.DependencyInjection, xUnit v3, ApiCompat.

**Spec:** `docs/plans/2026-10-22-replaceable-factory-parsers-design.md`

## Global Constraints

- Not a breaking change: **no member is added to `IRagBuilder`, and no overload is added to `AddParser<T>`**. ApiCompat strict, run by `pack-validate`, must report no breaking change.
- The new entry point is named exactly `AddParserFactory<TParser>`, an extension method on `IRagBuilder` in `Rag.NET.Abstractions`.
- A replaced name that matches **nothing** stays a silent no-op. A name that matches **claims but no registration** throws `InvalidOperationException`.
- Azure option: `AzureDocumentIntelligenceParserOptions.ReplaceExistingParsers`, `bool`, default `false`.
- Content-type comparison when Azure looks up claims is `StringComparer.OrdinalIgnoreCase`, the same as `ValidateParserClaims`.
- Build with 0 warnings; the repo treats warnings as errors.
- Commits: conventional commits, headers at most 100 characters (commitlint), **no nested parentheses in commit bodies**, author `Marcel Roozekrans <marcel@roozekrans.nl>`, ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never write a `claude.ai/code/session_` URL anywhere.
- `dotnet test --filter` fails with RAGNET0001 here. Focused runs: `dotnet build <proj> -v q && <proj>/bin/Debug/net10.0/<ProjectName> -method "*<TestName>*"`.

## Review Focus

1. **Existing `replaces:` callers.** `Rag.NET.Chunking.Templates` and `Rag.NET.Evaluation` replace parsers registered by type. The new "claims but no registration" throw must not fire for them. Task 1 runs their test projects.
2. **A parser registered twice**, for example two `AddPdfParser(configure)` calls. Replacing it removes both registrations and both markers. Task 2 pins this for PDF.
3. **Vision's forwarding registration.** Vision registers a concrete `ImageDocumentParser` singleton and forwards `IDocumentParser` to it. After replacement, `GetServices<IDocumentParser>()` must not contain an `ImageDocumentParser`. Task 2 asserts this on a built provider, not on descriptors.
4. **Case in content types.** A claim declared as `IMAGE/TIFF` is still found by Azure's lookup. Task 3 pins it.
5. **`ReplaceExistingParsers = true` with nothing to replace** registers normally and throws nothing. Task 3 pins it.

---

### Task 1: Core — `ParserRegistration`, `AddParserFactory`, shared removal

**Files:**
- Create: `src/Rag.NET.Abstractions/ParserRegistration.cs`, the public marker record
- Create: `src/Rag.NET.Abstractions/ParserFactoryRegistrationExtensions.cs`, the public `AddParserFactory`
- Create: `src/Rag.NET.Abstractions/ParserReplacement.cs`, the internal removal helper
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`. `RemoveReplacedParser` delegates to the helper; its remarks drop the "Recorded as debt" paragraph.
- Modify: `src/Rag.NET.Abstractions/IRagBuilder.cs`. The doc comments only: point to `AddParserFactory` for factory parsers.
- Test: `tests/Rag.NET.Tests/DependencyInjection/ParserFactoryReplacementTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ParserRegistration(string ParserTypeName, ServiceDescriptor Descriptor)`, plus `public static ParserRegistration For<TParser>(ServiceDescriptor descriptor)`, which uses `typeof(TParser).FullName`.
  - `public static IRagBuilder AddParserFactory<TParser>(this IRagBuilder builder, Func<IServiceProvider, TParser> factory, Type? replaces = null, string[]? replacesTypeNames = null) where TParser : class, IDocumentParser`. It runs the replacements first, then adds one singleton `IDocumentParser` descriptor built from `factory`, then adds `ParserRegistration.For<TParser>(thatDescriptor)` as a singleton instance. **It declares no `ParserClaim`s**: each package keeps declaring its own, labelled with the call the user wrote, such as `AddPdfParser(configure)`.
  - `internal static class ParserReplacement { internal static void Remove(IServiceCollection services, string parserTypeName); }`. It removes `IDocumentParser` descriptors whose `ImplementationType.FullName` matches, every descriptor a matching `ParserRegistration` marks, the markers, and matching `ParserClaim`s. When it removed at least one claim but no `IDocumentParser` descriptor, it throws `InvalidOperationException` with the §1.2 message.

- [ ] **Step 1: Write the failing tests** in `ParserFactoryReplacementTests`:
  - `AddParserFactory_RegistersTheParserAndAMarker`: the collection has one `IDocumentParser` descriptor with a non-null `ImplementationFactory`, and one `ParserRegistration` whose `ParserTypeName == typeof(FakeParser).FullName` and whose `Descriptor` is that same descriptor instance.
  - `AddParser_WithReplaces_RemovesAFactoryRegisteredParser`: register `FakeParser` with `AddParserFactory` plus a `ParserClaim.For<FakeParser>("x/y", "AddFake()")`, then `AddParser<OtherParser>(replaces: typeof(FakeParser))`. No `IDocumentParser` descriptor builds a `FakeParser`, and no marker or claim names it.
  - `AddParser_WithReplacesTypeNames_RemovesAFactoryRegisteredParser`: the same, by name.
  - `AddParserFactory_WithReplaces_MakesTheReplacementWinSelection`: on a built provider, `GetServices<IDocumentParser>()` contains the replacement and no `FakeParser`.
  - `Replace_UnmarkedFactoryParserWithClaims_Throws`: a raw `Services.AddSingleton<IDocumentParser>(_ => new FakeParser())` plus a claim, then `AddParser<OtherParser>(replaces: typeof(FakeParser))`. It throws `InvalidOperationException` whose message contains `typeof(FakeParser).FullName` and `AddParserFactory`.
  - `Replace_NameMatchingNothing_IsANoOp`: `replacesTypeNames: ["Not.Installed.Parser"]` throws nothing and leaves the descriptor count unchanged apart from the new parser.
- [ ] **Step 2: Run them and see them fail.** Run `dotnet build tests/Rag.NET.Tests -v q && tests/Rag.NET.Tests/bin/Debug/net10.0/Rag.NET.Tests -method "*ParserFactoryReplacementTests*"`. Expected: a compile failure, because `AddParserFactory` and `ParserRegistration` do not exist.
- [ ] **Step 3: Implement the three files and the `RagBuilder` delegation**, matching the Interfaces block. The throw message names the parser's full type name and says its package must register it through `AddParserFactory`, because a factory registration does not reveal the type it builds.
- [ ] **Step 4: Run them and see them pass.** Then run the existing tests, which must still pass unchanged: `ParserClaimValidationTests` in `Rag.NET.Tests`, the whole `Rag.NET.Chunking.Templates.Tests` project and the whole `Rag.NET.Evaluation.Tests` project.
- [ ] **Step 5: Commit** with `feat(core): let replaces: reach factory-registered parsers through AddParserFactory`.

### Task 2: Migrate the factory parsers

**Files:**
- Modify: `src/Rag.NET.Parsers.Pdf/PdfParserBuilderExtensions.cs`, the `configure` overload
- Modify: `src/Rag.NET.Parsers.Vision/RagBuilderExtensions.cs`. Both forwarding `IDocumentParser` registrations become `builder.AddParserFactory(sp => sp.GetRequiredService<ImageDocumentParser>())`, and the same for video.
- Modify: `src/Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs`, both parsers
- Modify: `src/Rag.NET.Parsers.Archive/ArchiveParserBuilderExtensions.cs`
- Modify: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceParserBuilderExtensions.cs`, `Register`
- Test: one new test per package's existing test project, each named `<Registration>_IsReplaceableByType`

**Interfaces:**
- Consumes: `AddParserFactory<TParser>` from Task 1. Each call site keeps its existing hand-declared `ParserClaim`s and its `RegistrationMethod` labels unchanged.

- [ ] **Step 1: Write the failing tests.** In each package's test project, register the package's parser, then `AddParser<StubParser>(replaces: typeof(<Parser>))`. Build the provider and assert that `GetServices<IDocumentParser>()` contains no `<Parser>` and does contain the stub:
  - PDF: `AddPdfParser(configure)` **called twice**, so both registrations go.
  - Vision: `UseImageDescription` with a fake chat client.
  - Email: replace `EmailDocumentParser` only; `MsgDocumentParser` stays.
  - Archive: replace `ZipDocumentParser`.
  - Azure: replace `AzureDocumentIntelligenceDocumentParser`.
- [ ] **Step 2: Run them and see them fail.** Today `replaces:` removes the claims but leaves the factory parser in the provider.
- [ ] **Step 3: Replace each `builder.Services.AddSingleton<IDocumentParser>(sp => …)` with `builder.AddParserFactory(sp => …)`.** Drop any remark that says the registration "bypasses" replacement.
- [ ] **Step 4: Run every test in the five packages' test projects.** They pass, including the existing ones, unedited.
- [ ] **Step 5: Commit** with `refactor(parsers): register factory parsers through AddParserFactory so replaces: reaches them`.

### Task 3: Azure `ReplaceExistingParsers`

**Files:**
- Modify: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceParserOptions.cs`, the new property and its XML doc
- Modify: `src/Rag.NET.Parsers.AzureDocumentIntelligence/AzureDocumentIntelligenceParserBuilderExtensions.cs`, `Register`
- Test: `tests/Rag.NET.Parsers.AzureDocumentIntelligence.Tests/ReplaceExistingParsersTests.cs`

**Interfaces:**
- Consumes: `AddParserFactory<TParser>(factory, replacesTypeNames:)` from Task 1, and the Task 2 version of `Register`.
- Produces: `public bool ReplaceExistingParsers { get; set; }` on `AzureDocumentIntelligenceParserOptions`.

The algorithm in `Register`, when `ReplaceExistingParsers` is true and before anything is added:
1. Let `ours` be `options.Formats.ContentTypes()`, compared `OrdinalIgnoreCase`.
2. Collect the `ParserClaim` instances in `builder.Services` whose `ContentType` is in `ours` and whose `ParserTypeName` is not `AzureDocumentIntelligenceDocumentParser`'s full name. The distinct `ParserTypeName`s are the parsers to replace.
3. For each of those parsers, if any of its claims has a `ContentType` not in `ours`, throw `InvalidOperationException`. The message names the parser, every such content type, and says that replacing would leave those types without a parser. Nothing has been added at this point.
4. Pass the names as `replacesTypeNames` to `AddParserFactory`. When exactly one parser was replaced, Azure's declared claims carry `ReplacesParserTypeName` set to it.

- [ ] **Step 1: Write the failing tests:**
  - `Word_ReplacesAddWordParser`: `AddWordParser()`, then Azure with `Formats = Word` and `ReplaceExistingParsers = true`. `AddRagNet` succeeds. The provider's parsers contain no `WordDocumentParser`, and the only claim for the DOCX content type is Azure's, with `ReplacesParserTypeName == typeof(WordDocumentParser).FullName`.
  - `Pdf_ReplacesFactoryRegisteredPdfParser`: the same with `AddPdfParser(o => { })` and `Formats = Pdf`.
  - `Images_WithVision_ThrowsNamingTheOrphanedTypes`: `UseImageDescription(...)`, then Azure with `Formats = Images` and replace on. It throws `InvalidOperationException` containing `image/webp` and `image/gif`, and the service collection's count is unchanged by the failed call.
  - `ParserRegisteredAfterAzure_StillConflicts`: Azure with `Formats = Word` and replace on, then `AddWordParser()`. `AddRagNet` throws the existing claim-conflict error.
  - `Default_DoesNotReplace`: `AddWordParser()` then Azure with `Formats = Word`, flag left at default. `AddRagNet` throws the claim-conflict error.
  - `ReplaceWithNothingToReplace_RegistersNormally`: Review Focus 5.
  - `ClaimCaseIsIgnored`: a stub parser claimed as `IMAGE/TIFF` is replaced by the default formats. Review Focus 4.
- [ ] **Step 2: Run them and see them fail**: a compile failure on `ReplaceExistingParsers`.
- [ ] **Step 3: Implement the property and the algorithm above.**
- [ ] **Step 4: Run the whole Azure test project.** All tests pass, including the existing 58.
- [ ] **Step 5: Commit** with `feat(parsers): let the Azure parser take formats over with ReplaceExistingParsers`.

### Task 4: Documentation and bookkeeping

**Files:**
- Modify: `src/Rag.NET.Parsers.AzureDocumentIntelligence/README.md`
- Modify: `docs/guide/ingestion.md`: the Azure parser section and the claim-model section
- Modify: `docs/planning/ROADMAP.md`: mark the debt entry at line 500 resolved, linking this design, and correct its stale `Chunking.Templates` mention
- Modify: `docs/plans/2026-10-22-replaceable-factory-parsers-design.md`: an amendment to §1 noting that `AddParserFactory` declares no claims, with the reason from Task 1's Interfaces block
- Modify: `docs/reference/features.md`, if it lists parser-replacement capabilities. Check first; edit only an existing row.

**Interfaces:**
- Consumes: the names from Tasks 1 and 3, verbatim.

- [ ] **Step 1: README and guide, Azure section.** Add "Replacing the default formats", with `o.Formats = AzureDocumentIntelligenceFormats.Word;` and a sentence that `=` drops TIFF and HEIF while `|=` keeps them. Add "Taking a format over from another parser", covering both routes: remove the other `Add…Parser()` call, or keep it and set `ReplaceExistingParsers = true`. Under that, cover the register-Azure-last rule, the partial-overlap error with the Vision `image/webp`/`image/gif` example, and that bundled services such as Vision's chunking strategy stay registered.
- [ ] **Step 2: Guide, claim-model section.** Add a paragraph for package authors on `AddParserFactory<TParser>`: why a factory parser needs it, and that replacing one registered without it now throws instead of silently doing nothing.
- [ ] **Step 3: ROADMAP and design amendment**, as listed under Files.
- [ ] **Step 4: Verify.** Run the `Rag.NET.RepoConventions.Tests` project. Then pack per `CONTRIBUTING.md` (`rm -rf artifacts/packages; dotnet tool restore; V=$(dotnet dotnet-gitversion /output json /showvariable SemVer); dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$V"`) and run the `Rag.NET.PackageValidation.Tests` exe. Expected: all green, and the docs-fence check resolves every new C# name.
- [ ] **Step 5: Commit** with `docs(parsers): document format replacement and AddParserFactory`.
