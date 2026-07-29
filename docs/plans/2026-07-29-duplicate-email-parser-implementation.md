# Duplicate Email Parser Implementation Plan (Phase 3.11)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Stop `Rag.NET.Chunking.Templates`' parsers from claiming `application/octet-stream`, which today turns one unknown-extension attachment into a failed document parse, and make the remaining `message/rfc822` overlap a startup error instead of silent content loss.

**Architecture:** Four independent fixes in three packages. Remove two `CanParse` clauses; add a `try`/`catch` in `EmailAttachmentDispatcher`; add a declared `ParserClaim` plus a `ValidateParserClaims` step in `AddRagNet`; rename the Templates parser type.

**Tech Stack:** .NET 10, MimeKit, CsvHelper, xUnit v3.

**Design:** `docs/plans/2026-07-29-duplicate-email-parser-design.md`. Read it first — §4 explains why detection needs a declared claim rather than probing `CanParse`, and that constraint is not obvious from the code.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (**one public type per file, file name must match — this matters in Task 5**), MA0006 (`string.Equals` not `==`), MA0008, MA0009, MA0132, MA0140, ZA0601/ZA0501, EPS05/EPS06, EPC12/EPC13 (**a `catch` that reads only `ex.Message` is an error — Task 3**), HLQ001/HLQ004/HLQ012/HLQ013, NU1510. **No new `#pragma` or `SuppressMessage`.**
- All logging through `LoggerMessage` source-gen. `Rag.NET.Parsers.Email` has `EmailParserLog.cs`; Templates uses `[LoggerMessage]` partial methods on each type. Never `logger.LogX` directly. **Note:** `EmailAttachmentDispatcher.cs:47` currently calls `logger?.LogWarning` directly — pre-existing, and Task 3 touches that method, so move it to the source-gen while you are there.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One commit per task.
- **Never `git add -A` or `git add .`** — explicit paths.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.

Baselines: `Rag.NET.Chunking.Templates.Tests` **34**, `Rag.NET.Parsers.Email.Tests` **71**, `Rag.NET.Tests` **1308**, `Rag.NET.RepoConventions.Tests` **9**.

**Timestamp trap:** restoring a file from git or a backup can preserve an mtime that makes MSBuild skip recompiling, so `--no-build` then tests a stale binary. Touch the file or build without `--no-build`, and confirm from the log that it recompiled.

---

## Task 1: the end-to-end failure, red first

**Files:**
- Modify: `tests/Rag.NET.Chunking.Templates.Tests/Rag.NET.Chunking.Templates.Tests.csproj`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/ParserClaimConflictTests.cs`

No test project currently references **both** `Rag.NET.Chunking.Templates` and `Rag.NET.Parsers.Email`, which is a large part of why this went unnoticed. Add a `ProjectReference` to `..\..\src\Rag.NET.Parsers.Email\Rag.NET.Parsers.Email.csproj`.

Neither package needs Docker or a model, so **do not** add `RequiresDocker`/`RequiresLlm` to the csproj — `Rag.NET.RepoConventions.Tests` fails in both directions and will catch a stale declaration.

**Write the test that is this phase.** Build a `.eml` carrying a text body and two attachments — one `notes.txt` and one `payload.dat` whose extension `MimeTypeMap` does not know, so it resolves to `application/octet-stream`. Register both packages (`UseEmailChunking()` and `AddEmailParser()`), parse through `Rag.NET.Parsers.Email.EmailDocumentParser`, and assert the body and `notes.txt` both survive.

```csharp
[Fact]
public async Task AnUnknownExtensionAttachmentDoesNotFailTheWholeDocument()
{
    // Rag.NET.Chunking.Templates' parsers claim application/octet-stream, which is what
    // MimeTypeMap returns for any extension it does not know. EmailAttachmentDispatcher has no
    // try/catch around parser.ParseAsync, so the Templates parser throwing on random bytes
    // escapes the entire document parse -- body and every other attachment with it.
    var sections = await ParseWithBothPackagesRegistered(ct);

    Assert.Contains(sections, s => s.Text.Contains("body text", StringComparison.Ordinal));
    Assert.Contains(sections, s => s.Text.Contains("notes content", StringComparison.Ordinal));
}
```

**Run it. Expected: FAIL** with `InvalidOperationException: Failed to parse .eml file 'payload.dat'`. Report the message verbatim.

Add a second case asserting the same in the **other registration order** (`AddEmailParser()` before `UseEmailChunking()`), because which parser wins is registration-order dependent and the fix must hold either way.

**Commit:** `test(templates): pin the unknown-extension attachment failure`

---

## Task 2: remove the octet-stream claims

**Files:**
- Modify: `src/Rag.NET.Chunking.Templates/EmailDocumentParser.cs:19-21`
- Modify: `src/Rag.NET.Chunking.Templates/QAPairsDocumentParser.cs:18-22`

Delete the `application/octet-stream` clause from both `CanParse` implementations. Nothing else changes in either file.

`application/octet-stream` means "unknown binary". A format-specific parser answering it is always guessing, and here a wrong guess is an exception rather than a miss.

**Guard against over-correcting.** Add tests asserting the legitimate claims still hold — `text/csv`, `application/vnd.ms-excel` and the OpenXML spreadsheet type for QAPairs, `message/rfc822` for the email one — and that both now return **false** for `application/octet-stream`. I checked: no existing test asserts the octet-stream claim is true, so nothing should break. **Verify that rather than trusting it**, and if an existing test does fail, stop and report rather than editing it.

**Run Task 1's tests. Expected: PASS**, both orders.

**Commit:** `fix(templates): stop claiming application/octet-stream`

---

## Task 3: contain attachment failures

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/EmailAttachmentDispatcher.cs`
- Modify: `src/Rag.NET.Parsers.Email/EmailParserLog.cs`
- Create: `tests/Rag.NET.Parsers.Email.Tests/AttachmentFailureContainmentTests.cs`

