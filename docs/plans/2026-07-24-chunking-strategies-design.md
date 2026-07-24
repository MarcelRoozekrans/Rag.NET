# Chunking Strategies — Design (Phase 1.1)

**Date:** 2026-07-24
**Milestone:** 1 — Feature Backlog, Phase 1.1
**Covers features.md rows:** Sliding Window Chunking with Overlap; Proposition Extraction Chunking; Late Chunking

## Scope decisions (agreed)

1. **Sliding window** is fulfilled by upgrading `TokenAwareChunkingStrategy` — no new near-duplicate
   strategy. It gains a proper options class and is documented as the O(n) token-window baseline.
2. **Proposition extraction** chunks carry parent-passage metadata compatible with the existing
   Parent-Document Retrieval feature, so retrieval can expand a hit proposition to its passage.
3. **Late chunking** is the real thing (token-level embeddings), not an approximation: a new
   `ITokenEmbeddingGenerator` abstraction plus a local ONNX implementation in a new
   `Rag.NET.Embeddings.Onnx` package (mirroring the `Rag.NET.Reranking.Onnx` precedent).
4. **Precomputed embeddings** flow through the pipeline via a new optional
   `ReadOnlyMemory<float>? Embedding` field on `TextChunk` (init-only, additive).
   `EmbeddingBehavior` skips chunks that already carry an embedding — the same
   non-destructive-field + skip-guard pattern proven by `SearchResult.CompressedText`.

## 1. Sliding Window — TokenAware upgrade

**Package:** `Rag.NET.Chunking.TokenAware` (existing)

- New `TokenAwareChunkingOptions` (in Abstractions, following `SemanticChunkingOptions` placement):
  `ModelName` (default `"gpt-4"` → cl100k_base), `WindowSizeTokens` (default from
  `ChunkingOptions.MaxChunkSize`), `OverlapTokens` (default from `ChunkingOptions.Overlap`).
  Validated: window > 0, overlap >= 0, overlap < window.
- `TokenAwareChunkingStrategy` gains a ctor accepting the options class; the existing
  `(string modelName)` ctor delegates to it (back-compat).
- `UseTokenAwareChunking(Action<TokenAwareChunkingOptions>? configure = null)` overload added
  beside the existing `UseTokenAwareChunking(string modelName)`.
- Docs: `docs/guide/chunking.md` section updated to present it as the sliding-window baseline;
  features.md row ticked.

## 2. Proposition Extraction Chunking

**Package:** `Rag.NET.Chunking` — `PropositionChunkingStrategy : IDocumentChunkingStrategy`

Modeled on `ResumeChunkingStrategy` (the IChatClient-calling chunking precedent).

**Flow:** concatenate incoming sections per document → split into passages by token window
(`MaxPassageTokens`, default 1000, cl100k_base) → for each passage, one LLM call with a
delimiter-fenced prompt (same randomized-fence hardening as `LlmAbstractiveCompressor`, v4 GUID)
asking for a JSON array of atomic, self-contained propositions → each proposition becomes a
`TextChunk`.

**Chunk metadata (Parent-Document-Retrieval compatible):**
- `parent.chunk.index` — index of the source passage
- `parent.start` / `parent.end` — char span of the passage within the document text
- The passage itself is NOT emitted as a chunk by default (`EmitParentPassages` option, default
  false, can emit them for dual-index setups).

**Options:** `PropositionChunkingOptions` — `MaxPassageTokens`, `EmitParentPassages`,
`ChatClient` override (nullable, same pattern as `ResumeChunkingOptions`), `MaxPropositionsPerPassage`
(safety cap, default 50).

**Error handling:** per-passage try/catch — on LLM failure or unparseable JSON, log a warning and
fall back to emitting the passage as a single chunk. Never throws for per-passage failures;
cancellation propagates. Empty/whitespace propositions are dropped.

**DI:** `UsePropositionChunking(Action<PropositionChunkingOptions>? configure = null)` using the
Semantic multi-interface aliasing pattern (registers `IDocumentChunkingStrategy` + `IChunkingStrategy`).

## 3. Late Chunking

### 3a. `ITokenEmbeddingGenerator` (Abstractions)

```csharp
public interface ITokenEmbeddingGenerator
{
    int MaxTokens { get; }
    ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken ct = default);
}

public sealed record TokenEmbeddingResult
{
    public required ReadOnlyMemory<float> Embeddings { get; init; }  // [tokenCount * dim] row-major
    public required int Dimension { get; init; }
    public required IReadOnlyList<(int Start, int End)> TokenOffsets { get; init; } // char spans
}
```

