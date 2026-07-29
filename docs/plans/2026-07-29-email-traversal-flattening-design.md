# Email Traversal Flattening — Design (Phase 3.9)

**Date:** 2026-07-29
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.9
**Covers:** the stack-recursive email traversal debt recorded in `ROADMAP.md` from the Phase 2.1
Part C review, reopened out of Phase 3.6

This phase runs ahead of 3.7 and 3.8, out of numeric order. It keeps the number it was assigned
when it was scheduled after 3.8, because three committed artifacts already reference "Phase 3.9".

## 0. Why this is being done twice

Phase 3.6 closed this debt as *re-justified, not implemented*, arguing that the recursion could not
be flattened because it re-enters through the public `IDocumentParser` boundary, so its frames
belong to arbitrary third-party parsers. That phase's own whole-phase review falsified the claim: a
nested `message/rfc822` arrives as a live `MimeKit.MessagePart` and is routed to
`ParseEmbeddedAsync`, which calls `ParseMessageAsync` **directly**, with `EmailAttachmentDispatcher`
never involved. Probe-verified with an empty parsers list against a 64-level chain.

Two words did most of the damage, and both were inherited rather than examined:

- Phase 2.1 recorded the fix as an explicit **work queue**. A queue is FIFO, FIFO reorders sections,
  and section ordering is exactly what one must not risk in a parser. Everyone who touched the entry
  afterwards — including the 3.6 design — argued against the data structure they had been handed.
  The correct structure is a **stack drained LIFO**, which is depth-first and order-preserving.
- The 3.6 review, and then the reopened roadmap entry, named the fix
  `Stack<IAsyncEnumerator<DocumentSection>>`. That type cannot express this traversal:
  an `IAsyncEnumerator<DocumentSection>` can say "here is a section" or "I am finished", and has no
  way to say "descend into a child here, then resume me". Driving off a stack of *section*
  enumerators would need a marker type smuggled through the stream. The workable unit is a
  traversal **frame**.

Recorded here because the pattern is now the phase's most transferable output: a debt note's
vocabulary propagates into every later decision about it, and neither of these words survived being
questioned.

## 1. What is wrong today

Both parsers run the same cycle:

```
ParseMessageAsync → ParseAttachmentsAsync → ParseEmbeddedAsync → ParseMessageAsync
```

Three async iterators per nesting level. Because each `await foreach` makes the outer iterator's
`MoveNextAsync` await the inner one's, pulling a single section from depth *N* drives *3N* nested
`MoveNextAsync` calls. Measured on this parser: 480 levels survive, 500+ terminates the process with
`0xC00000FD` `STATUS_STACK_OVERFLOW`, which no `catch` can intercept. About 40 KB of hand-crafted
MIME reaches 500 levels, at roughly 81 bytes per level.

`MaxSupportedEmbeddedDepth = 64` bounds it an order of magnitude below the floor, so **nothing
reachable crashes today**. This is latent robustness work, not a live defect.

## 2. The replacement

One internal depth-first driver holding an explicit `Stack<Frame>`, where a frame is a message plus
its position in that message's attachment list.

```
push Frame(root)
while stack is not empty:
    frame = stack.Peek()
    if not frame.HeaderEmitted:             emit subject, then body sections
    if frame.Attachments.MoveNext() fails:  stack.Pop(); continue
    step = current attachment
      embedded message  →  policy.TryDescend? push Frame(child) : skip
      file attachment   →  await foreach dispatcher sections
```

Stack depth is constant regardless of nesting; depth costs heap.

**Order is preserved by `Peek`-then-`Pop`-when-exhausted.** A parent's attachment *i* expands
completely before *i+1* is touched, which is precisely what the recursion does. This is the whole
reason the structure is a stack and not a queue.

Body sub-parses (`HtmlDocumentParser`) and dispatcher hops are `await foreach`-ed inline inside the
loop rather than pushed. They are bounded, so their frames come and go without accumulating.

### Two seams

**The adapter** carries everything library-specific: subject, body text and HTML, the attachment
enumerator, and how to tell an embedded message from a file attachment. MimeKit needs
`DecodeToAsync` into a `MemoryStream`; MsgReader hands over a `byte[]` and needs the `MimeTypeMap`
fallback for senders that omit `PidTagAttachMimeTag`. Those differences live in two small adapters;
the traversal is written once.

