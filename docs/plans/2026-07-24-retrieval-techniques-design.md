# Retrieval Techniques — Design (Phase 1.2)

**Date:** 2026-07-24
**Milestone:** 1 — Feature Backlog, Phase 1.2
**Covers features.md rows:** Hypothetical Document Embeddings v2; FLARE; Sparse Embedding Retrieval (SPLADE); Multi-Index Federation

## Scope decisions (agreed)

1. **FLARE trigger** is pluggable: an `IConfidenceScorer` abstraction with a self-assessment
   default that works on every `IChatClient`. A logprob-based scorer is a documented extension
   point, not built in this phase.
2. **SPLADE storage** ships for Qdrant (native named sparse vectors) and the in-memory store.
   PgVector sparse (custom schema, no native type) is deferred; the features.md row is
   annotated with the delivered scope.
3. **Federation writes** go to a configurable primary store (default: first); searches fan out
   to all stores and merge via RRF. Deletes affect only the primary (documented).
4. **HyDE v2** generates `HypothesisCount` (default 3) hypotheses and averages their
   embeddings; averaging happens in `HydeBehavior` via a new internal
   `RetrievalOptions.EmbeddingOverride` vector, not by multiplying text overrides.

## 1. HyDE v2 — multi-hypothesis

**Package:** `Rag.NET.QueryTechniques` (generator) + core `Rag.NET` (behavior plumbing)

- `HydeOptions` gains `HypothesisCount` (default 3, validated >= 1) and
  `HypothesisTemperature` (default 0.8 — diversity across hypotheses).
- `IHypotheticalDocumentGenerator` gains
  `Task<IReadOnlyList<string>> GenerateManyAsync(string query, int count, CancellationToken ct)`.
  `LlmHypotheticalDocumentGenerator` implements it with `count` parallel LLM calls
  (SemaphoreSlim-bounded), per-call failure tolerated as long as >= 1 hypothesis survives.
  The existing single-doc `GenerateAsync` remains (delegates to `GenerateManyAsync(query, 1)`).
- `RetrievalOptions` gains `internal ReadOnlyMemory<float>? EmbeddingOverride`.
  Consumption order in `VectorStoreBehavior`/`EnsembleBehavior`:
  `EmbeddingOverride` → embed(`EmbeddingTextOverride` ?? `Query`).
- `HydeBehavior` (embedder now injected, `Required=false`):
  - `HypothesisCount == 1` or no embedder → today's path (`EmbeddingTextOverride` = the one doc).
  - Else: generate N docs, embed them in one batch, mean + L2-normalize, set
    `EmbeddingOverride`, clear `UseHyde`. Any failure → log + fall through to plain query.
- `EmbeddingCacheBehavior` skips caching when `EmbeddingOverride` is set (the vector is
  already computed; there is no text key to cache under).

## 2. FLARE — active retrieval during generation

**Package:** `Rag.NET.AnswerEngines` (engine) + `Rag.NET.Abstractions` (scorer abstraction + options)

### 2a. `IConfidenceScorer`

```csharp
public interface IConfidenceScorer
{
    /// <summary>Score 0..1 confidence that the sentence is correct given the context.</summary>
    ValueTask<double> ScoreAsync(string sentence, string partialAnswer,
        IReadOnlyList<SearchResult> context, CancellationToken ct = default);
}
```

Default: `SelfAssessmentConfidenceScorer` (`IChatClient`) — one small LLM call returning a
0–1 score (JSON number), delimiter-fenced prompt (v4-GUID fence, data-not-instructions
sentence — compressor/proposition conventions). Parse failure → score 1.0 (fail-open: no
spurious re-retrievals). A logprob scorer is a documented extension point.

### 2b. `FlareAnswerEngine : IAnswerEngine`

Ctor `(IChatClient chatClient, IRetriever retriever, IConfidenceScorer scorer, FlareOptions options, ILogger?)`.