One vector per input token plus the char offsets needed to map token windows back to text.

### 3b. `Rag.NET.Embeddings.Onnx` (new package)

`OnnxTokenEmbeddingGenerator : ITokenEmbeddingGenerator` — runs a local long-context embedding
model (jina-embeddings-v2-style) via `Microsoft.ML.OnnxRuntime`, mirroring
`Rag.NET.Reranking.Onnx`'s structure (model path + tokenizer in options, lazy session init,
thread-safe). Options: `OnnxTokenEmbeddingOptions` — `ModelPath`, `TokenizerPath` (HF tokenizer
json), `MaxTokens` (default 8192). Long inputs are windowed internally with overlap and stitched.

### 3c. `LateChunkingStrategy` (`Rag.NET.Chunking`) : `IDocumentChunkingStrategy`

**Flow per section (or concatenated document up to `MaxTokens`):**
1. `ITokenEmbeddingGenerator.GenerateAsync(fullText)` → token embeddings + offsets.
2. Split the token sequence into windows (`WindowSizeTokens`, `OverlapTokens` — reuse the
   token-window math from TokenAware).
3. For each window: chunk text = substring from first/last token offsets; chunk embedding =
   mean-pool of the window's token vectors (L2-normalized).
4. Emit `TextChunk` with `Embedding` populated.

**Options:** `LateChunkingOptions` — `WindowSizeTokens`, `OverlapTokens`, `Generator` override
(nullable `ITokenEmbeddingGenerator`).

**Error handling:** if the generator fails for a section, log a warning and fall back to
token-window chunks WITHOUT embeddings (downstream `EmbeddingBehavior` embeds them normally) —
degraded, never broken.

**DI:** `UseLateChunking(Action<LateChunkingOptions>? configure = null)`; requires an
`ITokenEmbeddingGenerator` registered (e.g. `UseOnnxTokenEmbeddings(o => ...)` from the new package).

### 3d. Pipeline plumbing

- `TextChunk` gains `public ReadOnlyMemory<float>? Embedding { get; init; }` — additive,
  no existing call sites change.
- `EmbeddingBehavior`: chunks where `Embedding is not null` are passed through untouched;
  only the rest are sent to the batch embedder. Mixed batches preserve order.
- `StorageBehavior`/vector-store writes already receive embeddings paired with chunks — the
  only change is that the pairing source may be the chunk's own field. (Exact wiring verified
  during planning; the invariant: a precomputed embedding is never re-computed, never dropped.)
- **Dimension guard:** if a precomputed embedding's dimension differs from the store's configured
  dimension, fail the document with a clear error at storage time (existing store validation path).

## Testing

- `TokenAwareChunkingOptions` validation + strategy behavior with options (unit, `Rag.NET.Tests/Chunking`).
- Propositions: JSON parsing (well-formed, malformed → fallback), parent metadata correctness,
  passage windowing, empty-proposition filtering, cancellation. NSubstitute `IChatClient`
  (ResumeChunkingStrategy test precedent).
- Late chunking: fake `ITokenEmbeddingGenerator` (deterministic vectors) → window math, mean-pooling
  correctness (hand-computable small cases), offset→text mapping, fallback-without-embeddings path.
- `EmbeddingBehavior` skip-guard: mixed precomputed/plain chunk batches — precomputed untouched,
  plain embedded, order preserved.
- ONNX generator: unit tests for windowing/stitching logic with the session mocked behind a seam;
  a real-model smoke test only in `Rag.NET.Chunking.IntegrationTests` (skipped when model files
  absent, same pattern as other optional-asset integration tests).
- DI registration tests for all three `UseXxx` extensions (`Rag.NET.Tests/DependencyInjection`).

## Out of scope

- Downloading/shipping model weights (docs point to HF; integration test skips without them).
- Late chunking for the per-section `IChunkingStrategy` interface (document-level only, like Semantic's primary mode).
- Proposition dedup/verification passes (future refinement strategy).

## Docs & bookkeeping

- `docs/guide/chunking.md`: new sections for Propositions and Late Chunking; TokenAware section
  reframed as sliding-window baseline.
- `docs/reference/features.md`: tick the three rows (Late Chunking row's package becomes
  `Rag.NET.Chunking` + `Rag.NET.Embeddings.Onnx`).