Writing it once is the point. This repository has already paid for duplicated logic — Phase 2.1
found the *fourth* copy of a filename sanitizer, and Phase 3.6 spent itself deleting one of them. A
duplicated traversal would mean a future fix to one parser silently not reaching the other.

**The descent policy** answers "may I descend into this, and under what metadata?". Production wires
it to `EmbeddedMessageContext.TryEnterEmbedded` plus `EmbeddedMessageMetadata.Create`, preserving
depth accounting, budget accounting and warning behaviour exactly. Tests wire it to *always yes*.

This is a genuine separation — traversal and limit policy are different jobs — and it is also the
only way the phase's central claim becomes testable. See §3.

## 3. Proving the overflow class is gone

The obstacle: the overflow floor is ~500 levels, the ceiling is 64, and
`EmailParserOptions.MaxEmbeddedDepth` clamps to the ceiling. **No test reaching through the public
surface can construct a case that would ever have overflowed.** A 64-level test passes identically
before and after this change, certifying nothing — the same shape as the vacuous guards this
milestone has repeatedly turned up.

The descent-policy seam removes the obstacle. A fake adapter and an always-yes policy drive the
driver **10,000 levels** deep — twenty times the measured floor — in milliseconds, with no MIME
parsing and no options validation in the way.

**That test cannot pass against recursive code.** Its failure mode is a terminated test process
rather than a red test, because `STATUS_STACK_OVERFLOW` is uncatchable. That is ugly, and it is
still the right trade: an unmissable signal beats a 64-level test that would pass either way. The
test must carry a comment saying so, or the next person to see the runner die will treat it as
flakiness.

## 4. Proving nothing else moved

This is a pure refactor. **Any difference in emitted sections is a bug**, not an accepted change —
the opposite posture from Phase 3.6, which was a deliberate behaviour change.

A multi-branch fixture pins it: a message with a body, two embedded children each carrying their own
file attachments, and a sibling file attachment following them. The assertion is on the **exact
section sequence**, not a count. Ordering is the one thing a stack can plausibly get wrong, and only
a sequence assertion catches a subtle reordering — a count assertion passes while depth-first
quietly becomes breadth-first.

The existing `EmbeddedMessageRecursionTests` stay untouched and serve as the regression net. If the
flattening breaks one of them, that is the answer, not something to edit into agreement.

**Set `MaxEmbeddedMessages` deliberately in any depth test.** At its default of 50, a 64-level chain
stops on the fan-out cap rather than the depth ceiling. The Phase 3.6 probe hit exactly that and
would have measured the wrong bound had its first result been read at face value.

## 5. What does not change

- `MaxSupportedEmbeddedDepth` stays **64**. It stops being an overflow guard and becomes a bound on
  a third-party parser registered for a message content type, plus a fan-out sanity limit. Its XML
  needs narrowing again, not deleting.
- `MaxEmbeddedDepth` (3) and `MaxEmbeddedMessages` (50) defaults are untouched.
- `SectionIndex` is still stamped exactly once, in `ParseAsync`.
- The dispatcher path still re-enters through `IDocumentParser`. It costs a handful of frames per
  hop and is bounded by the same depth accounting.
- A nested `Storage.Message` is still deliberately not disposed — it belongs to the outer message's
  `Attachments` collection.
- `EmailAttachmentDispatcher` is not touched.

## 6. Risk

The driver plus two adapters is more moving parts than the recursion it replaces, and MA0051's
60-line method limit will force the loop to be split across helpers. If the result reads worse than
what it replaced, that is a real cost paid against a bug class the 64 ceiling already prevents from
firing. Reviewers should weigh readability seriously here rather than treating the refactor as
self-evidently good.

Frames hold `IEnumerator<T>` instances that may be `IDisposable`. The driver must dispose every
frame left on the stack when enumeration ends early — a caller breaking out of the `await foreach`,
or cancellation. `yield return` inside `try`/`finally` is legal in C# iterators and `finally` runs on
`DisposeAsync`, so this is expressible, but it is the most likely place for a leak to hide.

## Out of scope

- **Raising the ceiling or the defaults.** Nobody has asked for a deeper chain. This phase changes
  what the ceiling protects against, not where it sits.
- **Flattening the dispatcher path.** It crosses a genuine public boundary, costs a bounded handful
  of frames per hop, and is already bounded by depth accounting.
- **Touching `EmailAttachmentDispatcher`.** Its content-type dispatch is deliberate and correct; it
  replaced a `ReferenceEquals(parser, self)` check that missed `.eml → .msg → .eml` chains.
