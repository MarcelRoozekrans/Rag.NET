# Email Parser Debt Implementation Plan (Phase 3.6)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Delete the duplicated filename sanitizer in the email parser, and close the traversal-ceiling debt with the reasoning it was missing.

**Architecture:** Mostly deletion. `EmbeddedMessageMetadata.Compose` calls the shared `FileNameSanitizer` instead of its own copy; the ceiling stays exactly as it is and gains an explanation.

**Tech Stack:** .NET 10, xUnit v3.

**Design:** `docs/plans/2026-07-29-email-parser-debt-design.md`. Read it first, especially §2 — the second debt's recorded fix does not work, and the phase closes it rather than attempting it.

---

## Conventions
- Warnings are errors: MA0051 (≤60 lines), MA0015, MA0048 (one public type per file), MA0006 (`string.Equals` not `==`), MA0008, MA0009, MA0132, MA0140, ZA0601/ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ004/HLQ012/HLQ013, NU1510. **No new `#pragma` or `SuppressMessage`.**
- xUnit v3 `TestContext.Current.CancellationToken`; no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — explicit paths. `.lucent/*` is expected dirty; leave it.

Verify after each task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

Baselines: `Rag.NET.Parsers.Email.Tests` — **read the current count before you start and report it**; `Rag.NET.Tests` **1308**; `RepoConventions` **9**.

---

## Task 1: pin the defect before fixing it

**Files:**
- Create or extend: `tests/Rag.NET.Parsers.Email.Tests/EmbeddedMessageNamingTests.cs`

`EmbeddedMessageMetadata` is `internal`; check whether `Rag.NET.Parsers.Email` already grants `InternalsVisibleTo` to its test project and add it in the repo's `AssemblyAttribute` form if not.

**Write the failing test first.** This one fails against the current code, which is the whole point:

```csharp
[Fact]
public void ANonBreakingSpaceReExposedByDotTrimmingIsTrimmed()
{
    // TrimEnd('.', ' ') matches exactly two characters in a single pass, so stripping the dot
    // uncovers a non-breaking space it cannot see. char.IsWhiteSpace matches U+00A0 and the
    // rest of the Unicode whitespace set, and the shared sanitizer loops to a fixed point
    // because trimming dots re-exposes whitespace and vice versa. Today this name keeps its
    // non-breaking space.
    var metadata = EmbeddedMessageMetadata.Create(Parent(), "Quarterly report .", ".eml", "message/rfc822");

    Assert.DoesNotContain(' ', metadata.FileName);
}
```

> **Corrected during implementation.** This plan originally passed a *bare* trailing
> non-breaking space and claimed it fails today. It does not: the old `Sanitize` opens with
> `name.Trim()`, which is `char.IsWhiteSpace`-based and already removes U+00A0. Verified by
> running it — green against the pre-Task-2 code. Only the closing `TrimEnd('.', ' ')` is
> single-pass and two-character, so a trailing **dot** is what exposes the defect. The snippet
> above carries the corrected input.

Run it. **Expected: FAIL.** Report the message.

Then the two behaviour changes, which will fail until Task 2 and should be written now so Task 2 turns all three green at once:

- a stem longer than 64 characters survives to 128 (assert on a length between the two, so it pins the new cap rather than merely "longer than before");
- an all-invalid stem such as `"///"` yields `embedded-message` rather than `___`.

Also assert what must **not** change: the `parent.eml#child.eml` composition, the `#` separator, and that a normal subject is untouched. A deletion this size should be fenced on both sides.

**Commit:** `test(email): pin the naming changes the shared sanitizer brings`

---

## Task 2: delete the duplicate

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/EmbeddedMessageMetadata.cs`

Delete `Sanitize`, `InvalidChars`, `Replacement`, `MaxNameLength`, `BuildInvalidChars` — and the `<remarks>` block on `InvalidChars` arguing for the duplication, which Phase 2.5 already made false.

`Compose` becomes:

```csharp
private static string Compose(string parentFileName, string? name, string extension) =>
    string.Concat(parentFileName, Separator.ToString(), FileNameSanitizer.Sanitize(name, Fallback), extension);
```

Keep `Separator` and `Fallback`. `Fallback` is now an *argument* rather than a private rule, which is why the design says that divergence dissolves — passing `"embedded-message"` preserves today's behaviour exactly.

**Rely on the default `maxLength: 128`** rather than passing it; a parameter equal to its default is noise. Note the 64 → 128 change in the XML on `Compose` instead, where a reader looking for why names got longer will actually find it.

`FileNameSanitizer` lives in the bare `Rag.NET` namespace, so `Rag.NET.Parsers.Email` resolves it by enclosing-namespace lookup — **no `using` is needed**. That was the point of the namespace choice in Phase 2.5; verify rather than adding one reflexively.

Run Task 1's tests. **Expected: all pass.** Run the whole email suite and `Rag.NET.Tests`.

**Commit:** `refactor(email): use the shared filename sanitizer`

---

## Task 3: give the ceiling its reasoning, and close both debts

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/EmailParserOptions.cs` — the XML on `MaxSupportedEmbeddedDepth`
- Modify: `docs/planning/ROADMAP.md`
- Modify: the email parser's guide section (**find it — do not assume which file**)

**No code changes.** The ceiling stays at 64.

The XML must say why it cannot be removed, not merely that it exists: `EmailAttachmentDispatcher` selects a parser by content type and re-enters through the public `IDocumentParser` boundary, so a chain of embedded messages runs through frames belonging to arbitrary third-party parsers. A work queue cannot unwind those. The indirection is deliberate — it replaced a `ReferenceEquals(parser, self)` check that missed `.eml → .msg → .eml` chains because consecutive levels use different parser instances.

Include the three numbers, since they are what make 64 defensible: real chains run 10–20 deep, the ceiling is 64, the measured overflow floor is ~500.

> **Falsified in the whole-phase review.** Both instructions above are wrong and the work they
> produced was reverted. The `IDocumentParser`-boundary argument does not hold: a nested
> `message/rfc822` arrives as a live `MessagePart` and recurses inside `EmailDocumentParser`
> without the dispatcher, so the frames are ours and a `Stack<IAsyncEnumerator<DocumentSection>>`
> drained LIFO would flatten them at identical section ordering. The debt is reopened as
> **Phase 3.9**. Of the three numbers, `10–20` has no source anywhere in the repository and was
> dropped; the two measured ones carry the argument. See design §2 "Falsified in review" and the
> reopened `ROADMAP.md` entry.

**ROADMAP:** move both entries to `### Closed`. The sanitizer one closes as implemented. The traversal one closes as **re-justified, not implemented** — say so plainly, so a future reader does not go looking for the work queue. Leaving it open would keep implying a fix exists that nobody has got to.

**Guide:** note that embedded-attachment names may change and how — the cap, the all-invalid fallback, the non-breaking space.

**Commit:** `docs: close both email parser debts`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. `Rag.NET.Parsers.Email.Tests` at its new count; `Rag.NET.Tests` **1308**; `RepoConventions` **9**.
3. Confirm `EmbeddedMessageMetadata.cs` no longer contains `SearchValues`, `MaxNameLength` or a `Sanitize` method.
4. Confirm nothing else in the repository referenced the deleted members.
5. `ROADMAP.md` and `MILESTONE.md` flip Phase 3.6 to complete **after** the whole-phase review — both files.

**If the deletion turns out to change a test you did not write** — an existing recursion or parser test asserting on a name — stop and report rather than editing it to fit. Nothing about the traversal changes in this phase, so an unexpected failure means the sanitizer difference reaches further than the design accounted for, and that is worth knowing before it is papered over.
