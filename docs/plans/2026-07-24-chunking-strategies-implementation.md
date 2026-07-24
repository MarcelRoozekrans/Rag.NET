# Chunking Strategies Implementation Plan (Phase 1.1)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the three backlog chunking features: sliding-window baseline (TokenAware upgrade), proposition-extraction chunking, and true late chunking with a token-level embedding abstraction + ONNX implementation.

**Architecture:** Per `docs/plans/2026-07-24-chunking-strategies-design.md`. Three independent parts: (A) `TokenAwareChunkingOptions` + strategy/DI overloads; (B) `TextChunk.Embedding` field + `EmbeddingBehavior` skip guard; (C) `PropositionChunkingStrategy` (IChatClient, parent metadata); (D) `ITokenEmbeddingGenerator` + `LateChunkingStrategy` + `Rag.NET.Embeddings.Onnx`. Parts A–C have no dependency on each other; D depends on B.

**Tech Stack:** .NET 10, xUnit v3 + NSubstitute, Microsoft.ML.Tokenizers (Tiktoken + Bert), Microsoft.ML.OnnxRuntime, Microsoft.Extensions.AI (`IChatClient`, `IEmbeddingGenerator`).

**Conventions (read first):**
- Tests live in `tests/Rag.NET.Tests/Chunking/` and `tests/Rag.NET.Tests/DependencyInjection/`; run with `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~<TestClass>"`.
- Options POCOs live in `src/Rag.NET.Abstractions/Models/Options/` (init-only, validated in the `Use*` extension — `SemanticChunkingOptions` pattern).
- LLM-calling strategy precedent: `src/Rag.NET.Chunking.Templates/ResumeChunkingStrategy.cs` (primary ctor, `options.ChatClient ?? chatClient`, LoggerMessage warnings, fallback on parse failure).
- Delimiter fencing for LLM prompts: v4 GUID suffix, see `src/Rag.NET.QueryTechniques/ContextualCompression/LlmAbstractiveCompressor.cs:80-92`.
- Parent-key convention (core `Rag.NET`, internal): `_parentKey` = `"{documentId}:{parentIndex}"` (`src/Rag.NET/Ingestion/ParentChunkKeyHelper.cs`). Proposition chunks must set `StartPosition`/`EndPosition` to the source-passage char span so `ParentDocumentIngestionBehavior` maps them to parents; they additionally write human-readable `parent.start`/`parent.end` metadata.
- Commit after every task with the message given in the task.

---

## Part A — Sliding Window (TokenAware upgrade)

### Task A1: `TokenAwareChunkingOptions`

**Files:**
- Create: `src/Rag.NET.Abstractions/Models/Options/TokenAwareChunkingOptions.cs`
- Test: `tests/Rag.NET.Tests/Chunking/TokenAwareChunkingOptionsTests.cs` (new)

**Step 1: Write the failing test**

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class TokenAwareChunkingOptionsTests
{
    [Fact]
    public void Defaults_AreExpected()
    {
        var o = new TokenAwareChunkingOptions();
        Assert.Equal("gpt-4", o.ModelName);
        Assert.Null(o.WindowSizeTokens);
        Assert.Null(o.OverlapTokens);
    }
}
```

**Step 2: Run** `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~TokenAwareChunkingOptionsTests"` → FAIL (type not found).

**Step 3: Implement**

```csharp
namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>TokenAwareChunkingStrategy</c> — the sliding-window baseline.
/// When <see cref="WindowSizeTokens"/> / <see cref="OverlapTokens"/> are null the strategy
/// falls back to <see cref="ChunkingOptions.MaxChunkSize"/> / <see cref="ChunkingOptions.Overlap"/>
/// interpreted as token counts.
/// </summary>
public sealed class TokenAwareChunkingOptions
{
    public string ModelName { get; init; } = "gpt-4";
    public int? WindowSizeTokens { get; init; }
    public int? OverlapTokens { get; init; }
}
```

**Step 4: Run test** → PASS.
**Step 5: Commit** `feat(chunking): add TokenAwareChunkingOptions`

### Task A2: strategy consumes options

**Files:**
- Modify: `src/Rag.NET.Chunking.TokenAware/TokenAwareChunkingStrategy.cs`
- Test: `tests/Rag.NET.Tests/Chunking/TokenAwareChunkingStrategyTests.cs` (append)

**Step 1: Failing tests** — append to the existing test class:

```csharp
[Fact]
public async Task ChunkAsync_OptionsWindowOverridesChunkingOptions()
{
    var strategy = new TokenAwareChunkingStrategy(new TokenAwareChunkingOptions
    {
        WindowSizeTokens = 4,
        OverlapTokens = 1,
    });
    var section = new DocumentSection { Text = string.Join(' ', Enumerable.Repeat("word", 20)), DocumentId = new DocumentId("d") };
    // ChunkingOptions says 512/50 — the options-class values must win.
    var chunks = await strategy.ChunkAsync(section, new ChunkingOptions()).ToListAsync(TestContext.Current.CancellationToken);
    Assert.True(chunks.Count > 2);
}

[Fact]
public void Ctor_OverlapGreaterOrEqualWindow_Throws()
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        new TokenAwareChunkingStrategy(new TokenAwareChunkingOptions { WindowSizeTokens = 4, OverlapTokens = 4 }));
}
```

**Step 2: Run** → FAIL (no such ctor).

**Step 3: Implement** — add to `TokenAwareChunkingStrategy`:
- Field `private readonly TokenAwareChunkingOptions? _options;`
- New ctor `public TokenAwareChunkingStrategy(TokenAwareChunkingOptions options)`: validate `options.WindowSizeTokens is > 0 or null`, `OverlapTokens is >= 0 or null`, and when both set `OverlapTokens < WindowSizeTokens` (throw `ArgumentOutOfRangeException`); set `ModelName = options.ModelName`, create tokenizer, store `_options`.
- In `ChunkAsync`, resolve effective values at the top: `var window = _options?.WindowSizeTokens ?? options.MaxChunkSize; var overlap = _options?.OverlapTokens ?? options.Overlap;` and use `window`/`overlap` everywhere `options.MaxChunkSize`/`options.Overlap` were used (including the existing overlap-vs-size guard).
- Existing `(string modelName)` ctor delegates: `: this(new TokenAwareChunkingOptions { ModelName = modelName })` (keep the ThrowIfNullOrWhiteSpace on modelName first).

**Step 4: Run full class** `--filter "FullyQualifiedName~TokenAwareChunkingStrategyTests"` → PASS (old tests must stay green — they use the string ctor).
**Step 5: Commit** `feat(chunking): TokenAwareChunkingStrategy accepts TokenAwareChunkingOptions`

### Task A3: DI overload

**Files:**
- Modify: `src/Rag.NET.Chunking.TokenAware/RagBuilderExtensions.cs`
- Test: `tests/Rag.NET.Tests/DependencyInjection/UseTokenAwareChunkingTests.cs` (append)

**Step 1: Failing test** — resolve `IChunkingStrategy` after `UseTokenAwareChunking(o => { o.WindowSizeTokens = 128; o.OverlapTokens = 16; })`, assert it is `TokenAwareChunkingStrategy`. Follow the existing test file's builder setup verbatim.
**Step 2: Run** → FAIL.
**Step 3: Implement** overload:

```csharp
public static TBuilder UseTokenAwareChunking<TBuilder>(this TBuilder builder, Action<TokenAwareChunkingOptions>? configure)
    where TBuilder : IRagBuilder
{
    var options = new TokenAwareChunkingOptions();
    configure?.Invoke(options);
    builder.Services.AddSingleton<IChunkingStrategy>(_ => new TokenAwareChunkingStrategy(options));
    return builder;
}
```

(Ctor validation covers option errors; no duplicate validation here.)
**Step 4: Run** → PASS.
**Step 5: Commit** `feat(chunking): UseTokenAwareChunking(Action<TokenAwareChunkingOptions>) overload`

### Task A4: docs + feature tick

**Files:** `docs/guide/chunking.md` (TokenAware section → present as sliding-window baseline, show new overload), `docs/reference/features.md` (tick "Sliding Window Chunking with Overlap" row 1017; in the detail section 736-741 note it is delivered by `Rag.NET.Chunking.TokenAware`).
**Commit** `docs(chunking): document TokenAware as the sliding-window baseline; tick feature`

---

## Part B — Precomputed embeddings plumbing

### Task B1: `TextChunk.Embedding`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/TextChunk.cs`

Add one property (no test needed beyond compilation — covered by B2):

```csharp
/// <summary>
/// Optional precomputed embedding (e.g. from late chunking). When set,
/// <c>EmbeddingBehavior</c> uses it verbatim instead of re-embedding the chunk text.
/// </summary>
public ReadOnlyMemory<float>? Embedding { get; init; }
```

