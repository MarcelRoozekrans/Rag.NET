# Email Parser Debt — Design (Phase 3.6)

**Date:** 2026-07-29
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.6
**Covers:** the two email-parser debts recorded in `ROADMAP.md` from the Phase 2.1 Part C review

Both were recorded as behaviour changes rather than refactors, which is why they were scheduled
instead of done inline. One of them turns out not to be a behaviour change at all — because the fix
it proposes cannot work.

## 1. The duplicate sanitizer — delete it

`EmbeddedMessageMetadata.Sanitize` in `Rag.NET.Parsers.Email` reimplements `FileNameSanitizer`. The
recorded justification, still in the source, argues for the duplication:

> `Rag.NET.DataProviders.FileNameSanitizer` does the same job with more rules, but this assembly does
> not reference `Rag.NET.DataProviders` …

Phase 2.5 moved `FileNameSanitizer` to `Rag.NET.Abstractions`, which this parser *does* reference, so
the argument has been false since then. The comment was corrected at the time; the code was not.

Deleting the copy removes `Sanitize`, `InvalidChars`, `Replacement`, `MaxNameLength` and
`BuildInvalidChars` — roughly half the file — and `Compose` calls
`FileNameSanitizer.Sanitize(name, Fallback)` instead, relying on the shared default
`maxLength: 128` rather than restating it.

### One of the three recorded divergences dissolves

The debt lists three. `FileNameSanitizer` takes the fallback as a **parameter**, so passing
`"embedded-message"` preserves that behaviour exactly rather than changing it. What actually changes:

| | Before | After |
|---|---|---|
| Length cap | 64 | 128 |
| All-replacement input (`"///"`) | `"___"` | `"embedded-message"` |
| Trailing non-Ascii whitespace re-exposed by dot trimming | kept | trimmed |
| **Whitespace control character at an edge** (found in review) | trimmed | replaced with `_` |

The third is a genuine defect. `TrimEnd('.', ' ')` matches two characters in a single pass;
`char.IsWhiteSpace` matches U+00A0 and the rest of the Unicode whitespace set, and the shared
implementation loops to a fixed point because trimming dots re-exposes whitespace and vice versa.

**Corrected during implementation:** a *bare* trailing non-breaking space does **not** survive
today — the old `Sanitize` opens with `name.Trim()`, which is `char.IsWhiteSpace`-based and removes
U+00A0 before anything else happens. Nor does the row need truncation to fire. What survives is a
non-breaking space that the closing `TrimEnd('.', ' ')` *uncovers* by stripping a trailing dot, and
then cannot match: `"Quarterly report\u00A0."` keeps its U+00A0. The mechanism is general, so the
row is not about U+00A0 specifically \u2014 U+2007 (figure space) and U+3000 (ideographic space) behave
identically, and so does anything else `char.IsWhiteSpace` accepts but `' '` does not.

**Found in the whole-phase review \u2014 a fourth divergence, and the reason the count above is four.**
The two sanitizers order their steps oppositely. The deleted copy called `name.Trim()` **before**
replacing invalid characters; `FileNameSanitizer.Clean` replaces **before** trimming. TAB, LF, VT,
FF and CR are C0 control characters *and* whitespace, so one sitting in a leading or trailing
whitespace run is now substituted to `_` first \u2014 and `_` is not whitespace, so `TrimEdges` cannot
remove it:

| input | before Phase 3.6 | after |
|---|---|---|
| `"report\t"` | `parent.eml#report.eml` | `parent.eml#report_.eml` |
| `"\treport"` | `parent.eml#report.eml` | `parent.eml#_report.eml` |
| `"report \t"` | `parent.eml#report.eml` | `parent.eml#report _.eml` |
| `".\t"` | `parent.eml#embedded-message.eml` | `parent.eml#._.eml` |

