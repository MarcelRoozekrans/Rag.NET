# ONNX reranker loads SentencePiece cross-encoders (#926)

**Date:** 2026-11-14 · **Issue:** #926 · **Ships as:** `feat:` (new public option)

## Intent

`docs/guide/retrieval.md` recommends `bge-reranker-v2-m3` as the multilingual model for
`Rag.NET.Reranking.Onnx`, but the reranker only takes a BERT `vocab.txt` (`VocabPath`), and that
model is XLM-RoBERTa with a SentencePiece tokenizer. The package has no working multilingual
option while the docs say it has one. The operator's decision on 2026-11-09: add a `TokenizerPath`
option backed by the SentencePiece tokenizer in `Microsoft.ML.Tokenizers`, then check that
`bge-reranker-v2-m3` loads and ranks Dutch text sensibly before the docs row stays. #994 (Tokenizers
3.0.0, shipped in v1.8.1) was the prerequisite: 1.0.3 refuses the model's Unigram file.

## Goals

- `OnnxRerankerOptions.TokenizerPath`: path to a SentencePiece model file
  (`sentencepiece.bpe.model`). Exactly one of `VocabPath` and `TokenizerPath` is set. Both or
  neither throws `ArgumentException` naming the two options; the file named must exist
  (`FileNotFoundException`, as `VocabPath` does today).
- With `TokenizerPath`, the reranker encodes pairs the way XLM-RoBERTa cross-encoders were trained:
  - tokenizer: `SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false,
    addEndOfSentence: false)`;
  - **fairseq id offset**: raw `<unk>`=0, `<s>`=1, `</s>`=2 map to the model's `<unk>`=3, `<s>`=0,
    `</s>`=2; every other piece is raw id + 1. Raw ids fed directly score garbage without failing,
    so this is fixed behaviour, not an option;
  - **pair template** `<s> query </s></s> passage </s>`: four special positions, against BERT's
    three. Truncation reuses `CrossEncoderPairTokenizer.SplitBudget` with 4 reserved positions;
    `MaxLength` must exceed 4 on this path.
- Both paths feed only the inputs the ONNX session declares (`InferenceSession.InputMetadata`).
  XLM-R exports usually declare no `token_type_ids`; feeding an undeclared input fails the run.
  A declared input the reranker cannot supply throws at construction, naming it.
- A Dutch ranking check against `bge-reranker-v2-m3` passes before the docs row is kept. The docs
  show the `TokenizerPath` setup next to the existing one.

## Non-goals

- Reading Hugging Face `tokenizer.json`. Not loadable for Unigram models by
  `Microsoft.ML.Tokenizers`, and nothing asks for model families beyond XLM-R.
- SentencePiece for the ONNX embedders (`Rag.NET.Embeddings.Onnx`). A separate request if it comes.
- Batching pairs into one session run. Scoring stays one pair per run, as today.

## Anti-goals

- A public id-offset or template knob. One wrong value gives plausible, wrong rankings with no
  error, which is the failure #926 exists to remove.
- Changing the BERT path's output. `VocabPath` users get the same ids, masks and segment ids;
  only the input feeding follows the session's declared inputs, which for BERT exports is all three.

## Approaches

- **A. `TokenizerPath` with fixed XLM-R rules** (chosen by the operator, 2026-11-14). One new
  option; offset and template built in; covers bge-reranker-v2-m3 and the other XLM-R
  cross-encoders.
- **B. Read `tokenizer.json`.** Generic over layouts, but means parsing the Unigram vocabulary and
  the post-processor ourselves. Rejected: much more code to own for no requested model.
- **C. A plus public offset/template options.** Rejected: brings back the silent-garbage risk.

## Shape (builder's latitude, my calls marked)

- *My call:* an internal pair-encoder seam: the existing `CrossEncoderPairTokenizer` (BERT) and a
  new `XlmRobertaPairTokenizer`, both returning ids, mask and segment ids, with
  `SpecialTokensPerPair` per implementation. `OnnxReranker` picks one from the options.
- *My call:* `UseOnnxReranking` initialises both paths to `""` as today; the exclusivity check
  treats empty as unset.
- *My call:* tests.
  - Unit tests without a model file: options validation (both, neither, missing file, `MaxLength`
    at 4), template layout and budget with 4 reserved positions, through an internal seam that
    takes raw piece ids so the offset mapping is tested without the 5 MB model.
  - Gated tests, skipped unless `RAGNET_ONNX_RERANK_TOKENIZER` (and for scoring
    `RAGNET_ONNX_RERANK_XLMR_MODEL`) point at existing files. The reranker telemetry test gates on
    `RAGNET_ONNX_RERANK_MODEL`/`_VOCAB`, which hold the BERT model, so the XLM-RoBERTa export gets its
    own variable. Ids for a Dutch sentence equal the ids the model's
    `tokenizer.json` gives (recorded once as expected values); and the Dutch ranking check: for a
    few Dutch questions, the answering passage outranks plausible distractors.
- *Builder's call:* which community ONNX export of bge-reranker-v2-m3 (the BAAI repo ships none);
  pin its revision in the test's doc comment. Record the run in the PR, since CI has no model.
- *My call:* a CHANGELOG-visible `feat(reranking)` commit; docs updated in `docs/guide/retrieval.md`
  (setup for both tokenizer kinds, which file each model ships) and `docs/reference/features.md`
  where it names `VocabPath`.