**Commit** `feat(models): optional precomputed Embedding on TextChunk`

### Task B2: `EmbeddingBehavior` skip guard

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs`
- Test: `tests/Rag.NET.Tests/Ingestion/Behaviors/EmbeddingBehaviorTests.cs` (append or create alongside existing behavior tests — check for an existing file first)

**Step 1: Failing tests** (NSubstitute embedder; build an `IngestionContext` the way existing behavior tests do — copy their setup helper):

```csharp
[Fact]
public async Task HandleAsync_PrecomputedEmbeddings_AreNotReEmbedded_AndOrderIsPreserved()
{
    // chunk0: precomputed, chunk1: plain, chunk2: precomputed
    // Assert: embedder receives exactly ["plain text"];
    // EmbeddedChunks[i].Chunk.ChunkIndex == i for all i;
    // EmbeddedChunks[0].Embedding equals the precomputed vector.
}

[Fact]
public async Task HandleAsync_AllPrecomputed_EmbedderNeverCalled()
{
    // embedder.DidNotReceive().GenerateAsync(...)
}
```

**Step 2: Run** → FAIL.

**Step 3: Implement** — replace the body's embed-and-zip section:

```csharp
var pending = new List<(int Index, string Text)>();
for (var i = 0; i < ctx.Chunks.Count; i++)
    if (ctx.Chunks[i].Embedding is null)
        pending.Add((i, ctx.Chunks[i].Text));

// telemetry: keep chunk.count = total; add chunk.precomputed = total - pending.Count
GeneratedEmbeddings<Embedding<float>>? generated = null;
if (pending.Count > 0)
{
    // existing try/finally + stopwatch around Embedder.GenerateAsync(pending texts)
}

var byIndex = new ReadOnlyMemory<float>[ctx.Chunks.Count];
for (var i = 0; i < ctx.Chunks.Count; i++)
    if (ctx.Chunks[i].Embedding is { } pre) byIndex[i] = pre;
for (var p = 0; p < pending.Count; p++)
    byIndex[pending[p].Index] = generated![p].Vector;

for (var i = 0; i < ctx.Chunks.Count; i++)
    ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = ctx.Chunks[i], Embedding = byIndex[i] });
```

Skip the stopwatch/`GenerateAsync` entirely when `pending.Count == 0` (record 0ms is fine; embedder must not be called).
**Step 4: Run** → PASS. Also run the full `Rag.NET.Tests` suite — ingestion tests must stay green.
**Step 5: Commit** `feat(ingestion): EmbeddingBehavior honors precomputed chunk embeddings`

---

## Part C — Proposition Extraction

### Task C1: options

**Files:**
- Create: `src/Rag.NET.Abstractions/Models/Options/PropositionChunkingOptions.cs`
- Test: `tests/Rag.NET.Tests/Chunking/PropositionChunkingStrategyTests.cs` (new; defaults test first)

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

/// <summary>Options for proposition-extraction chunking.</summary>
public sealed class PropositionChunkingOptions
{
    /// <summary>Max tokens (cl100k_base) per passage sent to the LLM.</summary>
    public int MaxPassageTokens { get; init; } = 1000;
    /// <summary>Safety cap on propositions parsed per passage.</summary>
    public int MaxPropositionsPerPassage { get; init; } = 50;
    /// <summary>Also emit each source passage as its own chunk (for dual-index setups).</summary>
    public bool EmitParentPassages { get; init; }
    /// <summary>Optional dedicated chat client; falls back to the DI-registered one.</summary>
    public IChatClient? ChatClient { get; init; }
}
```

TDD steps as in A1 (defaults test → implement → pass). **Commit** `feat(chunking): add PropositionChunkingOptions`

### Task C2: strategy — passage windowing + LLM + fallback

**Files:**
- Create: `src/Rag.NET.Chunking/PropositionChunkingStrategy.cs`
- Modify: `src/Rag.NET.Chunking/Rag.NET.Chunking.csproj` — ensure `Microsoft.ML.Tokenizers` + `Microsoft.ML.Tokenizers.Data.Cl100kBase` package refs (copy versions from `src/Rag.NET/Rag.NET.csproj`); `Microsoft.Extensions.AI.Abstractions` comes via Abstractions.
- Test: `tests/Rag.NET.Tests/Chunking/PropositionChunkingStrategyTests.cs`

**Step 1: Failing tests** (NSubstitute `IChatClient`, model on `LlmAbstractiveCompressorTests` for the substitute setup):