Reachable through `.msg`: `MsgDocumentParser` takes the name from `Storage.Message.Subject`, a raw
MAPI `PidTagSubject` with no header normalization, and MsgReader was observed preserving a trailing
tab verbatim. **`FileNameSanitizer` is not changed to match.** Its ordering is shared with four
other call sites and is not obviously wrong \u2014 replacing first is arguably the more correct rule, and
reordering it to suit one caller would move behaviour under all five. The divergence is documented
and pinned by tests that record the pre-3.6 value alongside each case, so a future reader can tell
an intentional change from a regression.

The second is arguably a fix as well: `"___"` tells a reader nothing about what the attachment was,
where `"embedded-message"` at least names the category.

Nothing is published — NuGet packaging is Phase 4.1 — so per the posture this repository has already
set twice, the break is taken cleanly and documented rather than shimmed.

## 2. The traversal ceiling — the recorded fix cannot work

The debt says:

> Converting the traversal to an explicit work queue would remove the class entirely and is the real
> fix if a large `MaxEmbeddedDepth` is ever wanted.

**That premise does not hold, and this design closes the entry rather than carrying it forward.**

The recursion is not ours to flatten. `EmailAttachmentDispatcher.DispatchAsync` selects a parser by
**content type**, not by type identity:

```csharp
foreach (var p in parsers)
    if (p.CanParse(mimeType)) { parser = p; break; }
…
await foreach (var section in parser.ParseAsync(content, attachmentMetadata, cancellationToken))
    yield return section;
```

So a chain of embedded messages runs `ParseAsync → DispatchAsync → ParseAsync → …` where each hop
may land in an arbitrary `IDocumentParser`. That indirection is deliberate — the dispatcher's own
remarks say the content-type test exists "so a third-party replacement for either parser is bounded
too", and it replaced an earlier `ReferenceEquals(parser, self)` check that missed
`.eml → .msg → .eml` chains entirely because consecutive levels are handled by *different* parser
instances.

**A work queue cannot unwind frames belonging to code we do not own.** We could flatten the case
where the resolved parser is one of our two, but the ceiling would still have to exist for every
other case — so the class is not removed, only narrowed, at the cost of a second traversal path and
the risk of changing section ordering. Every level is also an async-iterator hop, so the frames are
state machines rather than plain calls, which is why ~500 levels overflowed on ~40 KB of crafted
input in the first place.

### What is true instead

- Real forwarded-email chains run maybe ten to twenty levels deep.
- The ceiling is **64**.
- The measured overflow floor is **~500**.

The ceiling is not a workaround awaiting a proper fix. It is the correct design given a public,
third-party-extensible parser boundary, and it sits an order of magnitude below the failure point
and several times above any real input. What was missing was the reasoning, not the code.

So this phase records that reasoning against `EmailParserOptions.MaxSupportedEmbeddedDepth` and
closes the debt. Leaving it open would keep implying that a fix exists and nobody has got to it.

### Falsified in review

**Everything above from "That premise does not hold" onward is wrong, and the debt is reopened as
Phase 3.9.** The section is kept rather than rewritten because it is the reasoning the phase was
built on, and the record needs both halves.

**The central error.** The argument assumes every level of embedded-message recursion goes through
`EmailAttachmentDispatcher`. It does not. `EmailDocumentParser.ParseAttachmentsAsync` tests
`if (entity is MessagePart embedded)` and routes to `ParseEmbeddedAsync`, which calls
`ParseMessageAsync` **directly**:

```
ParseMessageAsync → ParseAttachmentsAsync → ParseEmbeddedAsync → ParseMessageAsync
```

Four internal async-iterator frames per level, no interface hop, no dispatcher, no third-party
parser. That is the dominant path — a nested `message/rfc822` is exactly what MimeKit surfaces as a
live `MessagePart`, and the parser's own comment says it is "parsed in place rather than re-entering
the stream-based `ParseAsync`". The dispatcher path exists, but it is the *other* case: a
message-typed **stream** attachment, such as an `.eml` carrying a `.msg`.

