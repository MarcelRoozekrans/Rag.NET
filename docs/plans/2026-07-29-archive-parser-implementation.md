# Archive Parser (ZIP) Implementation Plan (Phase 3.10)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Parse `.zip` archives by dispatching each entry to the registered parser for its content type, bounded against decompression bombs and sharing one depth/entry budget with the email parsers.

**Architecture:** Part A promotes four `internal` types out of `Rag.NET.Parsers.Email` into `Rag.NET.Abstractions` as container-neutral machinery, with zero behaviour change. Part B builds `Rag.NET.Parsers.Archive` on top of them.

**Tech Stack:** .NET 10, `System.IO.Compression` (BCL — no new third-party dependency), xUnit v3.

**Design:** `docs/plans/2026-07-29-archive-parser-design.md`. Read it first — §0 explains why this is a promotion rather than a reuse, §3 why the caps cannot be read from the archive, and §4 why the roadmap's path-traversal framing is corrected rather than inherited.

---

## Conventions

- Warnings are errors: MA0051 (**≤60-line methods — the parser's `ParseAsync` will press on this**), MA0015, MA0048 (one public type per file, name matches file), MA0006 (`string.Equals` not `==`), MA0008 (`[StructLayout(LayoutKind.Auto)]` on public structs), MA0009, MA0132, MA0140, ZA0601 (no `GroupBy`/`OrderBy`/`ToList` in a loop), ZA0501, EPS05/EPS06, EPC12/EPC13 (**a `catch` reading only `ex.Message` is an error**), HLQ001/HLQ004/HLQ012 (no `foreach` over `List<T>`)/HLQ013, NU1510. **No new `#pragma` or `SuppressMessage`.**
- All logging through `LoggerMessage` source-gen. The email package has `EmailParserLog.cs`; the archive package needs its own.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One commit per task.
- **Never `git add -A` or `git add .`** — explicit paths only. `.claude/worktrees/` is untracked; leave it.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.

Baselines: `Rag.NET.Parsers.Email.Tests` **76**, `Rag.NET.Tests` **1325**, `Rag.NET.Chunking.Templates.Tests` **51**, `Rag.NET.RepoConventions.Tests` **9**.

**Two repo conventions will fail loudly if you forget them.** `Rag.NET.RepoConventions.Tests` asserts `EverySourceProjectIsInTheSolution` and `EveryTestProjectIsInTheSolution`, so both new projects must be added to `Rag.NET.slnx` (`<Project Path="..." />` entries, in the `/src/` and `/tests/` folder blocks). It also checks tier declarations in both directions — the archive tests need no Docker and no model, so declare neither.

**Timestamp trap:** restoring a file from git can preserve an mtime that makes MSBuild skip recompiling, so `--no-build` then tests a stale binary. Build without `--no-build` and confirm from the log that the project recompiled. This has produced a false result in this repository before.

---

# Part A — promote the container machinery

## Task 1: move all four types at once

**Files:**
- Create: `src/Rag.NET.Abstractions/Containers/ContainerContext.cs`, `ContainerBudget.cs`, `ContentTypeMap.cs`, `ContainerEntryDispatcher.cs`
- Delete: `src/Rag.NET.Parsers.Email/EmbeddedMessageContext.cs`, `EmbeddedMessageBudget.cs`, `MimeTypeMap.cs`, `EmailAttachmentDispatcher.cs`
- Modify: the 10 remaining files in `src/Rag.NET.Parsers.Email/` that reference them, and the 5 test files listed below

**This task cannot be split.** The four types are mutually referential — the context writes tags the dispatcher reads, the dispatcher builds the context's child metadata, `ContainerBudget` writes through `ContainerContext.BudgetTag` — so moving one at a time leaves a state that does not compile. It is one commit by nature.

| Now | Becomes |
|---|---|
| `EmbeddedMessageContext` | `ContainerContext`, tags `__rag_container_depth` / `__rag_container_budget` |
| `EmbeddedMessageBudget` | `ContainerBudget` |
| `MimeTypeMap` | `ContentTypeMap`, **public** |
| `EmailAttachmentDispatcher` | `ContainerEntryDispatcher` |

`ContainerContext`, `ContainerBudget` and `ContainerEntryDispatcher` stay `internal` to `Rag.NET.Abstractions` **only if** that assembly grants `InternalsVisibleTo` to both parser packages; check whether it already does and prefer `public` over adding a second grant, since Part B's package is a legitimate external consumer. `ContentTypeMap` becomes public either way — it is a useful lookup in its own right.

Files referencing these types (measured, not guessed): `EmailDocumentParser.cs`, `EmailParserLog.cs`, `EmailParserOptions.cs`, `EmbeddedMessageDescentPolicy.cs`, `EmbeddedMessageMetadata.cs`, `EmbeddedTraversal.cs`, `IDescentPolicy.cs`, `IMessageAdapter.cs`, `MsgDocumentParser.cs`, `StorageMessageAdapter.cs`, plus tests `EmbeddedMessageRecursionTests.cs`, `EmbeddedTraversalTests.cs`, `EmlFixtureBuilder.cs`, `ThrowingDocumentParser.cs` and `tests/Rag.NET.Chunking.Templates.Tests/EmailChunkingWithoutItsParserTests.cs`.

**Carry these two things across verbatim, because both are subtle and already documented:**

1. **The budget write-back.** Each `Consume()` writes the new value into the tag dictionary the dispatcher built for the child, so a parent recovers the count after enumeration. `EmbeddedMessageBudget`'s remarks say what happens without it: *"the cap would reset for every dispatched branch and the real bound would be `cap ^ depth` rather than `cap`"*. Keep that comment.
2. **The sink is supplied only below depth 0.** At depth 0 the tag dictionary belongs to the caller and reaches stored chunk metadata; writing to it was a real defect that the depth test fixed. Keep the guard and its comment.

**The acceptance criterion is zero behaviour change.** Run the full email suite and Templates suite. **Every existing test must pass unmodified.** Renaming a type in a test file is fine; changing an assertion is not.

**Stop condition:** if any existing test's *assertion* has to change to make it pass, stop and report. It means the promotion altered behaviour the design said it would not, and that is worth knowing before it is built on.

The `RegistrationMethod` strings inside `ParserClaim` registrations do not change — they name the user's call, not the internal type.

**Commit:** `refactor(abstractions): promote the container machinery out of the email parser`

---

# Part B — the zip parser

## Task 2: the options type and its ceilings

**Files:**
- Create: `src/Rag.NET.Parsers.Archive/Rag.NET.Parsers.Archive.csproj`, `ArchiveParserOptions.cs`
- Modify: `Rag.NET.slnx`
- Create: `tests/Rag.NET.Parsers.Archive.Tests/` project + `ArchiveParserOptionsTests.cs`
- Modify: `Rag.NET.slnx` for the test project too

Mirror `EmailParserOptions`' shape exactly — a `public const` ceiling, a backing field, a clamping setter, and a `Requested…` property the registration extension validates against so asking for more **throws** rather than being silently clamped:

| Property | Default | Ceiling const |
|---|---|---|
| `MaxTotalUncompressedBytes` | 256 MB | 2 GB |
| `MaxCompressionRatio` | 100 | 1000 |
| `MaxEntries` | 1,024 | 65,535 |

Add both projects to `Rag.NET.slnx`. Declare no `RequiresDocker`/`RequiresLlm` — `Rag.NET.RepoConventions.Tests` checks both directions and a stale declaration fails as loudly as a missing one.

Test: defaults are the table above; each setter clamps at its ceiling; each ceiling is exceeded → the registration extension throws naming the property and the ceiling. **Assert the message names the property and the number, and mutation-check that assertion** — a Phase 3.11 test passed against a mutant because a second incidental occurrence of the literal satisfied the substring.

**Commit:** `feat(archive): archive parser options with bomb ceilings`

---

## Task 3: the limiting stream, tested with bombs first

**Files:**
- Create: `src/Rag.NET.Parsers.Archive/LimitedReadStream.cs`
- Create: `tests/Rag.NET.Parsers.Archive.Tests/LimitedReadStreamTests.cs`, `ZipFixtureBuilder.cs`

**Write the bombs first.** A wrapping `Stream` that counts bytes actually read and throws a dedicated exception when a cap is exceeded. This is where the phase's security value lives, so it is tested in isolation before any parser exists.

**Do not build real 256 MB fixtures.** Lower the caps in the test instead — a 1 KB archive against `MaxTotalUncompressedBytes = 100` trips the same code path as a 300 MB archive against the default, and runs in milliseconds. One exception: include a *genuinely* high-ratio entry (1 MB of zero bytes compresses to roughly 1 KB, about 1000:1) so ratio detection is proved against real compressed data rather than a contrived counter.

Cases, each of which must fail before the corresponding cap exists **and name which cap it hit**:

- total uncompressed bytes exceeded;
- per-entry ratio exceeded;
- a **low-ratio, high-total** archive — the case two caps would miss, and the reason there are three;
- reads that stay under every cap pass through byte-for-byte identical to the source.

A bomb that trips the wrong cap is a test passing for the wrong reason. Assert *which* limit was reported, not merely that something threw.

`CompressedLength` is the ratio's denominator and is attacker-controlled; understating it makes the ratio look higher and trips earlier, which fails safe. Note that in the XML.

**Commit:** `feat(archive): a read-limiting stream that bounds decompression`

---

## Task 4: the parser

**Files:**
- Create: `src/Rag.NET.Parsers.Archive/ZipDocumentParser.cs`, `ArchiveParserLog.cs`, `ArchiveParserBuilderExtensions.cs`
- Create: `tests/Rag.NET.Parsers.Archive.Tests/ZipDocumentParserTests.cs`

`CanParse` accepts **`application/zip`** and **`application/x-zip-compressed`** — and nothing else. Explicitly **not** `application/epub+zip` (owned by `EpubDocumentParser`) and **not** `application/octet-stream` (Phase 3.11 made that invariant load-bearing).

`ParseAsync` opens a `ZipArchive` in read mode, rejects the archive if `Entries.Count` exceeds `MaxEntries`, then for each entry: skip directory entries (name ends `/`) and zero-length ones, decompress through `LimitedReadStream`, and dispatch by content type through `ContainerEntryDispatcher` — which contains a throwing parser to its own entry, so one bad entry does not cost the archive.

Entry names go through `FileNameSanitizer` and compose as `archive.zip#report.pdf`, mirroring `parent.eml#child.eml`. **Record in the XML that this is naming hygiene, not zip-slip mitigation** — this parser never touches the filesystem, and design §4 explains why claiming otherwise would mislead.

Content type per entry comes from `ContentTypeMap.FromFileName`.

`AddArchiveParser()` registers the parser, its options, and a `ParserClaim` **per claimed content type** — exactly the two, no more. Phase 3.11's guard fails at startup on any overlap, which is what stops a future change from over-claiming.

Tests: entries reach the right parsers end-to-end through a real container; a directory entry and an empty entry are skipped; an entry whose parser throws costs only that entry while siblings survive; `.epub` and `application/octet-stream` are **not** claimed; declared `ParserClaim`s match `CanParse` exactly.

**MA0051 will bite here.** Expect to extract the per-entry work into a helper.

**Commit:** `feat(archive): parse zip entries through the registered parsers`

---

## Task 5: the shared budget across formats

**Files:**
- Create: `tests/Rag.NET.Parsers.Archive.Tests/NestedContainerBudgetTests.cs`
- Modify: `tests/Rag.NET.Parsers.Archive.Tests/Rag.NET.Parsers.Archive.Tests.csproj` (add a `Rag.NET.Parsers.Email` reference)

This is the entire justification for Part A, so it is tested directly rather than assumed.

Build a `zip → .eml → zip` chain and assert the depth counter and entry budget are shared across the format boundary: a chain that alternates formats is bounded by the same numbers as one that does not.

**Assert the `cap ^ depth` trap explicitly.** With a budget of *N*, the total entries admitted across the whole tree must be *N* — not *N* per branch. Break the write-back in `ContainerBudget` and confirm this test goes red; that is the mutation which proves it is watching the invariant Task 1 was told to preserve. Report what you observed.

**Commit:** `test(archive): one budget across nested zip and email containers`

---

## Task 6: documentation

**Files:**
- Modify: `docs/reference/features.md` — the **Archive Parser (ZIP)** row and its detail section
- Modify: `docs/planning/ROADMAP.md`

`features.md`: flip the row to `[x]` and rewrite the detail section's **Status** to describe what shipped. The section currently lists three constraints as planned; the third of them — path traversal — **must be corrected, not ticked.** Design §4 explains why: this parser never touches the filesystem, so a traversal-shaped entry name is a metadata concern rather than zip-slip. State it as naming hygiene.

`ROADMAP.md`: flip Phase 3.10 to `[status: complete]` with a `**Completed:**` paragraph in the style of the other phases. Record that the phase turned out to be a promotion plus an addition rather than the reuse the entry predicted, and close the `MessageChild<TMessage>` union-by-convention debt scheduled here **or** say plainly why it was not addressed — do not leave it silently open.

Do **not** flip `MILESTONE.md`; that follows the whole-phase review.

**Commit:** `docs: close the archive parser phase`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. Email **76**, Templates **51**, `Rag.NET.Tests` **1325**, RepoConventions **9**, plus the new archive suite. **Any drop in the first four is a Part A regression.**
3. `grep -rn "EmbeddedMessageContext\|EmailAttachmentDispatcher\|MimeTypeMap\|EmbeddedMessageBudget" src/ tests/` returns nothing.
4. No new `#pragma` or `SuppressMessage` in the diff.
5. Both new projects appear in `Rag.NET.slnx`.

**Report:** every commit hash, verbatim build and test output, **which cap each bomb test reported**, what you observed when you broke `ContainerBudget`'s write-back, and everything this plan got wrong. That last item is not a formality — every one of the last four phases had a plan asserting something the code did not do, and each was caught only because the implementer ran the plan's own snippet and watched it disagree.
