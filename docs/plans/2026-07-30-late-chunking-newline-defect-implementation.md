# Late Chunking Newline Defect Implementation Plan (Phase 3.13)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make late chunking work on multi-line text, and stop all three ONNX encoders merging words across line breaks.

**Architecture:** Substitute a space for `\n`, `\t` and `\r` in the shared tokenizer plumbing before encoding. Length-preserving, so token offsets stay valid against the original text; and it corrects the token stream, which is the half that would corrupt embeddings even if offsets were fixed. CJK and NFD keep being refused, with a message that names the cause.

**Tech Stack:** .NET 10, `Microsoft.ML.Tokenizers` (`BertTokenizer`), ONNX Runtime, xUnit v3.

**Design:** `docs/plans/2026-07-30-late-chunking-newline-defect-design.md`. Read §1 before writing anything — the probe results there are measured, not assumed, and §1's "worse half" is the reason this is not purely an offsets fix.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matches file), MA0006, MA0008, MA0009, MA0132, MA0140, ZA0601, ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ001/HLQ003/HLQ004 (`foreach (ref readonly var x in span)`), HLQ006, HLQ012, HLQ013, NU1510, RCS1194, CA2022, MA0060. **No new `#pragma` or `SuppressMessage`.**
- All logging through `LoggerMessage` source-gen.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A` or `git add .`** — explicit paths. `.claude/worktrees/` is untracked; leave it.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.

Baselines: `Rag.NET.Embeddings.Onnx.Tests` **124 + 1 skipped**, `Rag.NET.Chunking.IntegrationTests` **2 + 1 skipped**, `Rag.NET.Benchmarks.Quality.Tests` **70**, `Rag.NET.Tests` **1325**, `Rag.NET.Parsers.Archive.Tests` **52**, `Rag.NET.Parsers.Email.Tests` **76**, `Rag.NET.Chunking.Templates.Tests` **51**, `RepoConventions` **9**.

**The model and vocab are provisioned locally** at `C:/Users/MARCEL~1/AppData/Local/Temp/claude/c--Projects-Prive-Rag-NET/2310a96c-be17-4a93-9256-e2770c41c90d/scratchpad/bench/` (`model.onnx`, `vocab.txt`). Set `RAGNET_ONNX_EMBED_MODEL` and `RAGNET_ONNX_EMBED_VOCAB` and **run the gated tests for real** — the entire reason this defect survived is that nobody ever did.

**Timestamp trap:** build without `--no-build` and confirm from the log that projects recompiled before trusting a `--no-build` result.

---

## Task 1: pin the defect, in all three encoders

**Files:**
- Create: `tests/Rag.NET.Embeddings.Onnx.Tests/WhitespaceNormalizationTests.cs`

**Write these first and watch them fail.** Requires the vocab; gate with `Assert.SkipWhen` on `RAGNET_ONNX_EMBED_VOCAB` following `tests/Rag.NET.Chunking.IntegrationTests/LateChunkingIntegrationTests.cs:28-33`.

**The token-merging case is the one that matters most**, because it is wrong output rather than a refusal:

```csharp
// BERT substitutes a space for a newline; this tokenizer deletes it, so the words either side
// merge into one the document never contained. Measured before the fix:
//   "alpha\n\nbeta gamma"  ->  normalized "alphabeta gamma"  ->  tokens: alphabet | ##a | gamma
var tokens = tokenizer.EncodeToTokens("alpha\n\nbeta gamma", out _);
Assert.DoesNotContain(tokens, t => t.Value.StartsWith("alphabet", StringComparison.Ordinal));
```

Also pin, each as its own case:

- `"alpha\nbeta"`, `"alpha\tbeta"`, `"alpha\r\nbeta"` and `"alpha beta\n"` all leave the length unchanged after normalization, so the guard passes.
- **CJK and NFD still refuse** — `"日本語 text"` and `"cafe\u0301 test"`. These are the documented limits; a test asserting they *work* would be asserting the opposite of the design.

**Run. Expected: the merging and whitespace cases FAIL, the CJK and NFD cases PASS.** Report the verbatim failure — the token list is the evidence this task exists to capture.

**Commit:** `test(onnx): pin the newline token-merging defect`

---

## Task 2: substitute whitespace in the shared plumbing

**Files:**
- Modify: `src/Rag.NET.Embeddings.Onnx/BertOnnxPlumbing.cs`
- Modify: `src/Rag.NET.Embeddings.Onnx/OnnxTokenEmbeddingGenerator.cs:120`
- Modify: `src/Rag.NET.Embeddings.Onnx/OnnxSpladeEncoder.cs:126`
- Modify: `src/Rag.NET.Embeddings.Onnx/OnnxEmbeddingGenerator.cs:188`

**All three encoders share this defect, and only one of them trips the guard.** `OnnxSpladeEncoder` and `OnnxEmbeddingGenerator` discard offsets, so neither ever saw a length error — but both tokenize through the same normalizer, so both merge words across line breaks and produce vectors for text the document does not contain. The substitution therefore belongs in `BertOnnxPlumbing`, applied at every `EncodeToTokens` call site, not in the late-chunking path alone.

Replace `\n`, `\t` and `\r` with a single space. **Length-preserving is the whole point** — offsets into the substituted text are then valid offsets into the original, which is what `ITokenEmbeddingGenerator` promises. Do not trim, collapse runs, or touch any other character: each of those changes the length and reintroduces exactly the bug being fixed. A test should pin that the substituted string has the same length as its input.

Return the substituted string so the caller passes the *same* string to both the tokenizer and any offset consumer; a substitution applied to one and not the other is worse than none.

**Run Task 1's tests. Expected: all pass**, including the CJK and NFD refusals, which must be unchanged.

**Then verify the parity number did not move.** SciFact contains no newlines or tabs — I verified this by parsing the JSON rather than grepping, and a first grep gave a plausible-looking 100% that was an artefact of `\t` meaning `t` in basic regex. So this change must leave nDCG@10 at **0.64593**. Run `tests/Rag.NET.Benchmarks.Quality.IntegrationTests` with the three environment variables and confirm. If it moves, stop and report — something is being substituted that should not be.

**Commit:** `fix(onnx): substitute a space for newlines rather than deleting them`

---

## Task 3: give the guard a message that names the cause, and tests

**Files:**
- Modify: `src/Rag.NET.Embeddings.Onnx/OnnxTokenEmbeddingGenerator.cs:205-215`
- Create or extend: `tests/Rag.NET.Embeddings.Onnx.Tests/NormalizationGuardTests.cs`

**The guard has no test coverage at all** — nothing anywhere calls `ThrowIfNormalizationChangedLength`. That is how a guard which silently disabled a feature for months survived: the unit tests exercise a `WindowRunner` seam that never reaches the tokenizer, and the one integration test that would have caught it could not run.

Today's message reports a length change and suggests stripping control characters. After Task 2 that advice is stale, and the three remaining causes are different problems: **CJK** (normalization inserts spaces and the text grows), **NFD** (combining marks are stripped and it shrinks), and anything else length-changing. Rewrite it to say which direction the length moved and name the likely cause, so a reader knows whether they are looking at a Japanese corpus or a macOS-normalized filename.

Test the guard directly: equal lengths pass, a shorter normalization throws, a longer one throws, a null normalized string passes. Then test it through the real tokenizer for CJK and NFD, asserting the message names the cause.

**Commit:** `fix(onnx): the normalization guard names what changed the length`

---

## Task 4: the end-to-end proof, and nightly back to green

**Files:**
- Verify: `tests/Rag.NET.Chunking.IntegrationTests/LateChunkingIntegrationTests.cs`

**This is the test that has never run.** Its fixture is two paragraphs separated by `\n\n`, which is exactly the shape that fails today. Run it with the model and vocab set.

**Expected: PASS**, with every chunk carrying a non-null embedding of consistent dimension and unit length.

Do not edit the test to make it pass. If it fails, the fix is incomplete — report what you observed. Editing this assertion would recreate the condition that hid the defect in the first place.

Add one case to it: a document containing a **tab** as well as newlines, since `\t` is a separate cause with the same fix and nothing else covers it.

**Commit:** `test(chunking): late chunking works on multi-paragraph text`

---

## Task 5: documentation

**Files:**
- Modify: `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`
- Modify: whichever guide documents late chunking — **find it, do not assume**

Flip Phase 3.13 to `[status: complete]` with a `**Completed:**` paragraph. Record what the phase found beyond its brief: that the defect was five times broader than the debt entry said, that it corrupted tokens rather than only offsets, and that `OnnxSpladeEncoder` and `OnnxEmbeddingGenerator` shared it without ever tripping the guard.

**Document the remaining limits precisely** in the guide: late chunking refuses CJK and NFD-normalized text, and says why. A limitation a user can read is a different thing from one they discover.

**`MILESTONE.md` is the step this project keeps losing** — 3.10 and 3.7 both shipped with it left at `[pending]` because each plan deferred it to after the review and no fix loop went back. Flip it in the same commit as the ROADMAP rather than deferring it.

**Commit:** `docs: close the late chunking newline defect`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. Every baseline holds; the ONNX and chunking suites gain tests and **lose their skips** when the model is set.
3. SciFact parity still **0.64593**.
4. No new `#pragma` or `SuppressMessage`.
5. `LateChunkingIntegrationTests` passes with a real model — the assertion nightly depends on.

**Report:** every commit hash, verbatim build and test output, the token list Task 1 captured before the fix, the parity number after Task 2, and everything this plan got wrong. That last item is not a formality — this phase exists because a guard nobody tested silently disabled a feature nobody could see, and the design's scope was found to be five times off by probing rather than reasoning.
