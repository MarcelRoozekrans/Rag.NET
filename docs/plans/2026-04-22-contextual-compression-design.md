# Contextual Compression — Design

**Status:** Approved (brainstorming complete — ready for implementation plan)
**Package:** `Rag.NET.QueryTechniques`
**Backlog entry:** `docs/reference/features.md` — *Contextual Compression*
**Date:** 2026-04-22

---

## Goal

Post-retrieval compression of each retrieved chunk to only the content relevant to the query, reducing prompt tokens without reducing answer quality. Supports both a zero-LLM extractive mode (embedding similarity, local) and an abstractive LLM mode (parallel per-chunk rewrite).

## Non-goals (v1)

- Global prompt budget allocation across chunks — per-chunk cap only. Global budget is a v2 enhancement once the abstraction is stable.
- LLM-as-extractor (return-relevant-sentences-verbatim-via-LLM) — extractive + abstractive is the right coverage. Can be added as a third strategy later.
- Retry, circuit-breaker, or rate-limiting logic on the compression LLM/embedding calls. Users wrap their `IChatClient` / `IEmbeddingGenerator` with resilience middleware at the Microsoft.Extensions.AI pipeline level; compression does not layer its own retry.
- LLM output-quality evaluation. That is the `Rag.NET.Evaluation` package's job.

---

## Architecture

A new `IContextualCompressor` abstraction invoked by default inside `ChatAnswerEngine` **before** prompt building. Two shipped implementations:

- `ExtractiveCompressor` — embedding similarity, local, zero LLM calls.
- `LlmAbstractiveCompressor` — one `IChatClient` call per chunk, parallel via `Task.WhenAll`.

Output is **non-destructive**: compressed text lives on a new `SearchResult.CompressedText` property. The answer engine reads `CompressedText ?? Chunk.Text`. `Chunk.Text` is never mutated.

**Registration entry point:** `builder.UseContextualCompression(opts => ...)` on `IRagBuilder`. Strategy is selected via options; exactly one stopping criterion (`KeepTopSentences` or `MaxTokensPerChunk`) is required.

**Optional retrieval-pipeline opt-in:** `ContextualCompressionRetrievalBehavior` wraps the same `IContextualCompressor` and runs inside the retrieval pipeline, exposed as `UseContextualCompressionInRetrieval()`. Not registered by default. Users who want compressed text from plain `RetrieveAsync` (e.g., feeding a non-Rag.NET consumer) opt in explicitly.

**Layering principle (why `IContextualCompressor` is separate from retrieval behaviors):**
Compression is fundamentally an LLM-context concern, not a retrieval concern. Raw `RetrieveAsync` results should stay pristine so UIs can display verbatim source text. Mirrors the existing pattern where `UsePromptHardening` is answer-engine-only and `UseRetrievalGuard` is retrieval-only.

---

## Components

### New types in `Rag.NET.QueryTechniques`

```csharp
public interface IContextualCompressor
{
    ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default);
}

public enum ContextualCompressionStrategy { Extractive, Abstractive }

public sealed class ContextualCompressionOptions
{
    public ContextualCompressionStrategy Strategy { get; set; }
        = ContextualCompressionStrategy.Extractive;

    /// <summary>Keep the top-N most relevant sentences per chunk.</summary>
    /// <remarks>Exactly one of <see cref="KeepTopSentences"/> or
    /// <see cref="MaxTokensPerChunk"/> must be set. If both are set,
    /// <see cref="KeepTopSentences"/> wins.</remarks>
    public int? KeepTopSentences { get; set; } = 3;

    /// <summary>Soft cap — keep highest-scoring sentences until the cap is reached.
    /// Measured with the same tokenizer used elsewhere in Rag.NET (cl100k_base).</summary>
    public int? MaxTokensPerChunk { get; set; }
}

public sealed class ExtractiveCompressor(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ContextualCompressionOptions options,
    ILogger<ExtractiveCompressor>? logger = null) : IContextualCompressor;

public sealed class LlmAbstractiveCompressor(
    IChatClient chatClient,
    ContextualCompressionOptions options,
    ILogger<LlmAbstractiveCompressor>? logger = null) : IContextualCompressor;

public sealed partial class ContextualCompressionRetrievalBehavior(
    IContextualCompressor compressor,
    ILogger<ContextualCompressionRetrievalBehavior>? logger = null)
    : IRetrievalBehavior;
```

### Modified type in `Rag.NET.Abstractions`

```csharp
public sealed record SearchResult
{
    public required TextChunk Chunk { get; init; }
    public required double Score { get; init; }

    /// <summary>Compressed-for-LLM view of <see cref="TextChunk.Text"/>.
    /// <see langword="null"/> when no compression was applied.
    /// Answer engines prefer this over <see cref="TextChunk.Text"/>
    /// when non-null.</summary>
    public string? CompressedText { get; init; }
}
```

### Modified type in `Rag.NET`

`ChatAnswerEngine.BuildMessagesAsync`:
```csharp
// Before: $"[Source {i + 1}]\n{s.Chunk.Text}"
// After:  $"[Source {i + 1}]\n{s.CompressedText ?? s.Chunk.Text}"
```

