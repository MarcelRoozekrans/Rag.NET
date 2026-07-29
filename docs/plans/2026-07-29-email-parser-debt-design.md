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
| Trailing non-breaking space re-exposed by dot trimming | kept | trimmed |

The third is a genuine defect. `TrimEnd('.', ' ')` matches two characters in a single pass;
`char.IsWhiteSpace` matches U+00A0 and the rest of the Unicode whitespace set, and the shared
implementation loops to a fixed point because trimming dots re-exposes whitespace and vice versa.

**Corrected during implementation:** a *bare* trailing non-breaking space does **not** survive
today — the old `Sanitize` opens with `name.Trim()`, which is `char.IsWhiteSpace`-based and removes
U+00A0 before anything else happens. Nor does the row need truncation to fire. What survives is a
non-breaking space that the closing `TrimEnd('.', ' ')` *uncovers* by stripping a trailing dot, and
then cannot match: `"Quarterly report\u00A0."` keeps its U+00A0.

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

## 3. Testing

The three divergences in §1 get tests, because each is a behaviour change someone could otherwise
mistake for a regression:

- a name longer than 64 characters now survives to 128;
- an all-invalid name falls back to `"embedded-message"` rather than `"___"`;
- a name whose trailing dot, once trimmed, uncovers a non-breaking space no longer keeps it.

The last is the one to write first — it fails against the current code, which is the point.

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
- **Retiring the parsers' `EmbeddedMessageMetadata` type itself.** Only `Sanitize` is duplicated;
  `Compose` and the `parent.eml#child.eml` naming convention are the parser's own and stay.
- **Raising `MaxEmbeddedDepth`'s default.** Nobody has asked for a deeper chain, and the ceiling is
  the thing this phase is justifying rather than moving.
