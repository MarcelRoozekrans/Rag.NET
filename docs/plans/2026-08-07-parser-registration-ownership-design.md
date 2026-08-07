# Parser Registration Ownership — Design (Phase 4.2)

**Date:** 2026-08-07
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. What this phase is, after measurement moved it

Phase 4.2 arrived carrying five workstreams re-pointed into it by four earlier phases: parser
replacement, `message/rfc822` ownership, options homes, connector deferrals, and repo-wide XML
documentation. **Documentation and connectors are split out** — the first is large, mechanical and
blocked on its own scoping decision; the second shares nothing with the rest.

What remains is one coherent subject: **who owns a content type, and how that is declared.**

Measuring it moved the phase. The intended centrepiece was a convenience API for replacing a
built-in parser. The actual finding is that **the mechanism which exists to make parser collisions
loud is silent for three quarters of the parsers in this repository**, and the replacement API
turns out to be the vocabulary that mechanism is missing rather than a convenience on top of it.

## 1. The `ParserClaim` guard covers a quarter of what its documentation claims

`ParserClaim` exists so `AddRagNet` can detect two parsers claiming one content type *before
anything is resolved*. Its own XML documentation says a second package claiming a declared type
"is a startup error".

**Corrected 2026-08-07, during Task 1.** An earlier version of this section claimed 11 parsers
covering ~22 content types "declare nothing" and called that a coverage hole. That overstated it,
and the correction changes what this phase should build. What follows is the measured version;
§1.1 records what was wrong and why, because the wrong version was persuasive.

Parsers reach the container four different ways, and the claim declarations track the mechanism
almost exactly:

| Mechanism | Parsers | Declares claims? |
|---|---|---|
| `AddParser<T>()` | Audio, Epub, Html, Office (×3), Pdf | **No — by design, see below** |
| `AddSingleton<IDocumentParser>` + explicit claim | Archive, Email (×2), Templates (×2) | Yes |
| `AddSingleton<IDocumentParser>`, no claim | **Vision (×2)**, Pdf's OCR overload | **No — inconsistent** |
| `[Singleton]` attribute + explicit claim | core Text, Markdown | Yes |
| Not registered at all | core **Csv**, **Json** | n/a |

**The `AddParser<T>()` blindness is documented, not accidental.** `ParserClaim`'s own remarks say
so: *"What genuinely goes undetected is a parser registered through `AddParser<T>()`, which
declares nothing. `CanParse` is a predicate rather than an enumeration, so nothing can discover
what an arbitrary parser accepts without probing it against a guessed list of content types — a
worse mechanism than an undetected collision."* Seven of the eleven "undeclared" parsers are on
that path. They are not an oversight; they are the accepted limitation.

**One live collision, not two.** `CsvDocumentParser` is **not registered by default** — it carries
no `[Singleton]` attribute and no extension registers it, so a user must `AddParser<CsvDocumentParser>()`
explicitly. The `text/csv` collision with `QAPairsDocumentParser` is therefore conditional, not
universal. What *is* live for anyone using `Rag.NET.Parsers.Office` together with QA-pairs
chunking is `…spreadsheetml.sheet`: `ExcelDocumentParser` registers through `AddParser<T>()` and
declares nothing, so the validator sees one claimant and says nothing while selection order
decides which parser runs.

**The genuine oversight is Vision.** It registers two parsers through
`AddSingleton<IDocumentParser>` — the same mechanism Archive, Email and Templates use *with*
claims — and declares none. That is an inconsistency with its own peers rather than a documented
limitation, and it is the only part of the original "coverage hole" that survives measurement.

### 1.1 What the first version of this section got wrong

Recorded rather than quietly rewritten, because the wrong version was convincing and nearly
reached implementation.

- **"Two live silent collisions."** One. `CsvDocumentParser` is not registered by default; the
  claim that it "ships with core, registered for everyone" confused *shipping in the core package*
  with *being registered by `AddRagNet`*.
- **"11 parsers declare nothing, therefore the guard has a hole."** Seven of them are on the
  `AddParser<T>()` path that `ParserClaim` explicitly documents as undetectable. That remark was
  read during the investigation and counted as a gap anyway.
- **A third collision, `image/jpeg`, between the Vision parsers.** There is none. The string in
  `VideoDocumentParser` is the MIME type of an extracted *frame* handed to `DataContent`. It came
  from grepping whole files rather than `CanParse` bodies. *Grepping a file is not reading a
  method.*

The pattern in all three is the same: a count taken from text matching, then reasoned about as
though it had been read.

## 2. Why the replacement API has to come first

Closing the gap — by any route — **breaks QA-pairs chunking for anyone who also uses
`Rag.NET.Parsers.Office`.** Once `ExcelDocumentParser` declares `…spreadsheetml.sheet`, that pair
becomes a startup error, and the same follows for `text/csv` for anyone who has added
`CsvDocumentParser` explicitly.

And the collision is *legitimate*. A caller who asked for QA-pairs chunking genuinely wants
`QAPairsDocumentParser` to win for those types. **There is currently no way to express that.** The
claim model has one verdict — conflict — and no vocabulary for a deliberate override.

So the ordering inverts from the roadmap's framing:

```
replacement API  →  full claim coverage  →  collisions become expressible
```

not

```
full claim coverage  →  everything breaks  →  replacement API as a fix
```