`ChatAnswerEngine` gains an optional `IContextualCompressor? compressor = null` constructor parameter. When non-null and `RagOptions.SkipCompression == false`, it is invoked before `BuildMessagesAsync`.

### New `RagOptions` flag

```csharp
public sealed class RagOptions
{
    // ... existing members ...

    /// <summary>Bypass contextual compression for this call even when an
    /// <c>IContextualCompressor</c> is registered. Use when raw source
    /// text is required (admin tooling, UI citation rendering).</summary>
    public bool SkipCompression { get; set; }
}
```

### New extensions in `Rag.NET.QueryTechniques.RagBuilderExtensions`

```csharp
public static TBuilder UseContextualCompression<TBuilder>(
    this TBuilder builder,
    Action<ContextualCompressionOptions>? configure = null)
    where TBuilder : IRagBuilder;

public static TBuilder UseContextualCompressionInRetrieval<TBuilder>(
    this TBuilder builder)
    where TBuilder : IRagBuilder;
```

`UseContextualCompression`:
1. Registers `ContextualCompressionOptions` (validates at registration time).
2. Registers the selected strategy implementation as `IContextualCompressor`.
3. Wires `IContextualCompressor` into `ChatAnswerEngine` via the existing `EnsureChatAnswerEngine` decorator pattern used by `UsePromptHardening`.

`UseContextualCompressionInRetrieval`:
- Requires `UseContextualCompression` to have been called first (throws `InvalidOperationException` otherwise — no silent no-op).
- Inserts `ContextualCompressionRetrievalBehavior` into the retrieval pipeline via `pipelineBuilder.Add<T>(before: typeof(RetrievalGuardBehavior))`.

---

## Data flow

### Default path — `AskAsync` with extractive compression

```
pipeline.AskAsync("query", opts)
 └─ RagPipeline.AskAsync
      ├─ retriever.RetrieveAsync → IReadOnlyList<SearchResult>
      └─ answerEngine.AskAsync
           ├─ IContextualCompressor.CompressAsync(sources, query)
           │    ├─ for each SearchResult (parallel):
           │    │    ├─ split Chunk.Text into sentences (regex: [.!?] + whitespace)
           │    │    ├─ embed all sentences in one batch call
           │    │    ├─ embed query (per-CompressAsync call)
           │    │    ├─ score each sentence (cosine similarity)
           │    │    ├─ Top-N → keep top KeepTopSentences, preserve original order
           │    │    ├─ Token-budget → keep highest-scoring sentences until cap hit
           │    │    └─ emit SearchResult { Chunk unchanged, CompressedText = "..." }
           │    └─ returns sources with CompressedText populated
           └─ BuildMessagesAsync uses CompressedText ?? Chunk.Text
```

### Default path — abstractive compression (same shape, different `CompressAsync` body)

```
IContextualCompressor.CompressAsync
 └─ Task.WhenAll over sources:
      for each SearchResult:
        └─ chatClient.GetResponseAsync(prompt:
              "Compress the following content to retain only information
               relevant to the query. Target: ≤{MaxTokensPerChunk} tokens.

               Query: {query}
               Content: {Chunk.Text}")
        └─ CompressedText = result.Text.Trim()
```

### Opt-in path — compression inside retrieval pipeline

```
pipeline.RetrieveAsync(query, opts)
 └─ RagPipeline.RetrieveAsync
      └─ retriever.RetrieveAsync
           └─ (pipeline execution, outermost first)
                SelfQuery → ... → Reranking
                → ContextualCompressionRetrievalBehavior      ← NEW (opt-in)
                → RetrievalGuard → ... → VectorStore
 └─ returns IReadOnlyList<SearchResult> with CompressedText populated
```

### Opt-out signals

- `RagOptions.SkipCompression = true` — `ChatAnswerEngine` skips the compressor call entirely.
- No `IContextualCompressor` registered — `ChatAnswerEngine` receives `null` and skips. Zero cost when unused.

### Invariants

- `CompressedText` is **never** empty-string. `null` means "no compression applied / fell back to `Chunk.Text`".
- `Chunk.Text` is **never** modified.
- Compression runs **after** retrieval-pipeline filtering (reranking, redundancy, guards) — cost is only spent on chunks that actually reach the LLM.

---

## Error handling

**Principle:** compression is a best-effort optimization. A failing compressor MUST NOT fail the enclosing `AskAsync`. The LLM still gets the raw chunk text. Same contract as `RerankingBehavior` and `RedundancyFilterBehavior`.

### Per-chunk failure isolation

Both compressors execute per-chunk via `Task.WhenAll`. A single failed chunk does not take down the batch.

```csharp
var tasks = sources.Select(async s =>
{
    try { return s with { CompressedText = await CompressOneAsync(s, query, ct) }; }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { LogCompressionFailed(_logger, s.Chunk.DocumentId, ex); return s; }
});
return await Task.WhenAll(tasks);
```

### Error-mode table

