# Late Chunking Newline Defect — Design (Phase 3.13)

**Date:** 2026-07-30
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.13
**Covers:** the defect Phase 3.7 exposed by provisioning an ONNX model for the first time

Nightly is deliberately red until this lands.

## 0. How it was found, and why that matters

Phase 3.7 needed a local dense embedder, which meant provisioning an ONNX model in CI. That ran
`LateChunkingIntegrationTests` for the first time in the project's history — the suite had been
skipping since Phase 3.5 because `RAGNET_ONNX_EMBED_MODEL` is consumed as a *path to a file* and
nothing ever put a model on a runner. It failed immediately.

The feature shipped in Phase 1.1 and has been inert since. It was invisible because its only
integration test could not run, and the unit tests exercise a `WindowRunner` seam that bypasses the
tokenizer entirely.

## 1. What is actually wrong

`ITokenEmbeddingGenerator` promises `TokenOffsets` are spans into the **original** input.
`BertTokenizer` returns offsets into the **normalized** text. When normalization changes the length,
those offsets cannot be mapped back, and `OnnxTokenEmbeddingGenerator.ThrowIfNormalizationChangedLength`
refuses rather than lying about it.

**The guard is correct.** Probed against the real `all-MiniLM-L6-v2` vocabulary, applying each
token's offset to both strings: the normalized slice yields exactly the token's own text, the
original yields garbage, and on `"日本語 text"` the offset for `text` is **out of bounds**. There is
no overload that returns original-text offsets.

### The defect is five times broader than it was recorded as

| Input | Length change | |
|---|---|---|
| plain ASCII, double space, NFC accents, umlauts, emoji, Cyrillic, NBSP, zero-width | none | ok |
| newline / tab / CR | 10 → 9 | **refused** |
| trailing newline | 11 → 10 | **refused** |
| control character | 10 → 9 | **refused** |
| NFD-decomposed accents | 14 → 11 | **refused** |
| **CJK** | 8 → **14** | **refused** |

Each row is one probe string, and the rows do not share one. The NFD row is a longer string carrying
**three** combining marks, which is why it loses three characters; the single-accent fixture the
tests use, `"cafe" + U+0301 + " test"`, measures **10 → 9** on the same code path. The CJK row is
`"日本語 text"` — three ideographs, a space inserted either side of each. Quote a row's figure only
with the string it came from: `NormalizationGuardTests` asserts 8 → 14 and 10 → 9 because those are
*its* fixtures, and 14 → 11 belongs to neither of them.

Late chunking works only on single-line, NFC, non-CJK text. It is inert for any document with a line
break, for all Japanese, Chinese and Korean text, and for NFD text — which is what macOS filesystems
produce.

### The worse half: tokens, not just offsets

```
input  "alpha\n\nbeta gamma"   →  normalized  "alphabeta gamma"
tokens: alphabet | ##a | gamma
```

BERT's reference implementation treats `\n` as whitespace and **substitutes a space**. This tokenizer
strips it as a control character, so `alpha` and `beta` **merge into a word the document never
contained**. Multi-line text does not merely get unmappable offsets — it gets wrong tokens, and
therefore wrong embeddings. A fix that only restored offsets would still be embedding `alphabet`.

## 2. Severity, stated accurately

`EmbeddingBehavior` backfills: it collects every chunk whose `Embedding` is null or empty and embeds
it normally, keeping any that already exist. So when late chunking fails, `LateChunkingStrategy`
catches, logs, and falls back to unembedded windows — and those windows then receive **ordinary**
embeddings downstream.

**Nothing has been unretrievable.** The defect is that a configured feature silently did not apply:
you asked for late chunking and got standard chunking, with a log line. That is a real defect and
worth fixing, but it is not data loss, and the debt entry that called it "chunks with
`Embedding = null`" was accurate about the mechanism and misleading about the consequence.

## 3. The fix

Replace `\n`, `\t` and `\r` with a single space before tokenizing.

- **Length-preserving**, so offsets stay valid against the original text and the guard passes.
- **Corrects the tokens**, matching BERT's own whitespace handling, which is the half that would
  otherwise corrupt embeddings regardless of offsets.
- Applied where the text enters the tokenizer, so both the offsets and the token stream see the same
  string.

## 4. What the guard keeps refusing

CJK grows under normalization and NFD shrinks. No length-preserving substitution fixes either, and
the probe showed offsets going out of bounds on CJK, so the refusal is genuine rather than cautious.

What changes is the **message**. Today it says the length changed and suggests stripping control
characters. It should name the cause, because "your corpus is Japanese", "your text is
NFD-normalized" and "your file has newlines" are three different problems and only the third is now
fixed.

## 5. The guard gets tests, because it has none

Nothing anywhere calls `ThrowIfNormalizationChangedLength`. **A guard that silently disabled a
feature for months was itself unguarded**, which is how it survived — the unit tests exercise a
`WindowRunner` seam that never reaches the tokenizer, and the one test that would have caught it
could not run.

Pinned: whitespace now passes; CJK and NFD still refuse; the refusal names which cause fired.

## 6. End-to-end proof

`LateChunkingIntegrationTests` — the test that has never run — must pass against a real model, with
its two-paragraph fixture producing non-null embeddings. That is what returns nightly to green, and
it is the only assertion in this phase that exercises the whole path.

## Out of scope

- **CJK and NFD support.** They need a mapping from normalized positions back to original ones,
  built against a normalizer whose behaviour this phase has just discovered is non-obvious. Recorded
  as a documented limitation rather than a silent one.
- **Changing the fallback.** `LateChunkingStrategy` degrading on failure is correct given
  `EmbeddingBehavior` backfills; one awkward section should not fail a document.
- **The other two inert guards Phase 3.7 found.** `RAGNET_TESSDATA`'s reader sits behind an
  `#if ENABLE_OCR` no workflow sets, so that test is not skipped — it is not compiled. Separate
  problem, separate phase.
