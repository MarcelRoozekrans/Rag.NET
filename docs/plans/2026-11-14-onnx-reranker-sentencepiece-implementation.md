# ONNX reranker loads SentencePiece cross-encoders: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `OnnxReranker` scores XLM-RoBERTa cross-encoders such as `bge-reranker-v2-m3` through a new `TokenizerPath` option, and the docs row recommending that model is true.

**Architecture:** A second internal pair tokenizer, `XlmRobertaPairTokenizer`, sits beside `CrossEncoderPairTokenizer` behind a small internal interface. It loads the SentencePiece model with `Microsoft.ML.Tokenizers`, applies the fairseq id offset and the `<s> q </s></s> p </s>` template, and reuses `CrossEncoderPairTokenizer.SplitBudget`. `OnnxReranker` picks the tokenizer from the options and feeds only the inputs the ONNX session declares.

**Tech Stack:** .NET 10/11, `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers` 3.0.0, xUnit v3 (`Assert.SkipWhen`).

**Spec:** `docs/plans/2026-11-14-onnx-reranker-sentencepiece-design.md`

## Global Constraints

- Exactly one of `VocabPath` and `TokenizerPath` is set; null, empty or whitespace counts as unset. Both or neither throws `ArgumentException` naming both options.
- Fairseq offset, fixed: raw 0 (`<unk>`) → 3, raw 1 (`<s>`) → 0, raw 2 (`</s>`) → 2, every other raw id → raw + 1. Not an option.
- XLM-R pair template: `<s> query </s></s> passage </s>`, 4 special positions; `MaxLength` must exceed 4 on that path, 3 on the BERT path.
- No public offset or template knob. The BERT path's ids, mask and segment ids are unchanged.
- Feed only the inputs `InferenceSession.InputMetadata` declares; a declared input outside `input_ids`, `attention_mask`, `token_type_ids` throws at construction, naming it.
- Commits: conventional, header at most 100 characters, no nested parentheses in bodies, no `Co-Authored-By` trailer, no session links.
- Docs C# fences are compiled by `tests/Rag.NET.PackageValidation.Tests`; keep every fence valid against the produced packages.

## Review Focus

1. `TokenizerPath` pointing at the wrong kind of file (`tokenizer.json`, `vocab.txt`): expect an `InvalidDataException` naming `TokenizerPath` and the path, not a raw parser exception. Test in Task 2.
2. A query or passage with no pieces (empty string): expect a valid feed `<s></s></s> p </s>`, no exception. Test in Task 1.
3. Characters the SentencePiece vocabulary lacks: they must reach the model as `<unk>` = 3, never raw 0 (= `<s>`). Pinned by the `MapRawId` test in Task 1.
4. An XLM-R export that does declare `token_type_ids`: it must get all zeros, since RoBERTa has one segment type. Test in Task 1.
5. `TokenizerPath = "   "` beside a real `VocabPath`: whitespace is unset, so the BERT path is used, not a "both set" error. Test in Task 2.

---

### Task 1: `XlmRobertaPairTokenizer`

**Files:**
- Create: `src/Rag.NET.Reranking.Onnx/IPairTokenizer.cs`
- Create: `src/Rag.NET.Reranking.Onnx/XlmRobertaPairTokenizer.cs`
- Modify: `src/Rag.NET.Reranking.Onnx/CrossEncoderPairTokenizer.cs` (implements `IPairTokenizer`; nothing else changes)
- Test: `tests/Rag.NET.Reranking.Onnx.Tests/XlmRobertaPairTokenizerTests.cs`

**Interfaces:**
- Consumes: `CrossEncoderPairTokenizer.SplitBudget(int queryTokenCount, int passageTokenCount, int available)`.
- Produces:
  - `internal interface IPairTokenizer { (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string query, string passage, int maxLength); }`
  - `internal sealed class XlmRobertaPairTokenizer : IPairTokenizer` with `internal const int SpecialTokensPerPair = 4;`, constants `BosId = 0`, `EosId = 2`, `UnkId = 3` (all `long`), ctor `XlmRobertaPairTokenizer(string tokenizerPath)`, `internal static long MapRawId(int rawId)`, `internal static (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) BuildFeed(IReadOnlyList<int> rawQueryIds, IReadOnlyList<int> rawPassageIds, int maxLength)`.

- [ ] **Step 1: Write the failing model-free tests**

