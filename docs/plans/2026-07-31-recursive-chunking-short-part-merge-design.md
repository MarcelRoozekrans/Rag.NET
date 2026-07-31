# Recursive Chunking Short-Part Merge — Design (Phase 3.16)

**Date:** 2026-07-31
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.16
**Covers:** the probable library defect measured in Phase 3.12 and scheduled from the follow-up-debts list

Phase 3.12 needed the chunk counts to budget an embedding run, and the arithmetic did not work:
57,638 FiQA documents produced 429,850 chunks against the ~2× a 522-character median over a
512-character chunk size suggests. That was recorded as a probable defect with the intent behind it
unconfirmed. This phase confirms it, and it is worse than the counts implied.

## 0. It is a defect, and the current tests pin it

The roadmap made confirmation a precondition — "a strategy that deliberately preserves split
boundaries is a different conversation from one that forgot to pack them". Four probes through
`RecursiveChunkingStrategy` at stock `ChunkingOptions` (512 characters, 50 of overlap):

| Input | chars | chunks | ideal | mean chunk |
|---|---:|---:|---:|---:|
| A. 10 sentences, no newlines | 649 | 10 | 2 | 108 |
| B. 150 words, no `. ` and no `\n` | 749 | **150** | 2 | **8** |
| C. 60 short lines | 1,010 | 60 | 2 | 31 |
| D. the existing unit test's own input | 35 | **2** | **1** | 24 |

**Case B settles the question.** With no sentence separator present, the recursion falls through to
`" "` and emits one chunk per word — 150 chunks of 8 characters against a 512-character limit. No
one deliberately preserves word boundaries as chunk boundaries. This is not a boundary-preservation
policy; it is a missing merge.

Three distinct faults sit underneath, and the phase must fix all three or it fixes none:

**Short parts are never packed.** `SplitParts` yields every part that fits, whatever its size. This
is the one the roadmap named.

**The size limit is not consulted before splitting.** Case D is 35 characters against a 512-character
limit and still comes out as two chunks. `SplitRecursively` only checks `text.Length <= maxSize` on
the branch where the separator is *absent* (`parts.Length <= 1`). When a separator is present it
splits unconditionally. So a document far smaller than `MaxChunkSize` is still fragmented.

**Sentence punctuation is silently destroyed.** `text.Split(". ")` drops the separator and nothing
puts it back, so case A's first chunk ends `…near the river bank` — the period is gone from the
stored text. Every sentence-level boundary loses a character of the source.

**Case D is `ChunkAsync_SplitsByParagraphsFirst`, a passing test**, asserting two chunks for a
35-character input. The docs agree with it: the flowchart in `docs/guide/chunking.md` draws
*fits in MaxChunkSize? → yes → emit chunk* with no merge step. Code, tests and docs are consistent
with each other and all three are wrong — the failure shape this milestone has now found in six
phases. The fix therefore **changes existing assertions**, and any plan that leaves the current
tests green has not fixed anything.

## 1. The fix: pack at each separator level

Three candidate answers were on the table — a merge pass over emitted parts, a minimum chunk size,
or a split-and-pack loop. They collapse to one, because a minimum-size option only rescues case B
and leaves A, C and D untouched, and a post-hoc merge pass cannot know which separator to rejoin
with.

The separator list `["\n\n", "\n", ". ", " "]` is LangChain's `RecursiveCharacterTextSplitter`
shape, and that splitter's `_merge_splits` is precisely the missing step. Mirror its structure:

```
SplitRecursively(text, maxSize, sepIndex):
    if text.Length <= maxSize:        yield text; return      // NEW — the fit check comes first
    if sepIndex >= Separators.Length: hard split;   return

    sep   = Separators[sepIndex]
    parts = text.Split(sep)
    if parts.Length <= 1: return SplitRecursively(text, maxSize, sepIndex + 1)

    pending = []
    foreach part in parts:
        if part.Length <= maxSize:
            pending.Add(part)                                  // a candidate at THIS level
        else:
            yield* Pack(pending, sep, maxSize);  pending.Clear()
            yield* SplitRecursively(part, maxSize, sepIndex + 1)
    yield* Pack(pending, sep, maxSize)
```

`Pack` greedily accumulates consecutive candidates, rejoined **with that level's separator**, while
`buffer + sep + next` still fits, then emits and starts a new buffer.

**Parts are only ever packed with siblings from their own level.** Results returned by a deeper
recursion pass straight through. This is not an optimisation detail — packing a deeper result with
the outer separator would join text with a separator that never appeared between those two pieces,
fabricating source text that does not exist.

## 2. Rejoining must reproduce the source exactly

`Split` drops the separator; `Pack` puts it back. That fixes the destroyed sentence punctuation of
§0 for free, but only if the rejoin is faithful, and two things currently prevent that.

**Parts must not be trimmed before joining.** Today each part is `.Trim()`ed. Trimming then
rejoining normalises whitespace, so `"a  \n\n  b"` would come back as `"a\n\nb"` — text that never
appeared in the document. Instead: join the untrimmed parts, and trim only the final emitted chunk.
The joined result is then an exact substring of the source.

**Empty parts must be kept during the join.** Today whitespace-only parts are skipped. Under a
faithful rejoin, skipping them eats the separator run: `"a\n\n\n\nb"` splits to `["a", "", "b"]`,
and dropping the empty yields `"a\n\nb"` — again, not source text. Keep empties in the join, where
they contribute no characters but do re-add their separator, and discard a chunk only if it trims
away to nothing.