Loop (per `AskAsync`):
1. Generate the next sentence only (prompt instructs continue-answer-one-sentence; stop
   after sentence terminator — parse first sentence from the response).
2. `scorer.ScoreAsync(sentence, partialAnswer, currentContext)`.
3. Below `ConfidenceThreshold` (default 0.6) and `retrievalsUsed < MaxRetrievals` (default 3):
   build lookahead query = the low-confidence sentence (+ original query), `retriever.RetrieveAsync`,
   merge new sources into context (dedup by `(DocumentId, ChunkIndex)`, keep max score),
   regenerate the sentence once with the refreshed context; accept the regenerated sentence
   regardless of its score (no infinite loops — one regeneration per sentence).
4. Append sentence; stop on empty continuation, `MaxSentences` (default 15), or model signaling
   completion.
5. `RagAnswer.Sources` = the full deduped context actually used.

`FlareOptions`: `ConfidenceThreshold`, `MaxRetrievals`, `MaxSentences`, `LookaheadTopK`
(TopK for mid-generation retrievals, default 3), `ChatClient`/`Scorer` overrides (nullable).

Error handling: retrieval failure mid-loop → log warning, continue with existing context;
scorer failure → fail-open (1.0). Cancellation propagates everywhere.

`AskStreamingAsync` delegates to `AskAsync` (deliberate — sources must reflect all
retrievals; same pattern and comment as MapReduce/Refine).

**DI:** `SynthesisStrategy.Flare` enum value; `DispatchingAnswerEngine` routes to it;
`UseFlare(Action<FlareOptions>?)` registers scorer + engine (engine resolves `IRetriever`
from DI — the full retrieval pipeline serves lookahead queries).

## 3. SPLADE — sparse embedding retrieval

**Packages:** `Rag.NET.Abstractions` (contracts), `Rag.NET.Embeddings.Onnx` (encoder),
core + `Rag.NET.VectorStores.Qdrant` (storage/search)

### 3a. Contracts

```csharp
public sealed record SparseVector
{
    public required ReadOnlyMemory<int> Indices { get; init; }   // ascending term ids
    public required ReadOnlyMemory<float> Values { get; init; }  // parallel weights, > 0
}

public interface ISparseEmbeddingGenerator
{
    ValueTask<SparseVector> GenerateAsync(string text, CancellationToken ct = default);
}

public interface ISparseSearchable   // optional capability, IHybridSearchable pattern
{
    Task StoreSparseAsync(IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items, CancellationToken ct);
    Task<IReadOnlyList<SearchResult>> SearchSparseAsync(SparseVector query, SearchOptions options, CancellationToken ct);
}
```

### 3b. `OnnxSpladeEncoder : ISparseEmbeddingGenerator` (`Rag.NET.Embeddings.Onnx`)

BERT tokenizer + MLM-head ONNX model (naver/splade-cocondenser-style): logits `[1, seq, vocab]`
→ `log(1 + ReLU(logit))` → max-pool over sequence → prune to `TopTerms` (default 256) non-zero
weights. Over-long input windowed with the existing `TokenWindowStitcher.Windows` and max-pooled
across windows. Options: `ModelPath`, `TokenizerVocabPath`, `MaxTokens` (512 default — SPLADE
models are short-context), `TopTerms`, `OutputName` (default "logits"). Same ctor validation,
output-shape validation, and `WindowRunner`-style test seam as `OnnxTokenEmbeddingGenerator`.
`UseSpladeEncoder(Action<OnnxSpladeOptions>)` registers it.

### 3c. Storage & retrieval

- `QdrantVectorStore : ISparseSearchable` — named sparse vectors ("splade") next to the dense
  vector; store-time upsert includes both; `SearchSparseAsync` queries the named sparse vector.
  Collection bootstrap creates the sparse vector config when the store is sparse-enabled.