```csharp
[Theory]
[InlineData(0, 3)]  // <unk>
[InlineData(1, 0)]  // <s>
[InlineData(2, 2)]  // </s>
[InlineData(3, 4)]
[InlineData(6585, 6586)] // "▁Wat", per the model's tokenizer.json
public void MapRawId_AppliesTheFairseqOffset(int raw, long expected)
    => Assert.Equal(expected, XlmRobertaPairTokenizer.MapRawId(raw));

[Fact]
public void BuildFeed_LaysThePairOutAsTheXlmRobertaTemplate()
{
    var (ids, mask, types) = XlmRobertaPairTokenizer.BuildFeed([10, 11], [20], maxLength: 512);
    Assert.Equal([0L, 11, 12, 2, 2, 21, 2], ids);
    Assert.All(mask, m => Assert.Equal(1L, m));
    Assert.All(types, t => Assert.Equal(0L, t));
    Assert.Equal(ids.Length, mask.Length);
    Assert.Equal(ids.Length, types.Length);
}

[Fact]
public void BuildFeed_WithAnEmptyQuery_StillProducesAValidFeed()
    => Assert.Equal([0L, 2, 2, 21, 2], XlmRobertaPairTokenizer.BuildFeed([], [20], 512).InputIds);

[Theory]
[InlineData(5)] [InlineData(6)] [InlineData(16)] [InlineData(512)]
public void BuildFeed_NeverProducesMorePositionsThanMaxLength(int maxLength)
{
    var longSide = Enumerable.Range(3, 1000).ToArray();
    var ids = XlmRobertaPairTokenizer.BuildFeed(longSide, longSide, maxLength).InputIds;
    Assert.True(ids.Length <= maxLength);
    Assert.Equal(0L, ids[0]);
    Assert.Equal(2L, ids[^1]);
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Rag.NET.Reranking.Onnx.Tests -c Release --filter-class "*XlmRobertaPairTokenizerTests"` (or the project's equivalent filter syntax for xUnit v3)
Expected: build failure, `XlmRobertaPairTokenizer` does not exist.

- [ ] **Step 3: Implement `IPairTokenizer` and `XlmRobertaPairTokenizer`**

`BuildFeed` truncates through `CrossEncoderPairTokenizer.SplitBudget(q.Count, p.Count, maxLength - SpecialTokensPerPair)`, then writes `BosId`, the mapped query ids, `EosId`, `EosId`, the mapped passage ids, `EosId`; mask all 1, token types all 0. The ctor opens the file and calls `SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false)`; check the 3.0.0 parameter names against the package. `Encode` takes raw ids from `EncodeToIds` for each side with no truncation inside the tokenizer, then calls `BuildFeed`. A doc comment on the class records why the offset exists: raw ids score garbage without failing. Make `CrossEncoderPairTokenizer` implement `IPairTokenizer`.

- [ ] **Step 4: Run the model-free tests; they pass**

Same command. Expected: all pass. The existing `CrossEncoderPairTokenizerTests` still pass.

- [ ] **Step 5: Provision the tokenizer file and record the reference ids**

Download `sentencepiece.bpe.model` and `tokenizer.json` from `BAAI/bge-reranker-v2-m3` at a pinned commit into a scratch directory outside the repo, such as `~/.cache/ragnet-models/bge-reranker-v2-m3/`. Compute the reference feed with the Hugging Face `tokenizers` Python package in an isolated venv: `Tokenizer.from_file("tokenizer.json").encode("Wat is de hoofdstad van Nederland?", "Amsterdam is de hoofdstad van Nederland.").ids`.

- [ ] **Step 6: Write the gated parity test**

```csharp
[Fact]
public void Encode_MatchesTheIdsTheModelsOwnTokenizerGives()
{
    var path = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_TOKENIZER");
    Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
        "Set RAGNET_ONNX_RERANK_TOKENIZER to bge-reranker-v2-m3's sentencepiece.bpe.model to run this test.");
    var ids = new XlmRobertaPairTokenizer(path!)
        .Encode("Wat is de hoofdstad van Nederland?", "Amsterdam is de hoofdstad van Nederland.", 512).InputIds;
    Assert.Equal(/* ids from Step 5, pasted */, ids);
}
```

The doc comment names the model repo and the pinned commit the expected ids came from.

- [ ] **Step 7: Run it with the variable set; it passes. Run it without; it skips**

Run: `RAGNET_ONNX_RERANK_TOKENIZER=<path> dotnet test tests/Rag.NET.Reranking.Onnx.Tests -c Release`
Expected: the parity test passes. Without the variable it reports as skipped.

- [ ] **Step 8: Commit**

```bash
git add src/Rag.NET.Reranking.Onnx/IPairTokenizer.cs src/Rag.NET.Reranking.Onnx/XlmRobertaPairTokenizer.cs \
  src/Rag.NET.Reranking.Onnx/CrossEncoderPairTokenizer.cs tests/Rag.NET.Reranking.Onnx.Tests/XlmRobertaPairTokenizerTests.cs
git commit -m "feat(reranking): encode XLM-RoBERTa cross-encoder pairs from a SentencePiece model"
```

---

### Task 2: `TokenizerPath` option and declared-input feeding

**Files:**
- Modify: `src/Rag.NET.Reranking.Onnx/OnnxRerankerOptions.cs`
- Modify: `src/Rag.NET.Reranking.Onnx/OnnxReranker.cs`
- Modify: `src/Rag.NET.Reranking.Onnx/RagBuilderExtensions.cs` (initialiser becomes `new OnnxRerankerOptions { ModelPath = "" }`)
- Test: `tests/Rag.NET.Reranking.Onnx.Tests/OnnxRerankerTests.cs`

**Interfaces:**
- Consumes: `IPairTokenizer`, `XlmRobertaPairTokenizer(string)`, `XlmRobertaPairTokenizer.SpecialTokensPerPair`, `CrossEncoderPairTokenizer.SpecialTokensPerPair`.
- Produces: `public string VocabPath { get; set; } = "";` (no longer `required`; source-compatible) and `public string? TokenizerPath { get; set; }` on `OnnxRerankerOptions`, with doc comments saying which file each model family ships and that exactly one is set.

- [ ] **Step 1: Write the failing tests**

- `Constructor_WhenBothVocabPathAndTokenizerPathAreSet_ThrowsArgumentException`: message contains `VocabPath` and `TokenizerPath`.
- `Constructor_WhenNeitherVocabPathNorTokenizerPathIsSet_ThrowsArgumentException`: `[Theory]` over `VocabPath` `""`/`" "` with `TokenizerPath` `null`/`""`; message contains both names.
- `Constructor_WhenTokenizerPathIsWhitespace_UsesVocabPath`: `VocabPath = "nonexistent/vocab.txt"`, `TokenizerPath = "   "`, real temp model file; throws `FileNotFoundException` whose `FileName` is the vocab path.
- `Constructor_WhenMaxLengthCannotHoldTheXlmRobertaSpecialTokens_ThrowsArgumentOutOfRangeException`: `TokenizerPath` set, `MaxLength = 4`; message contains `MaxLength`.
- `Constructor_WhenMaxLengthIsFourOnTheBertPath_PassesTheLengthCheck`: `VocabPath` set, `MaxLength = 4`, missing files; throws `FileNotFoundException`, not `ArgumentOutOfRangeException`.
- `Constructor_WhenTokenizerPathDoesNotExist_ThrowsFileNotFoundException`: real temp model file; `FileName` equals the tokenizer path.
- `Constructor_WhenTokenizerPathIsNotASentencePieceModel_ThrowsInvalidDataException`: temp model file plus a temp tokenizer file containing `{}`; message contains `TokenizerPath` and the path.

Existing tests stay as they are and must still pass.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Rag.NET.Reranking.Onnx.Tests -c Release`
Expected: the new tests fail, either by not compiling (`TokenizerPath` missing) or on the asserted exception.

- [ ] **Step 3: Implement validation and the tokenizer choice in `OnnxReranker`**

Order: null check → exclusivity (`string.IsNullOrWhiteSpace` means unset) → `MaxLength` against the chosen path's `SpecialTokensPerPair` (message names the special tokens of that path) → model file exists → vocab or tokenizer file exists → build the tokenizer (`XlmRobertaPairTokenizer` creation wrapped: any exception from loading becomes `InvalidDataException($"TokenizerPath '{path}' is not a SentencePiece model file ...", inner)`) → `InferenceSession`. The field type becomes `IPairTokenizer`.

- [ ] **Step 4: Implement declared-input feeding**

After creating the session, read `_session.InputMetadata.Keys`. If one is not `input_ids`, `attention_mask` or `token_type_ids`, dispose the session and throw `InvalidOperationException` naming that input and listing the three the reranker supplies. If `input_ids` is not declared, the same. `ScorePair` adds a `NamedOnnxValue` only for declared names.

- [ ] **Step 5: Run the tests; they pass**

Run: `dotnet test tests/Rag.NET.Reranking.Onnx.Tests -c Release`
Expected: all pass, gated ones skipped.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.Reranking.Onnx tests/Rag.NET.Reranking.Onnx.Tests/OnnxRerankerTests.cs
git commit -m "feat(reranking): add TokenizerPath to OnnxRerankerOptions for SentencePiece cross-encoders" \
  -m "Closes #926. Exactly one of VocabPath and TokenizerPath is set. The reranker feeds only the inputs the ONNX session declares, since XLM-RoBERTa exports usually take no token_type_ids."
```

---

### Task 3: Real-model checks: Dutch ranking and the BERT path

**Files:**
- Create: `tests/Rag.NET.Reranking.Onnx.Tests/XlmRobertaRerankingTests.cs`
- Modify: `docs/plans/2026-11-14-onnx-reranker-sentencepiece-design.md` (the scoring variable's name, see Step 1)

**Interfaces:**
- Consumes: `OnnxReranker`, `OnnxRerankerOptions.TokenizerPath` from Task 2.

- [ ] **Step 1: Pick and provision the models**

Find a community ONNX export of `bge-reranker-v2-m3` on Hugging Face whose input names and single logit fit the reranker, and pin its commit. Download it to the scratch directory from Task 1. Also download `cross-encoder/ms-marco-MiniLM-L-6-v2`'s ONNX file and `vocab.txt` for the BERT regression run. The scoring test reads `RAGNET_ONNX_RERANK_XLMR_MODEL`, not `RAGNET_ONNX_RERANK_MODEL`. That one already holds the BERT model the telemetry test pairs with `RAGNET_ONNX_RERANK_VOCAB`. Correct the design doc's line to say so.

- [ ] **Step 2: Write the gated Dutch ranking test**

`RerankAsync_RanksTheAnsweringDutchPassageFirst`, skipped unless `RAGNET_ONNX_RERANK_XLMR_MODEL` and `RAGNET_ONNX_RERANK_TOKENIZER` name existing files. There are three Dutch questions, for example on the capital, the Tweede Kamer and the age to apply for a driving licence. Each has one passage that answers it and three distractors that share its topic words but don't answer it. For every question, the answering passage has the highest `RelevanceScore`. The class doc comment names the export repo and its pinned commit.

- [ ] **Step 3: Run the real-model tests**

Run:
```bash
RAGNET_ONNX_RERANK_XLMR_MODEL=<onnx> RAGNET_ONNX_RERANK_TOKENIZER=<spm> \
RAGNET_ONNX_RERANK_MODEL=<minilm onnx> RAGNET_ONNX_RERANK_VOCAB=<vocab.txt> \
dotnet test tests/Rag.NET.Reranking.Onnx.Tests -c Release
```
Expected: the Dutch ranking test, the parity test and `OnnxRerankerTelemetryTests` all pass with none skipped. If the Dutch test fails, stop and report the scores. The docs row depends on it.

- [ ] **Step 4: Commit**

```bash
git add tests/Rag.NET.Reranking.Onnx.Tests/XlmRobertaRerankingTests.cs docs/plans/2026-11-14-onnx-reranker-sentencepiece-design.md
git commit -m "test(reranking): check bge-reranker-v2-m3 ranks Dutch answers first"
```

---

### Task 4: Docs

**Files:**
- Modify: `docs/guide/retrieval.md` (the "Recommended models" table near line 893 and its setup text)
- Modify: `docs/reference/features.md:320-345` (options table, usage, and the stale "BERT whitespace tokeniser" sentence, which has been WordPiece since Phase 3.15)
- Modify: `src/Rag.NET.Reranking.Onnx/README.md` (Setup section)

- [ ] **Step 1: Update the three pages**

The models table gains a column for the tokenizer file and option: `vocab.txt` → `VocabPath` for MiniLM, `sentencepiece.bpe.model` → `TokenizerPath` for bge-reranker-v2-m3. Note that the BAAI repo ships no ONNX file, so a community export is needed. Each page adds a `TokenizerPath` setup example next to the `VocabPath` one. The features options table lists `TokenizerPath`, and `VocabPath` is no longer marked required; the table says exactly one of the two is set.

- [ ] **Step 2: Validate the compiled docs fences**

Run:
```bash
rm -rf artifacts/packages
dotnet tool restore
v=$(dotnet dotnet-gitversion /output json | jq -r '.SemVer')
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$v"
dotnet test tests/Rag.NET.PackageValidation.Tests -c Release
```
Expected: all pass, and `EveryPackageCarriesTheVersionGitVersionDerives` runs rather than skips.

- [ ] **Step 3: Commit**

```bash
git add docs/guide/retrieval.md docs/reference/features.md src/Rag.NET.Reranking.Onnx/README.md
git commit -m "docs(reranking): document TokenizerPath and the bge-reranker-v2-m3 setup"
```

## Checks

- build: `dotnet build Rag.NET.slnx -c Release`
- tests: `dotnet test Rag.NET.slnx -c Release`

`dotnet test Rag.NET.slnx` stops at `tests/Rag.NET.Testing`, which is a library. Before pushing, run each `*Tests.csproj` that `dotnet sln Rag.NET.slnx list` lists, then the pack-validate sequence from Task 4 Step 2.