The property to test, and the reason both rules exist: **every emitted chunk, before overlap is
applied, is an exact substring of the section text.** That is what makes `StartPosition` and
`EndPosition` meaningful, and it is currently false.

## 3. Positions stop being a search

`ChunkAsync` locates each chunk with `IndexOf(text, cursor)` and, when that fails, silently falls
back to `pos = cursor` — a wrong position reported as a real one. The fallback exists because
trimmed text does not always match the source.

§2 removes the cause: chunks become exact substrings, so `IndexOf` from the cursor succeeds. The
silent fallback then has no legitimate case left and must go, rather than being left in place to
mask a future regression. Positions get direct tests on whitespace-irregular input, which they do
not have today — the existing position test uses `"First paragraph.\n\nSecond paragraph."`, where
every part is already trim-clean and the search cannot fail.

Keeping the string-based pipeline rather than threading `(start, length)` ranges through the
recursion is deliberate: with §2 in place the search is correct, and the cursor advances
monotonically so the total cost stays linear in the section length.

## 4. Overlap: unchanged semantics, now stated

Overlap keeps its current meaning — pack the body to `MaxChunkSize`, then prepend up to `Overlap`
characters of the previous chunk's own text. An emitted chunk can therefore reach
`MaxChunkSize + Overlap`: 562 characters at stock options.

That ceiling is true today and documented nowhere. The alternative — packing to
`MaxChunkSize − Overlap` so the emitted chunk respects the limit — was rejected for this phase: it
is a second semantic change riding along with the first, and it silently shrinks the useful body to
462 characters. The guide already warns that characters are not tokens, so `MaxChunkSize` was never
a hard bound for an embedding model in the first place. **The ceiling gets documented rather than
changed.**

One interaction is now live that was mostly theoretical before: because chunks get longer, the
overlap fraction falls. At stock options a chunk went from ~108 characters with 50 of overlap
(46%) to ~512 with 50 (10%). Nothing in the code changes; the phase states it, because a user who
tuned `Overlap` against the old fragment sizes will find it means something different.

## 5. Breaking change, taken deliberately

**Packing is the default and there is no opt-out.** Chunk boundaries change for every user of the
default strategy, so `DeterministicChunkId` changes and stored vectors must be re-ingested.

The library is pre-1.0 with Milestone 4 as the release milestone, which is exactly when this is
affordable. An option would preserve the broken mode permanently, force every downstream strategy
and document to explain two behaviours, and double the test matrix around a mode whose defining
output is 8-character chunks. Nobody would choose it deliberately.

The obligation this creates is documentation, not code: the change is called out in the chunking
guide and the migration notes, with the re-ingestion requirement stated plainly rather than left for
users to discover through degraded retrieval.

## 6. Every downstream number moves, and one of them is a prediction

`docs/reference/retrieval-quality.md` describes the old chunker. The real-chunking runs are
re-measured rather than annotated, and the chunk-shape assertions in
`Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments` change with them.

Phase 3.12 measured, under the old chunker:

| Dataset | parity | real | delta |
|---|---:|---:|---:|
| SciFact | 0.64593 | 0.65589 | +0.0100 |
| ArguAna | 0.50432 | 0.42594 | −0.0784 |

and explained the opposite signs by where relevance lives — passage-level for SciFact's claims
against abstracts, document-level for ArguAna's whole counterarguments. That explanation makes a
falsifiable prediction about this phase:

**ArguAna's loss should shrink substantially.** Its documents average ~1,190 characters, so packing
takes them from ~9.5 fragments to ~3 chunks, and if the loss came from fragmenting an argument then
un-fragmenting it must recover much of the 0.0784. **If ArguAna does not improve, the explanation
recorded in 3.12 was wrong** and the roadmap entry saying so must be corrected rather than left
standing. SciFact's +0.0100 gain may correspondingly shrink, since it was attributed to
fragmentation helping.

This is stated before measuring on purpose. The numbers are the measurement; the prediction is what
makes them capable of disconfirming something.

Parity runs are untouched — they index one chunk per document and never call the chunker's split
path — which makes them the regression gate for the phase, exactly as SciFact's 0.64593 was for
3.12.

## 7. What the audit of the other strategies found

The roadmap put other strategies out of scope "unless the same shape is found in them — in which
case say so rather than widening quietly." It was found nowhere, but something else was.

`FixedSizeChunkingStrategy` is a straight window and packs correctly by construction.

`HierarchicalMergerChunkingStrategy` **never reads `MaxChunkSize` at all.** It emits one chunk per
heading subtree, so its chunks are unbounded above — the inverse defect. `BookChunkingStrategy`,
`LegalChunkingStrategy` and `AcademicPaperChunkingStrategy` all delegate to it and inherit that.
This is plausibly deliberate, since a heading subtree is a semantic unit and truncating it would
defeat the strategy's purpose, but it is undocumented and a user setting `MaxChunkSize` on one of
those templates gets no effect from it.

**Recorded, not fixed here.** It is a different defect with a different argument, and folding it in
would be the quiet widening the roadmap warned against.

## Out of scope

- **`HierarchicalMergerChunkingStrategy`'s unbounded chunks** — §7, recorded for scheduling.
- **Changing `Overlap` accounting** to keep emitted chunks under `MaxChunkSize` — §4.
- **Token-aware sizing.** `TokenAwareChunkingStrategy` already exists for anyone who needs a real
  token bound; this phase does not make the character chunker pretend to be one.
- **Re-running FiQA's real leg.** It stays Phase 3.15's. This phase makes it cheaper — packing
  should take 429,850 chunks to roughly 115,000 on the median-document arithmetic, which is an
  estimate and labelled as one — but measuring it is not this phase's job.