```csharp
// 1. WellFormedResponse_OnePropositionPerChunk_WithParentMetadata:
//    LLM returns ["Fact one.","Fact two."] → 2 chunks; chunk.Text == proposition;
//    Metadata["parent.start"]/"parent.end" parse to the passage span;
//    StartPosition/EndPosition == passage span; ChunkIndex increments.
// 2. MalformedJson_FallsBackToPassageChunk: LLM returns "not json" → 1 chunk == passage text.
// 3. LlmThrows_FallsBackToPassageChunk (chat client throws InvalidOperationException).
// 4. EmptyAndWhitespacePropositions_AreDropped: ["", "  ", "Real."] → 1 chunk.
// 5. CapRespected: 60 propositions returned, MaxPropositionsPerPassage=50 → 50 chunks.
// 6. EmitParentPassages_True_EmitsPassageChunkFirst with Metadata["chunk.kind"]=="passage".
// 7. LongDocument_SplitsIntoMultiplePassages: text > MaxPassageTokens (use tiny MaxPassageTokens=8)
//    → chat client called once per passage.
// 8. Cancellation propagates (pre-cancelled token → OperationCanceledException).
```

**Step 2: Run** → FAIL.

**Step 3: Implement** `PropositionChunkingStrategy` (`IDocumentChunkingStrategy`, primary ctor `(IChatClient chatClient, PropositionChunkingOptions options, ILogger<PropositionChunkingStrategy>? logger = null)`):

- Concatenate sections (ResumeChunkingStrategy lines 25-35 pattern), remembering each section's char offset.
- Passage split: `TiktokenTokenizer.CreateForEncoding("cl100k_base")` (static readonly), encode full text, split token IDs into windows of `MaxPassageTokens` (no overlap), decode each window back to text; track each passage's char span via running `IndexOf`-free accumulation: decode lengths are exact since windows partition the token sequence — accumulate `start += passageText.Length` is NOT reliable after Trim; instead keep the untrimmed decode for span math and trim only for the chunk text.
- Per passage: build prompt with v4-GUID fenced content (LlmAbstractiveCompressor pattern):

```text
System: You decompose text into atomic propositions for a retrieval system. Each proposition is a
single, self-contained factual claim expressed as one complete sentence, understandable without
the surrounding text (resolve pronouns). Return ONLY a JSON array of strings — no markdown,
no commentary.
User: <content-{delim}>
{passage}
</content-{delim}>
```

- Parse with `JsonNode.Parse` → `JsonArray` of strings (use `TryGetString` guard like ResumeChunkingStrategy); cap at `MaxPropositionsPerPassage`; drop null/whitespace entries.
- Per-passage `try/catch (OperationCanceledException) { throw; } catch (Exception)` → LoggerMessage warning + fallback: yield the passage itself as one chunk.
- Chunk shape: `Text` = proposition (or passage on fallback), `DocumentId`, global `ChunkIndex` counter, `StartPosition`/`EndPosition` = passage char span, `Metadata` = `{ ["parent.start"], ["parent.end"], ["chunk.kind"] = "proposition" | "passage" }`.
- `EmitParentPassages`: yield the passage chunk (kind `passage`) before its propositions.
- NOTE: an LLM call inside `try` cannot contain `yield return` — collect per-passage results into a `List<TextChunk>` inside the try, yield after.

**Step 4: Run class** → PASS.
**Step 5: Commit** `feat(chunking): add PropositionChunkingStrategy`

### Task C3: DI + docs

**Files:**
- Modify: `src/Rag.NET.Chunking/RagBuilderExtensions.cs` — add `UsePropositionChunking<TBuilder>(Action<PropositionChunkingOptions>? configure = null)`: build options, validate (`MaxPassageTokens > 0`, `MaxPropositionsPerPassage > 0`, throw `ArgumentOutOfRangeException` otherwise), register singleton `PropositionChunkingStrategy` and alias `IChunkingStrategy` + `IDocumentChunkingStrategy` to it (Semantic aliasing pattern — propositions per-section works fine: ChunkDocumentAsync over a single-section stream; add a thin `IChunkingStrategy.ChunkAsync` implementation on the strategy that wraps the section in a one-element async stream).
- Test: `tests/Rag.NET.Tests/DependencyInjection/UsePropositionChunkingTests.cs` (new, copy `UseSemanticChunkingTests` shape; register a substitute `IChatClient` first).
- Docs: `docs/guide/chunking.md` new `## PropositionChunkingStrategy` section; features.md tick row 1033.