The API is the missing half of the guard, not a convenience beside it.

## 3. `message/rfc822`: retire the duplicate. `text/csv`: do not.

Both are collisions involving `Rag.NET.Chunking.Templates`. They resolve **differently**, and the
reason is a coupling that is easy to miss.

**Email — retire it.** `EmailTemplateDocumentParser` duplicates `Rag.NET.Parsers.Email`'s strictly
more capable `EmailDocumentParser`, and `UseEmailChunking`'s own remarks already record that "the
chunking strategy is unaffected either way: it consumes `DocumentSection`s and does not care which
parser produced them." Deleting it removes the collision outright, retires the `registerParser`
escape hatch, and **drops MimeKit** from the package.

**QA-pairs — keep it.** `QAPairsChunkingStrategy` carries an explicit note: *"Reads the answer from
`DocumentSection.Heading` — internal contract with `QAPairsDocumentParser`."* The parser encodes
the answer into `Heading`; the strategy reads it back. They are a **matched pair**, and core's
`CsvDocumentParser` produces nothing of the sort. Retiring it would break the feature.

**This corrects an earlier version of this design**, which proposed retiring both on symmetry and
claimed all three heavyweight dependencies would drop. Only MimeKit drops. CsvHelper and ClosedXML
stay, because `QAPairsDocumentParser` genuinely needs them. **Phase 4.7's stopped Task 10 is
therefore partly completed, not finished** — recorded plainly rather than claimed.

The symmetry was appealing and wrong. One duplicate is redundant; the other is half of a contract.

## 4. Options homes

`CostBudgetOptions.DatabasePath` is now a property that **does nothing when left alone and throws
when set**. The SQLite ledger moved to `Rag.NET.Storage.Sqlite`, whose `UseSqliteCostLedger(path)`
takes the path directly; the property survives only to convert a silent downgrade — a budget
quietly enforced against an in-memory ledger — into a loud failure.

That was the right fix at the time. It leaves a public property that cannot be used for its
apparent purpose, still carrying a default that can be assigned to no effect. Retiring it is this
phase's call because nothing is published: the loud error can be deleted along with the property,
since after removal the compiler is the error.

## 4a. Closing the gap structurally, not site by site

**Decided 2026-08-07 after §1.1.** Hand-declaring claims at each registration site was the original
plan and is the wrong shape: it leaves `AddParser<T>()` permanently blind, so every parser added
later — by this repository or by a consumer — reopens the gap, and a convention test could only
ever cover parsers that live here.

Instead, **let a parser enumerate the content types it accepts**, and have `AddParser<T>()`
declare claims from that automatically.

`ParserClaim`'s objection was to probing `CanParse` against *a guessed list*. A parser stating its
own types is not a guess. `CanParse` stays exactly as it is — the predicate remains the thing the
pipeline calls, and a parser that wants to accept types it cannot enumerate (a wildcard, a
computed set) simply does not opt in and keeps today's behaviour.

The shape is deliberately opt-in and additive:

- an optional declaration a parser can carry, alongside `IDocumentParser` rather than inside it,
  so no existing implementation breaks and no third-party parser is forced to change
- `AddParser<T>()` checks for it and declares a `ParserClaim` per type
- parsers that do not carry it behave exactly as they do today, and remain the documented
  undetected case

**The risk this introduces, stated rather than discovered later:** the enumerated list and
`CanParse` can drift apart, which would produce claims that do not match behaviour — a guard
saying the wrong thing, which is worse than one saying nothing. §5's convention test exists to
hold them together, and that is its primary job rather than an incidental one.

## 5. Testing

- **A test that every enumerated content type is actually accepted by `CanParse`, and that every
  type in `ContentTypeMap` accepted by `CanParse` is enumerated.** Both directions. This is what
  stops §4a's declaration drifting from behaviour, and it is the primary guard of this phase
  rather than a nicety. It must be watched go red — a guard nobody has seen fail is not a guard,
  and this repository has shipped three of those.
- **A test that the Vision parsers declare claims**, since they are the one genuine oversight §1
  found and nothing else would notice them regressing.
- **A test that a deliberate override registers cleanly and a genuine collision still throws** —
  the replacement API must not become a way to silence the guard entirely.
- The existing `ParserClaimValidationTests` keep their coverage, including
  `TwoParsersSharingAShortName_StillConflict`, whose history is recorded on `ParserClaim` itself.

## 6. Breaking changes, stated up front

- `UseEmailChunking()` no longer registers a parser; `.eml` flows need `Rag.NET.Parsers.Email`.
- Its `registerParser` parameter is removed — it existed only for the collision being deleted.
- `CostBudgetOptions.DatabasePath` is removed.

Nothing is published, so the cost is documentation rather than migration — the same window that
made Phases 4.7 and 4.8 cheap, and it closes at 6.3.

## 7. Out of scope

- **Repo-wide XML documentation.** Split out; it needs its own scoping decision about types that
  are `public` only for cross-package access.
- **Connector deferrals** — webhook payload parsers, cron/NCrontab schedules, field selections.
- **Retiring `QAPairsDocumentParser`.** §3 — it is not a duplicate.
- **Narrowing `ImageDocumentParser`'s claim set.** It claims six `image/*` types and collides with
  nothing; there is no problem to solve.