**Probe.** Construct `EmailDocumentParser` with an **empty** `IEnumerable<IDocumentParser>`, so the
dispatcher can resolve nothing whatsoever, and feed it a 64-level `MessagePart` chain with
`MaxEmbeddedDepth = 64` and the fan-out cap raised above 64 so depth is the only bound. Result:
**130 sections** — two per level for 64 levels plus two for the outer message — reaching the
innermost body, with **no warnings**. Full recursion to the ceiling with zero parsers registered.
The dispatcher was provably not on the path.

(Run first with the default `MaxEmbeddedMessages = 50`, the probe stops at 102 sections on the
fan-out cap rather than the depth ceiling. That is the node cap doing its job, not evidence about
depth, and it has to be raised before the probe measures what it claims to.)

**Consequence for the measurement.** The ~500-level overflow floor came from the
~81-bytes-per-level hand-crafted MIME described in `2026-07-26-engineering-debt-sweep-design.md`
(around lines 160–180) — that is nested `message/rfc822`, i.e. this same in-place path. So the
frames that overflowed were almost certainly all ours.

**Two subsidiary errors.**

- *"A queue of ours can only unwind frames we own"* conflates **crossing a public interface** with
  **entering third-party code**. In every configuration this repository ships, the parser the
  dispatcher resolves for `message/rfc822` **is** `EmailDocumentParser`. The interface hop is real;
  the third-party frame is hypothetical.
- *"the risk of changing section ordering"* is a property of a FIFO **queue**, not of the
  transformation. The standard flattening of a recursive async iterator is a
  `Stack<IAsyncEnumerator<DocumentSection>>` drained **LIFO** — depth-first, byte-identical
  ordering, O(1) frames regardless of depth. This design never distinguished the two, and the word
  "queue" carried an objection that the actual data structure does not have.

**What should have been written.**

> An explicit stack would remove the overflow class for the in-place `MessagePart` path entirely,
> and for the dispatcher path in every configuration this repository ships. The ceiling would
> survive only as a bound on a third-party parser registered for a message content type. The cost
> is a second traversal path in a parser that currently has one. Not done because the ceiling
> holds — 64 is an order of magnitude below the measured floor, so no input reaching the bound can
> overflow — but it is deferred work, not a closed question.

The ceiling itself is unaffected: 64 was and remains the right number. What changes is why. It is
not "the recursion is unflattenable"; it is "the recursion is flattenable and we have not done it,
and 64 is low enough that nothing breaks in the meantime."

## 3. Testing

The three divergences in §1 get tests, because each is a behaviour change someone could otherwise
mistake for a regression:

- a name longer than 64 characters now survives to 128;
- an all-invalid name falls back to `"embedded-message"` rather than `"___"`;
- a name whose trailing dot, once trimmed, uncovers a non-breaking space no longer keeps it.

The last is the one to write first — it fails against the current code, which is the point.

**Added after the whole-phase review:** the fourth divergence in §1 gets tests too — the leading and
trailing whitespace-control-character cases, each recording the pre-3.6 value beside the current one
so the direction of the change is legible from the test itself.

Existing recursion tests are untouched: nothing about the traversal changes.

## 4. Documentation

`ROADMAP.md`: both entries move to the **Closed** list, the second with its re-justification rather
than a claim it was implemented.

`EmailParserOptions.MaxSupportedEmbeddedDepth`: the XML gains why the ceiling cannot be removed —
the parser boundary is public, so the recursion is not entirely ours.

The email parser guide notes that embedded-attachment names may change, and how.

## Out of scope

- **Flattening our own recursion path** (§2). It narrows nothing that matters: the ceiling stays,
  and a second traversal path risks section ordering for a case no real input reaches.
  *(Both halves of that sentence were falsified in the whole-phase review — see §2, "Falsified in
  review". It stays out of scope for 3.6 and is rescheduled as Phase 3.9.)*
- **Retiring the parsers' `EmbeddedMessageMetadata` type itself.** Only `Sanitize` is duplicated;
  `Compose` and the `parent.eml#child.eml` naming convention are the parser's own and stay.
- **Raising `MaxEmbeddedDepth`'s default.** Nobody has asked for a deeper chain, and the ceiling is
  the thing this phase is justifying rather than moving.