Task 2 fixes this parser. It does not stop the next parser that accepts a type and then throws, including a third-party one.

Wrap `parser.ParseAsync` so a throwing parser costs only its own attachment: log a warning naming the attachment **and** the parser type, then continue with the next attachment.

**Two things make this harder than it looks:**

1. **You cannot `yield return` from inside a `try` with a `catch`.** C# forbids it. The enumeration has to be driven manually — get the enumerator, and wrap each `MoveNextAsync` in the `try`/`catch`, yielding outside it. Write this carefully; it is the whole task.
2. **EPC12/EPC13 make a `catch` that reads only `ex.Message` a build error.** Log the exception object itself — the `LoggerMessage` signature takes an `Exception` parameter.

**Top-level ingestion keeps throwing.** Do not touch `ParseBehavior` or `ParentDocumentIngestionBehavior`. The asymmetry is deliberate and the design explains it: an attachment is sub-content the caller never named; a document the caller passed directly should fail loudly rather than silently index nothing.

**Test with a parser that throws on purpose** — a fake `IDocumentParser` whose `CanParse` accepts a type and whose `ParseAsync` throws. Assert: the body and sibling attachments survive, the warning names both the attachment and the parser type, and — importantly — a parser that throws **after** yielding some sections keeps the sections it already yielded. That second case is the one a naive implementation gets wrong.

Also assert the containment does **not** swallow `OperationCanceledException` — cancellation must still propagate.

**Commit:** `fix(email): contain a failing attachment parser to its own attachment`

---

## Task 4: make the message/rfc822 overlap a startup error

**Files:**
- Create: `src/Rag.NET.Abstractions/ParserClaim.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs`
- Modify: `src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/ParserClaimValidationTests.cs`

Both parsers still claim `message/rfc822`. Registration order decides which wins, and when the Templates one wins a 3-level nested `.eml` yields **2 sections instead of 6** — silent content loss.

`ParserClaim` is a small record carrying the content type, the parser type name, and the registration method a user would recognise:

```csharp
public sealed record ParserClaim(string ContentType, string ParserTypeName, string RegistrationMethod);
```

Register one alongside each claiming parser: `AddEmailParser()` (for `message/rfc822` and `application/vnd.ms-outlook`), `UseEmailChunking()` (`message/rfc822`), `UseQAPairsChunking()` (`text/csv` and the two spreadsheet types).

`ValidateParserClaims(services)` goes in `AddRagNet` **after** `configure?.Invoke(builder)`, alongside the existing `WireRefinementStrategy` / `WireDeepResearch` / `WireTimeWeighting` / `WireTagRetrieval` calls — that point sees the final registration set regardless of the order the user called things in. Group the claims by content type; throw `InvalidOperationException` on any type claimed more than once, naming every claimant and its registration method.

