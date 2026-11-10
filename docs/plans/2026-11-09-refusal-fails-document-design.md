# A deliberate refusal fails the document (#938)

**Date:** 2026-11-09 · **Issue:** #938 · **Follows:** #941 (`CallRefusedException`, shipped in 1.5.0)

## Intent

Several ingestion components catch every exception except cancellation and fall back to a degraded
result. That catch also takes a `CallRefusedException`. When a cost budget is spent, the document is
still ingested in degraded form: passage chunks, unembedded windows, no metadata, no tags, no sparse
vectors. The result reports success, so the budget's kill switch stops nothing. A benchmark cache
opened refuse-on-miss is swallowed in the same way and records a degraded run as a valid one.

Decision (2026-11-09, on the issue): a refusal fails the document.

## Goals

- At every ingestion site that degrades on a model failure, a refusal propagates untouched to
  `PipelineIngestor`, and the document fails.
- Each site is pinned by a test that throws `BudgetExceededException` from the client or embedder.
- One end-to-end ingest shows the document failing on a refusal.

## Non-goals

- The query path. The same catch is in seven query-path components, filed as #948. That would
  change query behaviour, and it was kept out of this fix by decision.
- A `RagError` case for refusals. Until #937 (next major) a refusal that reaches
  `PipelineIngestor` is still reported as `RagError.StorageFailed`, with the refusal inside it.

## Anti-goals

- Changing what a genuine provider failure does at these sites. A failed or unparseable call still
  degrades exactly as before; only the refusal changes.

## Approaches

1. **Rethrow before each degrading catch (chosen).** `catch (CallRefusedException) { throw; }` sits
   right after the `OperationCanceledException` rethrow, the order `ModelCallException`'s remarks
   give for every wrap. It is one line per site and matches the existing wraps in
   `EmbeddingBehavior`, `SemanticChunkingStrategy`, RAPTOR and community detection.
2. **Exception filter on the catch-all** (`catch (Exception ex) when (ex is not CallRefusedException)`).
   It has the same effect but a different shape from every other wrap in the codebase. Rejected for
   consistency.
3. **Catch the refusal centrally in a decorator.** Not possible: the catch sits inside the component,
   below any decorator, so the refusal is gone before a decorator sees it.

## Sites

| Site | Today on refusal |
|---|---|
| `PropositionChunkingStrategy` (`src/Rag.NET.Chunking/PropositionChunkingStrategy.cs:56`) | passage chunk |
| `LateChunkingStrategy` (`src/Rag.NET.Chunking/LateChunkingStrategy.cs:47`) | unembedded windows |
| `ResumeChunkingStrategy` (`src/Rag.NET.Chunking.Templates/ResumeChunkingStrategy.cs:42`) | one full-text chunk |
| `MindMapExtractor` (`src/Rag.NET.GraphRag/MindMapExtractor.cs:59`) | empty root |
| `LlmChunkSanitiser` (`src/Rag.NET.Security/LlmChunkSanitiser.cs:51`) | regex fallback |
| `LlmPiiChunkSanitiser` (`src/Rag.NET.Security/Pii/LlmPiiChunkSanitiser.cs:56`) | regex fallback |
| `LlmMetadataExtractionBehavior` (`src/Rag.NET/Ingestion/Behaviors/LlmMetadataExtractionBehavior.cs:83`) | no metadata |
| `TagIngestionBehavior` (`src/Rag.NET/Ingestion/Behaviors/TagIngestionBehavior.cs:45`) | tag skipped |
| `SparseEmbeddingBehavior` (`src/Rag.NET/Ingestion/Behaviors/SparseEmbeddingBehavior.cs:43`) | no sparse vectors |

**Already correct:** `GraphEntityExtractionBehavior`'s chat call catches only `JsonException` and
one `ArgumentOutOfRangeException`, so a refusal already propagates. A test pins it. The callers
(`ChunkSanitiserBehavior`, `MindMapExtractionBehavior`, `TracingChunkSanitiser`) do not catch around
the inner call.

## Also changes

- `ModelCallException`'s remarks: the "tracked as #938" sentence becomes a statement that the
  degrading catches let a refusal through.
- Release note: release-please builds the changelog from the squash subject alone, so the
  behaviour change is in the subject, for example
  `fix(ingestion): stop a document when a call is refused, instead of ingesting it degraded`.
  The PR body says budget-limited ingestion now stops when the budget is spent, and that the
  failure is reported as `StorageFailed` until #937.

## Left to the builder

The test doubles per project, and whether the end-to-end test lives in `Rag.NET.Tests` or a
GraphRag/Chunking test project.
