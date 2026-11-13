# A deliberate refusal fails the query (#948)

**Date:** 2026-11-13 · **Issue:** #948 · **Follows:** #938 (shipped in 1.6.0 as #950)

## Intent

#938 made a deliberate refusal (`CallRefusedException`: a spent cost budget, or a benchmark cache
opened refuse-on-miss) fail the document instead of being ingested in degraded form. The query path
still has the old catch: components that degrade on a model failure log a refusal and carry on. The
bad case is the benchmark harness. A `HypotheticalCache` opened refuse-on-miss throws on a miss,
HyDE swallows it, and the run is recorded as a valid HyDE result when no hypothetical document was
used. With a spent budget the answer engine's own call usually fails next, but a retrieval-only
caller is not stopped at all.

Decision (operator, 2026-11-13): the #938 rule carries over to every query-path catch that wraps a
model call, plus the wrappers that would otherwise catch the rethrow one level up. The issue names
seven components; the sweep it asked for found the rest.

## Goals

- At every query-path site that degrades on a model failure, a refusal propagates untouched, and the
  query fails.
- Each site is pinned by a test that throws `BudgetExceededException` from the client, embedder,
  reranker or inner component it wraps.
- One end-to-end retrieval through `PipelineRetriever` shows a HyDE refusal failing the query
  instead of retrieving without a hypothetical document.

## Non-goals

- A `RagError` case for refusals. Until #937 (next major) a refusal that reaches `PipelineRetriever`
  is reported as `RagError.StorageFailed` with the refusal inside, and `RagPipeline.AskAsync` throws
  it wrapped in `InvalidOperationException`, as for ingestion.
- Catches that wrap no model call: BM25 search, vector-store and parent-document fetches, the
  federated store fan-out, audit and diagnostics capture, regex sanitisers and guards, and CRAG's web
  search. A refusal cannot start there.

## Anti-goals

- Changing what a genuine provider failure does at these sites. A failed or unparseable call still
  degrades exactly as before; only the refusal changes. The one exception is the cache behaviours'
  double run, below, which is a bug.

## Approaches

1. **Rethrow before each degrading catch (chosen).** `catch (CallRefusedException) { throw; }` right
   after the `OperationCanceledException` rethrow, the order `ModelCallException`'s remarks give and
   the shape #950 used at every ingestion site.
2. **Exception filter on the catch-all.** Same effect, different shape from every other wrap.
   Rejected for consistency, as in #938.
3. **Catch once at the top (`PipelineRetriever`, `AskAsync`).** Does not work on its own: the inner
   catches swallow the refusal before it gets there.

## Sites

Named in the issue:

| Site | Today on refusal |
|---|---|
| `LlmQuerySanitiser` (`src/Rag.NET.Security/LlmQuerySanitiser.cs:43`) | regex fallback |
| `HydeBehavior` (`src/Rag.NET/Retrieval/Behaviors/HydeBehavior.cs:46`) | retrieves without HyDE |
| `CorrectiveRagBehavior` LLM scoring (`src/Rag.NET/Retrieval/Behaviors/CorrectiveRagBehavior.cs:63`) | heuristic scoring |
| `AdaptiveRetrievalBehavior` (`src/Rag.NET/Retrieval/Behaviors/AdaptiveRetrievalBehavior.cs:29`) | default complexity |
| `LlmHypotheticalDocumentGenerator` (`src/Rag.NET.QueryTechniques/LlmHypotheticalDocumentGenerator.cs:76`) | returns the failure as data |
| `LlmAbstractiveCompressor` (`src/Rag.NET.QueryTechniques/ContextualCompression/LlmAbstractiveCompressor.cs:63`) | uncompressed source |
| `SelfAssessmentConfidenceScorer` (`src/Rag.NET.AnswerEngines/SelfAssessmentConfidenceScorer.cs:66`) | confidence 1.0 |

Found by the sweep:

| Site | Today on refusal |
|---|---|
| `IHypotheticalDocumentGenerator` default multi-hypothesis loop (`src/Rag.NET.Abstractions/Abstractions/IHypotheticalDocumentGenerator.cs:51`) | hypothesis skipped |
| `MultiQueryBehavior` expansion and per-variant retrieval (`src/Rag.NET/Retrieval/Behaviors/MultiQueryBehavior.cs:29`, `:62`) | no variants / variant skipped |
| `DeepResearchRetriever` sub-query and sufficiency check (`src/Rag.NET/Retrieval/DeepResearchRetriever.cs:85`, `:180`) | sub-query skipped / "sufficient" |
| `RerankingBehavior` (`src/Rag.NET/Retrieval/Behaviors/RerankingBehavior.cs:46`) | unreranked, truncated |
| `EnsembleBehavior` sparse arm (`src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs:269`) | no sparse arm |
| `MmrBehavior` (`src/Rag.NET/Retrieval/Behaviors/MmrBehavior.cs:41`) | candidates unselected |
| `RedundancyFilterBehavior` (`src/Rag.NET/Retrieval/Behaviors/RedundancyFilterBehavior.cs:29`) | unfiltered |
| `ContextualCompressionRetrievalBehavior` (`src/Rag.NET/Retrieval/Behaviors/ContextualCompressionRetrievalBehavior.cs:39`) | uncompressed (wraps the compressor) |
| `ExtractiveCompressor` query and source embedding (`src/Rag.NET.QueryTechniques/ContextualCompression/ExtractiveCompressor.cs:66`, `:110`) | uncompressed |
| `TagRetriever` (`src/Rag.NET/Retrieval/TagRetriever.cs:83`) | no tag filter |
| `EmbeddingCacheBehavior`, `ResultCacheBehavior` (`src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs:46`, `ResultCacheBehavior.cs:40`) | re-runs the inner pipeline |
| `RefineAnswerEngine` refine step (`src/Rag.NET.AnswerEngines/RefineAnswerEngine.cs:98`) | step skipped |
| `MapReduceAnswerEngine` map step (`src/Rag.NET.AnswerEngines/MapReduceAnswerEngine.cs:213`) | chunk skipped |
| `FlareAnswerEngine` scorer and lookahead (`src/Rag.NET.AnswerEngines/FlareAnswerEngine.cs:195`, `:244`) | confidence 1.0 / no lookahead |
| `ConversationMemoryPipeline` summary (`src/Rag.NET/Memory/ConversationMemoryPipeline.cs:100`) | no summary |
| `PersistentConversationMemory` search (`src/Rag.NET.Memory/PersistentConversationMemory.cs:145`) | no prefix injection |

The two cache behaviours call `next()` inside the cache factory, so their catch also takes any
refusal raised further down the pipeline and then runs the inner pipeline a second time. The same
double run happens for any genuine downstream failure: a failing reranker call or vector search is
made twice, and logged as a cache failure. That is a bug in the same function, so it is fixed here:
the catch degrades only when the cache itself failed, and an exception thrown by the inner pipeline
propagates as it is. That covers the refusal too, so these two sites need no separate rethrow.

`LlmHypotheticalDocumentGenerator` returns its failure as data rather than throwing; the rethrow
goes before that catch so a refusal leaves the generator as an exception.

## Also changes

- `ModelCallException`'s remarks: the "query path ... tracked as #948" sentence becomes a statement
  that the query-path catches let a refusal through too.
- Release note: the squash subject carries the behaviour change, for example
  `fix(retrieval): fail a query when a call is refused, instead of answering it degraded`. The PR
  body says a spent budget or refuse-on-miss cache now fails the query, reported as `StorageFailed`
  (retrieval) or a thrown `InvalidOperationException` (`AskAsync`) until #937.

## Left to the builder

Test doubles per project, and grouping the sites into commits by package.