**Why not just call `CanParse`?** Because that needs live instances, which means building a service provider during registration. And `ServiceDescriptor.ImplementationType` is `null` for every colliding registration — they all use factory lambdas (`sp => sp.GetRequiredService<EmailDocumentParser>()`). The design's §4 covers this; do not rediscover it the hard way.

**Test the message content by its parts, not its prose.** Assert it contains both parser type names and both registration method names. A Phase 3.9 test asserted "the message names the ceiling" and passed against a mutant, because a second incidental occurrence of the literal satisfied the substring. **Mutation-check this assertion**: remove one name from the message, confirm the test fails, restore.

Also assert that registering **either package alone** does not throw — the guard must not fire on the normal case.

**Commit:** `feat(rag): fail at startup when two parsers claim one content type`

---

## Task 5: rename the Templates parser

**Files:**
- Rename: `src/Rag.NET.Chunking.Templates/EmailDocumentParser.cs` → `EmailTemplateDocumentParser.cs`
- Modify: `src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs`
- Rename: `tests/Rag.NET.Chunking.Templates.Tests/EmailDocumentParserTests.cs` → `EmailTemplateDocumentParserTests.cs`

Two public types named `EmailDocumentParser`, both `IDocumentParser`, both claiming `message/rfc822`, is a reading hazard in stack traces and logs. A public API break, free now and expensive after 4.1.

Use `git mv` so the rename is visible as a rename in history rather than a delete-plus-add. MA0048 requires the file name to match the type name, so both files move.

Update the `ParserClaim` from Task 4 to carry the new name, and check whether any documentation names the old type (`grep -rn "EmailDocumentParser" docs/`) — `Rag.NET.Parsers.Email` has a type of the same name, so **read each hit before changing it** and only touch the ones meaning the Templates type.

**Commit:** `refactor(templates)!: rename EmailDocumentParser to EmailTemplateDocumentParser`

---

## Task 6: correct the documentation

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/MimeTypeMap.cs` — the type XML
- Modify: `docs/planning/ROADMAP.md`

`MimeTypeMap`'s XML was rewritten in Phase 3.9 to record that its "no parser claims `application/octet-stream`" assumption was **false**, with a pointer to this phase. That is now stale in the opposite direction: after Task 2 the assumption holds again. Rewrite it to state the assumption plainly, and keep one sentence of history — that two parsers claimed it until 3.11 — so a future reader knows the invariant is load-bearing rather than incidental.

> **Corrected during implementation — do not simply flip the 3.9 addendum to "true again".** That
> addendum blames `MimeTypeMap` for a failure on a path `MimeTypeMap` never touches.
> `FromFileName` has exactly one caller, `StorageMessageAdapter.cs:86`, so it serves `.msg` only;
> an `.eml` attachment gets its type from the part's own MIME headers via
> `MimeMessageAdapter.Enumerate`. Both routes produce `application/octet-stream` and both were
> broken, but only one of them is this type's doing. State the narrower truth: the extension
> fallback **and** a header-supplied `application/octet-stream` now both go unclaimed, and the
> invariant matters for both. Correct the 3.9 addendum's attribution rather than preserving it.

`ROADMAP.md`: move the **Two `EmailDocumentParser`s** entry to `### Closed`, recording what shipped and what did not — the `message/rfc822` overlap is now a startup error rather than resolved, and third-party parsers registered through `AddParser<T>()` are still undetected. Flip Phase 3.11 to `[status: complete]` with a `**Completed:**` line in the style of the other phases.

Do **not** flip `MILESTONE.md` — that happens after the whole-phase review.

**Commit:** `docs: close the duplicate email parser debt`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. All four suites at or above baseline: Templates **34**+, Parsers.Email **71**+, `Rag.NET.Tests` **1308**+, RepoConventions **9**.
3. `grep -rn "application/octet-stream" src/Rag.NET.Chunking.Templates/` returns nothing.
4. No new `#pragma` or `SuppressMessage` anywhere in the diff.
5. Re-run Task 1's end-to-end test in both registration orders on the final state.

**Report:** every commit hash, verbatim build and test output, the verbatim failure message from Task 1 before the fix, what happened when you mutation-checked Task 4's assertion, and everything this plan got wrong. That last item is not a formality — each of the last three phases had a plan that asserted something the code did not do, and every one was caught only because the implementer ran the plan's own snippet and watched it disagree.