TDD steps as A3. **Commit** `feat(chunking): UsePropositionChunking DI extension + docs`

---

## Part D — Late Chunking

### Task D1: `ITokenEmbeddingGenerator` abstraction

**Files:**
- Create: `src/Rag.NET.Abstractions/Abstractions/ITokenEmbeddingGenerator.cs`
- Create: `src/Rag.NET.Abstractions/Models/TokenEmbeddingResult.cs`

Exact shapes from the design doc §3a (record with `Embeddings` row-major, `Dimension`, `TokenOffsets`). Compilation-only (consumed by D2 tests). **Commit** `feat(abstractions): ITokenEmbeddingGenerator for token-level embeddings`

### Task D2: `LateChunkingStrategy`

**Files:**
- Create: `src/Rag.NET.Abstractions/Models/Options/LateChunkingOptions.cs` (`WindowSizeTokens = 256`, `OverlapTokens = 32`, `ITokenEmbeddingGenerator? Generator`)
- Create: `src/Rag.NET.Chunking/LateChunkingStrategy.cs`
- Test: `tests/Rag.NET.Tests/Chunking/LateChunkingStrategyTests.cs` (new)

**Step 1: Failing tests** with a deterministic fake generator (NOT NSubstitute — write a tiny `FakeTokenEmbeddingGenerator` in the test file that tokenizes by whitespace, returns per-token vectors you choose, offsets = word char spans):

```csharp
// 1. Windows_MapBackToText: 10 words, window 4, overlap 1 → chunk texts are the exact
//    substrings spanning each window's first token start .. last token end.
// 2. MeanPooling_IsCorrect_AndL2Normalized: 2 tokens with vectors (1,0),(0,1), window 2
//    → chunk embedding == (0.7071.., 0.7071..) within 1e-4.
// 3. GeneratorFails_FallsBackToUnembeddedTokenWindows: generator throws →
//    chunks still produced (via cl100k token windows), all with Embedding == null.
// 4. EmptySection_YieldsNothing.
// 5. ChunkIndexes_AreSequential_AcrossSections.
```

**Step 2: Run** → FAIL.

**Step 3: Implement** `LateChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy` (primary ctor `(ITokenEmbeddingGenerator generator, LateChunkingOptions options, ILogger<LateChunkingStrategy>? logger = null)`):

- Per section: `var gen = options.Generator ?? generator;` → `GenerateAsync(section.Text)`.
- Token windows over `result.TokenOffsets.Count` with `WindowSizeTokens`/`OverlapTokens` (validate overlap < window in ctor).
- Chunk text: `section.Text[offsets[first].Start .. offsets[last].End]`, trimmed.
- Embedding: mean over rows `first..last` of the row-major matrix, then L2-normalize; slice rows with `result.Embeddings.Span.Slice(row * result.Dimension, result.Dimension)`.
- Fallback on generator exception (log warning): reuse token-window text chunking via `TiktokenTokenizer` cl100k (same helper as propositions — extract a shared internal static `TokenWindowSplitter` in `Rag.NET.Chunking` used by both C2 and here, DRY).
- Set `StartPosition`/`EndPosition` to the char span.

**Step 4: Run** → PASS.
**Step 5: Commit** `feat(chunking): add LateChunkingStrategy (token-level late chunking)`

### Task D3: DI for late chunking

**Files:**
- Modify: `src/Rag.NET.Chunking/RagBuilderExtensions.cs` — `UseLateChunking<TBuilder>(Action<LateChunkingOptions>? configure = null)`: validate, register singleton + alias both interfaces; resolves `ITokenEmbeddingGenerator` from DI (`sp.GetRequiredService`), so registration order doesn't matter.
- Test: `tests/Rag.NET.Tests/DependencyInjection/UseLateChunkingTests.cs` — happy path (fake generator registered) + failure: resolving `IChunkingStrategy` without any `ITokenEmbeddingGenerator` throws `InvalidOperationException`.

TDD steps as A3. **Commit** `feat(chunking): UseLateChunking DI extension`

### Task D4: `Rag.NET.Embeddings.Onnx` project scaffold