| Condition | Behavior |
|---|---|
| Embedding call fails (extractive) | Log warning, emit chunk with `CompressedText = null` |
| LLM call fails (abstractive) | Log warning, emit chunk with `CompressedText = null` |
| LLM returns null / whitespace | Log warning, `CompressedText = null` (never empty-string — would silently hide the chunk) |
| LLM returns *longer* than `MaxTokensPerChunk` | Keep it — cap is a guideline, not hard limit. Log info. Do not re-prompt. |
| Chunk contains zero sentences after splitting | `CompressedText = null` — nothing to compress |
| Both `KeepTopSentences` and `MaxTokensPerChunk` unset | `InvalidOperationException` at `UseContextualCompression` registration time |
| Both set | Documented precedence: `KeepTopSentences` wins |
| Non-positive stopping-criterion value | `InvalidOperationException` at registration time |
| `sources` empty | Return empty, no-op |
| `OperationCanceledException` | Propagate |

### Logging

`[LoggerMessage]` source-generated logs (same pattern as `RegexChunkSanitiser`, `SqliteAuditLog`):

```csharp
[LoggerMessage(Level = LogLevel.Warning,
    Message = "Contextual compression failed for chunk {DocumentId}, falling back to original text.")]
private static partial void LogCompressionFailed(
    ILogger logger, string documentId, Exception ex);
```

---

## Testing

### Unit tests (`tests/Rag.NET.QueryTechniques.Tests/ContextualCompression/`)

**`ExtractiveCompressorTests`:**
- `CompressAsync_TopNMode_KeepsHighestSimilaritySentences`
- `CompressAsync_TokenBudgetMode_StopsAtBudget`
- `CompressAsync_EmbeddingFailure_ReturnsOriginalWithNullCompressedText`
- `CompressAsync_EmptyChunk_ReturnsNullCompressedText`
- `CompressAsync_CancelledToken_ThrowsOperationCanceled`

**`LlmAbstractiveCompressorTests`** (NSubstitute for `IChatClient`):
- `CompressAsync_HappyPath_StoresLlmResponseInCompressedText`
- `CompressAsync_PerChunkParallelism_RunsConcurrentlyNotSequentially` — TaskCompletionSource-based blocking test that proves N chunks fan out concurrently
- `CompressAsync_OneChunkFails_OthersStillCompressed`
- `CompressAsync_EmptyLlmResponse_FallsBackToNull`
- `CompressAsync_CancelledToken_PropagatesOCE`

**`ContextualCompressionRetrievalBehaviorTests`:**
- `HandleAsync_InvokesCompressorOnPipelineResults`

**`RagBuilderExtensionsTests`:**
- `UseContextualCompression_WithoutStoppingCriteria_ThrowsOnRegistration`
- `UseContextualCompression_NegativeValue_ThrowsOnRegistration`
- `UseContextualCompression_ExtractiveStrategy_RegistersExtractiveCompressor`
- `UseContextualCompression_AbstractiveStrategy_RegistersLlmAbstractiveCompressor`
- `UseContextualCompressionInRetrieval_AddsBehaviorToPipelineBuilder`
- `UseContextualCompressionInRetrieval_WithoutBaseRegistration_Throws`

### `ChatAnswerEngine` regression tests (`tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs`)

- `BuildMessagesAsync_PrefersCompressedTextWhenPresent`
- `BuildMessagesAsync_FallsBackToChunkTextWhenCompressedTextNull`
- `AskAsync_SkipCompressionTrue_DoesNotInvokeCompressor`

### Integration test

Extend `tests/Rag.NET.Tests/E2E/` (or add one test in the existing E2E suite):
- `FullPipeline_WithExtractiveCompression_AnswerReceivesCompressedText` — ingest a long document, ask a question, capture the `IChatClient` prompt via a test double, verify the prompt contains fewer sentences than the same pipeline without compression.

### Benchmarks (`benchmarks/Rag.NET.Benchmarks/ContextualCompressionBenchmarks.cs`)

Extractive only for v1 (abstractive would measure network latency, not library performance):
- `TopN_SmallChunk`
- `TopN_LargeChunk`
- `TokenBudget_LargeChunk`

All benchmarks use a deterministic fake `IEmbeddingGenerator` returning fixed vectors.

### Out of scope

- LLM output quality — `Rag.NET.Evaluation` package's concern.
- Tokenizer accuracy — trust `Microsoft.ML.Tokenizers`.

---

## Open decisions (none)

All five design questions answered during brainstorming:

1. **Modes:** Extractive + Abstractive behind `IContextualCompressor` strategy interface.
2. **Placement:** Separate `IContextualCompressor` interface; default at `ChatAnswerEngine`; opt-in retrieval behavior.
3. **Stopping criterion:** Top-N (default) + Per-chunk token budget (alternative). Exactly one required.
4. **Output shape:** Non-destructive `CompressedText` property on `SearchResult`. Answer engine reads `CompressedText ?? Chunk.Text`.
5. **Abstractive call shape:** One LLM call per chunk, parallel via `Task.WhenAll`.
