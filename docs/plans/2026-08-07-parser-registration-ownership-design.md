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

| | Parsers | Content types |
|---|---:|---:|
| Declare claims | 6 | 8 |
| **Declare nothing** | **11** | **~22** |

Undeclared: `CsvDocumentParser` and `JsonDocumentParser` (**core**), Audio, Epub, Html,
Office (×3), Pdf, Vision (×2).

**Two live collisions, neither detected:**

| Content type | Claimants | Why it is silent |
|---|---|---|
| `text/csv` | `CsvDocumentParser` (**core**) + `QAPairsDocumentParser` | core declares nothing |
| `…spreadsheetml.sheet` | `ExcelDocumentParser` (Office) + `QAPairsDocumentParser` | Office declares nothing |

Neither errors. The pipeline takes the first parser whose `CanParse` accepts, and built-in claims
are registered before the user's `configure` delegate runs — so **`UseQAPairsChunking()` most
likely registers a CSV parser that never executes**, and nothing says so.

This is this milestone's recurring defect exactly: the mechanism, its tests and its documentation
agree with one another, and the coverage is not there.

**A correction worth recording.** A first pass reported a third collision, `image/jpeg` between
`ImageDocumentParser` and `VideoDocumentParser`. There is none: the string in `VideoDocumentParser`
is the MIME type of an extracted *frame* handed to `DataContent`, not a `CanParse` claim. It came
from grepping whole files rather than `CanParse` bodies. *Grepping a file is not reading a
method.*

## 2. Why the replacement API has to come first

The obvious fix — declare the missing claims — **breaks QA-pairs chunking for every user who
enables it.** `CsvDocumentParser` is a built-in, registered for everyone. Once both sides declare
`text/csv`, `UseQAPairsChunking()` becomes a guaranteed startup error.

And the collision is *legitimate*. A caller who asked for QA-pairs chunking genuinely wants
`QAPairsDocumentParser` to win for `text/csv`. **There is currently no way to express that.** The
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

## 5. Testing

- **A `RepoConventions` test asserting every `IDocumentParser` implementation declares a claim for
  every content type its `CanParse` accepts.** This is the guard that stops the coverage rotting
  again, and it is the only reason to believe §1 will not recur. It must be watched go red — a
  guard nobody has seen fail is not a guard, and this repository has shipped three of those.
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