**Files:**
- Create: `src/Rag.NET.Embeddings.Onnx/Rag.NET.Embeddings.Onnx.csproj` — copy `src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj` verbatim, change RootNamespace/PackageId to `Rag.NET.Embeddings.Onnx`, Description `ONNX Runtime token-level embeddings for Rag.NET (late chunking)`, InternalsVisibleTo `Rag.NET.Embeddings.Onnx.Tests`. Keep the `Microsoft.ML.OnnxRuntime` version identical to Reranking.Onnx; add `Microsoft.ML.Tokenizers` (same version as elsewhere).
- Modify: `Rag.NET.slnx` — add the project next to `Rag.NET.Reranking.Onnx` (same folder grouping); also add the test project from D6.
- Verify: `dotnet build src/Rag.NET.Embeddings.Onnx` → 0 errors.

**Commit** `chore(embeddings): scaffold Rag.NET.Embeddings.Onnx project`

### Task D5: `OnnxTokenEmbeddingGenerator`

**Files:**
- Create: `src/Rag.NET.Embeddings.Onnx/OnnxTokenEmbeddingOptions.cs` — `ModelPath` (required), `TokenizerVocabPath` (required, BERT/WordPiece vocab like Reranking.Onnx), `MaxTokens = 8192`, `WindowOverlapTokens = 64` (for internal stitching of over-long inputs).
- Create: `src/Rag.NET.Embeddings.Onnx/OnnxTokenEmbeddingGenerator.cs`
- Test: `tests/Rag.NET.Embeddings.Onnx.Tests/` (new project — copy `tests/Rag.NET.Reranking.Onnx.Tests/` csproj, adjust name/refs)

**Design for testability:** split the class into (a) a pure internal `static TokenWindowStitcher` — given token count, `MaxTokens`, overlap → list of (start,end) windows, and given per-window embedding matrices → stitched full matrix (overlap region: keep the later window's vectors, they carry more right-context; document this choice) — and (b) the ONNX session wrapper. Unit-test (a) exhaustively without ONNX; (b) follows `OnnxReranker` (ctor validates file paths, lazy `InferenceSession`, `Microsoft.ML.Tokenizers.BertTokenizer.Create(vocabPath)` with `EncodeToTokens` for offsets, inputs `input_ids`/`attention_mask`/`token_type_ids`, output = last_hidden_state `[1, tokens, dim]` → copy to row-major float[]).

**Step 1: Failing tests** for `TokenWindowStitcher` (window math: exact cover, overlap handling, single-window short input; stitch: overlap rows come from later window).
**Step 2-4:** standard TDD; ONNX wrapper itself gets only ctor-validation tests (missing files throw `FileNotFoundException`) — no model needed.
**Step 5: Commit** `feat(embeddings): OnnxTokenEmbeddingGenerator with windowed stitching`

### Task D6: ONNX DI + integration smoke + docs

**Files:**
- Create: `src/Rag.NET.Embeddings.Onnx/RagBuilderExtensions.cs` — `UseOnnxTokenEmbeddings<TBuilder>(Action<OnnxTokenEmbeddingOptions> configure)`: require configure, validate paths non-empty, register `ITokenEmbeddingGenerator` singleton.
- Test: DI test in `tests/Rag.NET.Embeddings.Onnx.Tests/` (registration resolves when files exist — use two temp files; ctor file checks make real paths necessary, so write dummy files and assert `FileNotFoundException` is NOT thrown at registration time but AT resolution — match OnnxReranker's registration timing).
- Create: integration smoke test in `tests/Rag.NET.Chunking.IntegrationTests/LateChunkingIntegrationTests.cs` — `[Fact]` that Skips (`Assert.Skip`) when env var `RAGNET_ONNX_EMBED_MODEL` is unset; otherwise runs LateChunkingStrategy end-to-end over a paragraph and asserts non-null, correct-dimension embeddings. Follow the existing optional-asset skip pattern in that project.
- Docs: `docs/guide/chunking.md` `## LateChunkingStrategy` section (concept, options, ONNX setup incl. pointing at jina-embeddings-v2 ONNX export on HF); features.md tick row 1049 with packages `Rag.NET.Chunking` + `Rag.NET.Embeddings.Onnx`.

**Commit** `feat(embeddings): UseOnnxTokenEmbeddings DI + late-chunking integration smoke + docs`

---

## Final verification (after all parts)

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Full unit sweep: `dotnet test tests/Rag.NET.Tests` + the per-package test projects touched (`Rag.NET.Embeddings.Onnx.Tests`).
3. features.md: exactly three rows newly ticked.
4. `docs/planning/ROADMAP.md` + `MILESTONE.md`: mark Phase 1.1 complete.
5. Request code review (superpowers:requesting-code-review) over the phase's commit range.