- `InMemoryVectorStore : ISparseSearchable` — postings `Dictionary<int, List<(chunkRef, weight)>>`,
  dot-product scoring (BM25-index style, `ReaderWriterLockSlim`).
- Ingestion: `SparseEmbeddingBehavior` (after `EmbeddingBehavior`) — when an
  `ISparseEmbeddingGenerator` is registered AND the store is `ISparseSearchable`, compute sparse
  vectors per chunk and pass them to `StoreSparseAsync` (storage behavior orchestrates; exact
  seam decided in planning against the current `StorageBehavior` shape). Failure → log, dense-only.
- Retrieval: `EnsembleBehavior` grows a third arm — dense + BM25 (if configured) + sparse
  (encoder + `ISparseSearchable` present and `RetrievalOptions.UseSparseSearch`, default follows
  `UseHybridSearch`) — all fused by the existing `RrfMerger` with an added `SparseWeight` on
  `EnsembleOptions`.
- features.md row annotated: delivered for Qdrant + in-memory; PgVector deferred.

## 4. Multi-Index Federation

**Package:** core `Rag.NET`

`FederatedVectorStore : IVectorStore`:
- Ctor `(IReadOnlyList<IVectorStore> stores, FederatedStoreOptions options, ILogger?)`;
  validates >= 2 stores; `PrimaryIndex` (default 0) bounds-checked.
- `SearchAsync`: fan out to all stores concurrently (each asked for `TopK` — RRF needs full
  per-store rankings); per-store failure → log warning + skip; all stores failed → throw
  `InvalidOperationException` naming the stores; merge via RRF (`RrfMerger` — promote to
  `internal` + `InternalsVisibleTo`, or a thin internal bridge; decided in planning); take
  `TopK` post-merge; each result's metadata gains `source.store` = store index/name.
- `StoreAsync`/`DeleteByDocumentIdAsync`: primary only (documented — deletes do not touch
  secondary stores).
- `FederatedStoreOptions`: `PrimaryIndex`, `StoreNames` (optional labels for `source.store`
  and error messages), `RrfK` (default 60).
- **DI:** `UseFederatedSearch(Action<FederatedStoreBuilder>)` — builder collects store
  factories (`AddStore(Func<IServiceProvider, IVectorStore>)` + convenience for registered
  singletons), optional `WithPrimary(index/name)`; registers the federated instance AS the
  `IVectorStore`, so the entire existing pipeline (MMR, rerank, cache, …) composes unchanged.
- Note: `IHybridSearchable`/`ISparseSearchable` are NOT federated in this phase (dense-only
  federation; documented limitation).

## Error handling summary

Uniform posture: degraded, never broken. HyDE v2 falls back to plain query; FLARE continues
with existing context on retrieval failure and fails open on scorer failure; sparse arm drops
to dense-only; federation skips failed stores and only throws when nothing answered.

## Testing

- HyDE v2: generator multi-call behavior (count, partial failures), averaging math
  (hand-computable), EmbeddingOverride consumption + cache skip. Substituted embedder/chat.
- FLARE: scripted `IChatClient` (sentence sequence) + substituted `IRetriever`/scorer —
  trigger threshold, regeneration, MaxRetrievals/MaxSentences caps, dedup of sources,
  fail-open scorer, cancellation. Dispatching + DI tests.
- SPLADE: encoder pure-logic via seam (ReLU/log/max-pool/prune hand-computed, windowed
  max-pool); in-memory sparse store scoring; EnsembleBehavior 3-arm fusion with RrfMerger;
  Qdrant sparse gated behind Docker integration tests (existing Qdrant test pattern).
- Federation: fan-out/merge with fake stores (per-store failure, all-fail, RRF ordering,
  TopK, source.store metadata, primary-only writes), DI builder tests.

## Out of scope

- Logprob-based confidence scorer (extension point only).
- PgVector sparse storage.
- Federating hybrid/sparse search across stores.
- FLARE token-incremental streaming.
