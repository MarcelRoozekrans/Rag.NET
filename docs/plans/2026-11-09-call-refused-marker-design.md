# A marker for deliberate refusals, and the model-call wraps it unblocks (#930 layer 1)

**Date:** 2026-11-09 · **Issue:** #930 (layer 1) · **Follows:** #504 / #508

## Intent

A failed embedding call during ingestion is reported as `RagError.StorageFailed`. The same is true
of the semantic and late chunkers, and of RAPTOR and community detection, whose wraps #508 reverted.
`StorageFailed` sends an operator to inspect a vector store that was never asked to do anything.

#508 recorded why those call sites cannot just be wrapped. A call-site `catch` cannot tell a
provider failure from a decorator that refused on purpose, such as a benchmark cache opened
refuse-on-miss or a blown cost budget. Wrapping relabels the refusal as "the model failed" when no
model was called. This change builds the missing seam first, then applies the wraps.

## Goals

- A public type that marks a deliberate refusal. Wraps let it through untouched.
- Embedding, chunking, RAPTOR and community-detection failures reach `PipelineIngestor` as
  `ModelCallException`, and so are reported as `RagError.ModelCallFailed`.
- Every refusal that exists today carries the marker.

## Non-goals

- A `RagError` case for refusals. A refusal that reaches `PipelineIngestor` still maps to
  `StorageFailed`. A new case breaks exhaustive switches after 1.0, which is option 4 on #930;
  it is filed as its own issue.
- Recognising provider SDK exception types in core. `ModelCallException`'s remarks explain why not.

## Anti-goals

- A refusal reported as a model failure. That would repeat the mislabelling that broke two guards
  in #508.
- A refusal that triggers a retry or fallback. Retrying a blown budget keeps spending past the limit.

## Approaches

The operator chose option 1 on #930 (build the seam, then wrap) over wrapping `EmbeddingBehavior`
alone, attributing failures by stage in `PipelineIngestor`, and adding a `RagError` case. Three
shapes for the marker were considered:

| Shape | Trade-off |
|---|---|
| **Base exception** (chosen, 2026-11-09) | One `catch (CallRefusedException) { throw; }` per wrap. Stays an `InvalidOperationException`, so existing catches keep working. Needs a new project reference from `Rag.NET.Benchmarks.Quality` to Abstractions. |
| Marker interface | Leaves each exception's base type alone, but the caches still need their own exception type to carry it. Every wrap needs a `when` filter. Interfaces on exceptions are unusual in .NET. |
| `Exception.Data` flag | No new type, but nothing enforces it; a forgotten or misspelled key fails silently. |

## Design

**The marker.** `public class CallRefusedException : InvalidOperationException` in
`Rag.NET.Abstractions/Models`, namespace `Rag.NET.Models`. It is unsealed so specific refusals
can derive from it, and has `(string message)` and `(string message, Exception? inner)`
constructors. Its contract: thrown *instead of* calling a model, on purpose, by this library or a
decorator around a client. It is never a provider failure, and it is never retried.

**Who carries it:**

- `BudgetExceededException` derives from `CallRefusedException` instead of directly from
  `InvalidOperationException`. It is still an `InvalidOperationException`. Inserting a base class
  is binary-compatible.
- The benchmark caches' refuse-on-miss throws, where no model was called:
  - `EmbeddingCache` when the prefetch finds a cold cache (`:218`) or a text was never prefetched (`:251`)
  - `GraphExtractionCache.MissingEntry` and `HypotheticalCache.MissingEntry`
  - `CachedGraphRagClient` when no model was given to fill a miss (`:267`)

  `Rag.NET.Benchmarks.Quality` gains a `ProjectReference` to `Rag.NET.Abstractions`. It is not
  packable, so nothing outside the repo sees the change.
- **Not** the cache throws that come after a real model call: blank text
  (`GraphExtractionCache:264`, `HypotheticalCache:179`) and a wrong vector count or an empty vector
  (`EmbeddingCache:298`, `:310`). The model answered badly, so those are model failures, and a wrap
  should report them as such.

**The wrap, at each call site.** In this order:

1. `OperationCanceledException`: rethrow.
2. `CallRefusedException`: rethrow.
3. `ModelCallException`: rethrow, so a wrap nested inside another wrap does not wrap twice.
4. Anything else: throw `ModelCallException` with a message that names the stage and the document,
   and the original exception as `InnerException`.

Call sites:

- `EmbeddingBehavior`: both `Embedder.GenerateAsync` calls (`:89` and `:124`). For the "generator returned N
  embeddings for M inputs" check, see "Left to the builder".
- `SemanticChunkingStrategy` (`:82`, `:128`) and `LateChunkingStrategy` (`:115`).
- `RaptorIngestionBehavior`: the summary chat call (`:497`) and its embedding (`:501`).
- `CommunityDetectionBehavior`: the report chat call (`:254`).

**Bringing the existing code in line:**

- `ImageDocumentParser` catches `CallRefusedException` instead of `BudgetExceededException`, so a
  cache refusal also passes through it.
- `FallbackChatClient.IsTransient` returns false for any `CallRefusedException`, not only a blown
  budget. A cache refusal must not fall back to the next provider either.
- `ModelCallException`'s remarks are rewritten: the seam now exists, and the list of throwing
  components grows. `BudgetExceededException`'s summary mentions its new base.

**Tests.** For each wrapped call site, three guards:

- a provider failure arrives as `ModelCallException`;
- a `CallRefusedException` passes through by type;
- cancellation passes through.

At the ingestor level, an embedding failure becomes `RagError.ModelCallFailed`. A cache refusal
through the RAPTOR and community-detection paths stays a refusal; these are the two guards that
failed in #508. Existing tests that pin a cache refusal with an exact
`ThrowsAsync<InvalidOperationException>` move to `CallRefusedException`.

**Release.** Additive public API: `feat`, a minor release. No `!`.

## Left to the builder

- Whether the four-step wrap lives in a shared helper or is written out at each site. Different
  packages make the call (Rag.NET, Chunking, Raptor, GraphRag), so a helper would have to be
  public in Abstractions or shared source. Writing it inline costs about five lines per site and
  keeps the public surface small. My call: inline, unless a fifth site appears.
- The count-mismatch checks in `EmbeddingBehavior` (`:92`, `:127`): the generator answered with
  the wrong number of vectors. My call: report it as a model failure by moving the check inside the
  wrap, matching how the cache's own count check is treated above.
- Log messages and exact exception wording.
