# Replaceable factory-registered parsers, and Azure's `ReplaceExistingParsers` — Design

**Date:** 2026-10-22
**Status:** proposed (awaiting approval)
**Origin:** a follow-up to [#824](https://github.com/MarcelRoozekrans/Rag.NET/issues/824) / PR #839. The
Azure Document Intelligence parser can claim formats a local parser already claims, and the only
answer the docs give is "register one of them". The user asked for a way to override the existing
parser, and for that to rest on fixing the core gap rather than working around it.
**Closes:** the debt recorded at `docs/planning/ROADMAP.md:500` (Phase 4.2, 2026-08-08):
"`AddParser<T>(replaces:, replacesTypeNames:)` cannot replace a factory-registered parser".

## 0. The gap, measured

`RagBuilder.RemoveReplacedParser` finds the parser to remove by
`ServiceDescriptor.ImplementationType`. That is `null` for every factory registration
(`AddSingleton<IDocumentParser>(sp => …)`), so naming such a parser in `replaces:` removes its
**claims** — they are instances carrying `ParserTypeName` — but not its **registration**. The
conflict check goes quiet, the old parser stays in the selection list, and whichever was registered
first keeps winning. That is a silent wrong answer, the exact shape Phase 4.2 set out to remove.

Factory-registered parsers on `main` today, by reading every `AddSingleton<IDocumentParser>(` in `src/`:

| Package | Registration | Parsers |
|---|---|---|
| `Rag.NET.Parsers.Pdf` | `AddPdfParser(configure)` | `PdfDocumentParser` (the no-argument overload registers by type) |
| `Rag.NET.Parsers.Vision` | `UseImageDescription`, `UseVideoDescription` | `ImageDocumentParser`, `VideoDocumentParser` |
| `Rag.NET.Parsers.Email` | `AddEmailParser` | `EmailDocumentParser`, `MsgDocumentParser` |
| `Rag.NET.Parsers.Archive` | `AddArchiveParser` | `ZipDocumentParser` |
| `Rag.NET.Parsers.AzureDocumentIntelligence` | `AddAzureDocumentIntelligenceParser` | `AzureDocumentIntelligenceDocumentParser` |

The ROADMAP entry also lists `Rag.NET.Chunking.Templates`; it now registers through
`AddParser<QAPairsDocumentParser>(replacesTypeNames:)`, by type, and is no longer affected. The
ROADMAP line is corrected as part of this change.

*Amended 2026-10-22: the table missed two parsers, because their registrations are not in the
source. `Rag.NET`'s built-ins, `MarkdownDocumentParser` and `TextDocumentParser`, carried
`[Singleton(As = typeof IDocumentParser, AllowMultiple = true)]`, and ZeroAlloc.Inject's generated
`AddRagNETServices()` registered them as factories, `sp => new TextDocumentParser()`, with no
marker. Their claims were declared by hand in `AddRagNet`, so once §1.2 made an unmarked factory
with claims throw, `AddParser<X>(replaces: typeof(TextDocumentParser))`, the call
`docs/guide/extending.md` documents as the supported way to override a built-in, threw too. The
fix is at the source: the attribute is gone from both parsers, and `AddRagNet` registers them by
type, next to their claims, in the generator's order and before `configure`. A generated
registration is always a factory, so no `IDocumentParser` is left to the generator.*

## 1. Core: a removal key that a factory registration supplies

A factory cannot say what type it will build without being run, so the type has to be stated at
registration. The key lives **in the service collection**, not in `RagBuilder`, because extension
packages see only `IRagBuilder.Services`.

- **`ParserRegistration`** (public sealed record, `Rag.NET.Abstractions`): `ParserTypeName`
  (full type name) and `Descriptor` (the `ServiceDescriptor` it marks), registered as a singleton
  instance next to the descriptor. It is never resolved; it exists to be found.
- **`IRagBuilder.AddParserFactory<TParser>(Func<IServiceProvider, TParser> factory, Type? replaces = null, string[]? replacesTypeNames = null)`**,
  an **extension method** in `Rag.NET.Abstractions`. It registers the factory as an
  `IDocumentParser` singleton, adds the `ParserRegistration` marker, declares claims for
  `IDeclaresContentTypes` parsers like `AddParser<T>` does, and supports `replaces:` the same way.
  *Amended 2026-10-22: `AddParserFactory` declares no claims. A generic helper cannot know the
  user-facing label a claim carries, such as `AddPdfParser(configure)`, and declaring claims would
  duplicate the ones each package already declares. The helper registers the parser and its marker
  only, and `replaces:` and `replacesTypeNames:` behave as described.*
- **Removal moves to `Rag.NET.Abstractions`** as an internal helper, already visible to `Rag.NET`
  through `InternalsVisibleTo`. Both `AddParser<T>` and `AddParserFactory<T>` call it. It removes, for
  a type name: descriptors whose `ImplementationType` matches, descriptors a `ParserRegistration`
  marks, the markers themselves, and the claims.
  *Amended 2026-10-22: two behaviours this section did not specify. The helper validates every
  replaced name before it removes anything, so a throw for one name leaves the collection untouched
  rather than half-edited. And it also matches a parser registered as an instance
  (`ImplementationInstance`), not only by type or marker, so `AddSingleton<IDocumentParser>(new X())`
  is replaceable too.*

### 1.1 Why this is not a breaking change

- **No member is added to `IRagBuilder`.** Adding one would break every implementer outside this
  repository. The new entry point is an extension method instead.
- **No overload of `AddParser<T>` is added.** A factory overload would make an existing
  `AddParser<X>(null)` call ambiguous, because `null` converts to both `Type` and `Func<…>`. The
  distinct name `AddParserFactory` avoids that.
- **Migrated registrations resolve identically.** Each package's
  `AddSingleton<IDocumentParser>(sp => …)` becomes `AddParserFactory<T>(sp => …)`; the same factory
  runs, with the same lifetime and order. The only visible difference is one extra
  `ParserRegistration` instance per parser in the collection.
- **The behaviour change fixes a bug.** `replaces: typeof(ImageDocumentParser)` used to remove
  Vision's claims and leave Vision selectable; now it removes Vision. No configuration that works
  correctly today depends on the old result.
- **ApiCompat strict** runs over every package and is the proof; the release is a `feat:` minor.

### 1.2 Unmarked factories become loud

A third-party package can still call `AddSingleton<IDocumentParser>(sp => …)` without the marker.
Today, replacing one is silent. After this change, when the replaced name matches **claims but no
registration**, the replace throws `InvalidOperationException`: the parser declared what it
claims, so it is installed, but its registration cannot be found. The message names the parser
and says its package must register through `AddParserFactory`. A name that matches nothing at all
is still a no-op, which is what an optional package that was never installed needs.

*Amended 2026-10-22: "no registration that can be found" covers a registration by type, by
`AddParserFactory` marker and by instance. What is left is any raw
`AddSingleton<IDocumentParser>(sp => ...)` whose parser has claims, and that includes registrations
a source generator writes, since generated output is always a factory. When this was first
written, the built-in Text and Markdown parsers were exactly that, and replacing them threw; §0's
amendment records the fix. No registration in `src/`, generated output included, is an unmarked
factory now, so in practice only a third-party one remains.*

## 2. Azure: `ReplaceExistingParsers`

`AzureDocumentIntelligenceParserOptions.ReplaceExistingParsers` (bool, default `false`):

```csharp
services.AddRagNet(rag => rag
    .AddWordParser()                                   // registered first
    .AddAzureDocumentIntelligenceParser(endpoint, credential, o =>
    {
        o.Formats = AzureDocumentIntelligenceFormats.Word;  // replace the TIFF/HEIF default
        o.ReplaceExistingParsers = true;                     // take .docx over from AddWordParser
    }));
```

- **Driven by claims, not a hard-coded list.** For each content type the enabled `Formats` claim,
  the registration looks up existing `ParserClaim`s; their `ParserTypeName`s are the parsers to
  replace. Azure never names `WordDocumentParser`, so it needs no reference to the Office package,
  and a third-party parser that declares claims is covered too.
- **Partial overlap throws.** Replacing removes a whole parser. If a replaced parser also claims
  types Azure will not take — Vision's image parser claims `image/webp` and `image/gif`; `Images`
  covers only PNG, JPEG and BMP — the registration throws `InvalidOperationException`, naming the
  types that would lose their parser. Removing them silently is the failure this design exists to
  prevent.
  *Amended 2026-10-22: the error names every parser that would be orphaned, each with its sorted
  leftover types, not just the first, so one run shows everything to fix. It is thrown before
  anything is added. This also means `Images` with `UseImageDescription()` always throws: Vision
  claims `image/gif` and `image/webp`, which Azure never emits, so no `Formats` value takes PNG,
  JPEG or BMP over from it.*
- **Order matters, and the failure is loud.** Only parsers registered **before** Azure can be
  replaced. One registered after it still collides at `AddRagNet`'s claim check, as it does today.
  The docs say to register Azure last.
- **Bundled services stay.** Replacing removes the parser only, as `AddParser(replaces:)` does.
  `UseImageDescription`'s chunking strategy, for example, stays registered. The docs say so.
- **The claims record the override.** The claims Azure declares carry `ReplacesParserTypeName`
  when exactly one parser was replaced, matching `AddParser`'s rule.
- **Default `false`.** Without it, overlap is still the startup error it is today.

## 3. Documentation

Both the package README and the guide's "Azure Document Intelligence as a parser" section gain:

1. **Replacing the defaults** — `o.Formats = …` instead of `|=`, so TIFF and HEIF can be dropped.
2. **Taking a format over** — the two routes: remove the other `Add…Parser()` call, or keep it and
   set `ReplaceExistingParsers`, with the order rule and the partial-overlap error.

The claim-model section of `docs/guide/ingestion.md` documents `AddParserFactory<T>` for package
authors, and states that a factory parser registered without it cannot be replaced, and now fails
loudly when someone tries.

## 4. Testing

- **Core**, for both `replaces:` and `replacesTypeNames:`: replacing a factory-registered parser
  removes its registration, marker and claims; the replacement wins selection in a built provider.
  An unmarked factory parser with claims throws the §1.2 error. A name matching nothing is still a
  no-op. `AddParserFactory<T>` declares `IDeclaresContentTypes` claims like `AddParser<T>`.
  *Amended 2026-10-22: per §1's amendment, `AddParserFactory<T>` declares no claims, so the test
  shows it registers the parser and its marker only. It also rejects an interface or abstract
  `TParser`, since the marker must name the concrete type the claims name. And the built-in
  `TextDocumentParser` and `MarkdownDocumentParser` are each replaced through
  `AddParser<T>(replaces:)` on a built provider, per §0's amendment.*
- **Per migrated package**: the existing registration tests pass unchanged, and one test per
  package replaces its parser through `replaces:` and shows the replacement selected.
- **Azure**: `Word` replaces `AddWordParser()`, a type registration. `Pdf` replaces
  `AddPdfParser(configure)`, a factory registration. `Images` with `UseImageDescription()` throws
  and names `image/webp` and `image/gif`. A parser registered after Azure still collides. The
  default `false` still collides.
- **ApiCompat strict** shows no breaking change in any package.

## Out of scope

- **Replacing only some of a parser's types.** A parser keeping WebP and GIF while Azure takes PNG
  would need per-type selection, which the "first `CanParse` wins" pipeline does not have. That
  would be its own design.
- **Changing `AddParser<T>`'s signature** or adding members to `IRagBuilder`.
