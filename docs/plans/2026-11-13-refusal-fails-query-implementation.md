# A deliberate refusal fails the query — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every query-path catch that degrades on a model failure lets a `CallRefusedException` through, so a spent budget or a refuse-on-miss benchmark cache fails the query instead of answering it degraded (#948).

**Architecture:** One line per site, `catch (CallRefusedException) { throw; }`, placed directly after the existing `OperationCanceledException` rethrow, the shape #950 used for ingestion. The two cache behaviours get a real fix instead: an exception thrown by the inner pipeline inside the cache factory propagates rather than being taken for a cache failure and re-run.

**Tech Stack:** .NET 10/11, xUnit, NSubstitute, `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`), `HybridCache`.

**Spec:** `docs/plans/2026-11-13-refusal-fails-query-design.md`

## Global Constraints

- The rethrow sits after `catch (OperationCanceledException)` and before `catch (Exception ex)`. Comment it once per file: `// A deliberate refusal is not a model failure: it fails the query instead of degrading it (#948).`
- A genuine (non-refusal) failure at every site keeps today's degraded behaviour. Do not change any existing passing test's expectation.
- Refusal tests use `new BudgetExceededException(CostWindow.Day, 1m, 1m)` and assert `Assert.Same(refusal, ex)`, so the refusal is neither wrapped nor replaced.
- Commits: conventional, header ≤ 100 chars, CI lints every commit; no nested parentheses in bodies; body ends `Refs #948.`; author `Marcel Roozekrans <marcel@roozekrans.nl>` passed with `-c user.name/-c user.email`.
- No `Co-Authored-By` trailer, no session links.

## Review Focus

1. **A refusal one level down.** The compressor rethrows, and then `ContextualCompressionRetrievalBehavior`'s own catch must not swallow it. The same applies to HyDE's generator under `HydeBehavior`, and to anything under the cache behaviours. The end-to-end test in Task 2 and the wrapper test in Task 3 pin it.
2. **Cache stampede joiners.** A second concurrent caller that joins the first caller's `HybridCache` factory receives the same exception. It must propagate for the joiner too, not re-run `next()`. Task 4 pins this by using an exception wrapper, not a local flag.
3. **A real cache failure still degrades.** When `HybridCache` itself throws before or after the factory, the behaviour still logs and calls `next()` once. Task 4 tests it.
4. **Parallel fan-out.** MultiQuery runs its variants concurrently, and the refusal from one variant must surface from `HandleAsync` as the `BudgetExceededException` itself, not an `AggregateException`. Task 3 asserts the type with `Assert.Same`.
5. **A genuine failure still degrades.** Each task checks that its test file already has a test for the degraded path at every site it touches. Where none exists, it adds one: the same arrange step throwing `InvalidOperationException("provider down")` and asserting the fallback.

---

### Task 1: LLM query sanitiser

**Files:**
- Modify: `src/Rag.NET.Security/LlmQuerySanitiser.cs:42`
- Test: `tests/Rag.NET.Security.Tests/LlmQuerySanitiserTests.cs`

- [ ] **Step 1:** Add `Sanitise_RethrowsRefusal_InsteadOfFallingBackToRegex`. The client's `GetResponseAsync` returns `Task.FromException<ChatResponse>(refusal)`; assert `Assert.Throws<BudgetExceededException>` (sync API, as in `LlmChunkSanitiserTests`) and `Assert.Same(refusal, ex)`.
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Security.Tests --filter "FullyQualifiedName~LlmQuerySanitiserTests"`. Expected: the new test FAILS (no exception thrown).
- [ ] **Step 3:** Add the rethrow.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(security): let a refusal through the LLM query sanitiser`.

### Task 2: HyDE (generator, multi-hypothesis default, behaviour, end-to-end)

**Files:**
- Modify: `src/Rag.NET.QueryTechniques/LlmHypotheticalDocumentGenerator.cs:75`, `src/Rag.NET.Abstractions/Abstractions/IHypotheticalDocumentGenerator.cs:50`, `src/Rag.NET/Retrieval/Behaviors/HydeBehavior.cs:45`
- Test: `tests/Rag.NET.Tests/HyDE/LlmHypotheticalDocumentGeneratorTests.cs`, `tests/Rag.NET.Tests/Retrieval/Behaviors/HydeBehaviorTests.cs`

`LlmHypotheticalDocumentGenerator` returns its failure as a `(null, ex)` tuple; the rethrow goes before that catch so a refusal leaves as an exception. Check the tuple's caller (the same file) for a second catch that would re-capture it.

- [ ] **Step 1:** Tests:
  - `GenerateAsync_RethrowsRefusal`: the chat client throws the refusal; `Assert.ThrowsAsync<BudgetExceededException>`, `Assert.Same`.
  - `GenerateManyAsync_RethrowsRefusal` (the default interface method's multi-hypothesis loop; use the method name the interface declares): a stub generator whose `GenerateAsync` throws the refusal; the refusal propagates on the first hypothesis.
  - `HandleAsync_RethrowsRefusal_InsteadOfRetrievingWithoutHyde`: `HydeGenerator` throws the refusal; assert it propagates and that `next` was never called.
  - `RetrieveAsync_HydeRefusal_FailsTheQuery` (end-to-end in `HydeBehaviorTests` or the existing `PipelineRetriever` test file, whichever already builds a pipeline with HyDE): `RetrieveAsync` with `UseHyde = true` returns a failure whose error is `RagError.StorageFailed` and whose inner exception is `Assert.Same(refusal, ...)`.
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~Hyde|FullyQualifiedName~HypotheticalDocument"`. Expected: the four new tests FAIL.
- [ ] **Step 3:** Add the three rethrows.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(retrieval): let a refusal through HyDE instead of retrieving without it`.

### Task 3: Other retrieval behaviours and retrievers

**Files:**
- Modify: `src/Rag.NET/Retrieval/Behaviors/CorrectiveRagBehavior.cs:62` (LLM scoring only; not the web-search catch at `:41`), `AdaptiveRetrievalBehavior.cs:28`, `MultiQueryBehavior.cs:28` and `:61`, `RerankingBehavior.cs:45`, `EnsembleBehavior.cs:265` (sparse arm only; not BM25 at `:220`), `MmrBehavior.cs:40`, `RedundancyFilterBehavior.cs:28`, `ContextualCompressionRetrievalBehavior.cs:38`; `src/Rag.NET/Retrieval/TagRetriever.cs:82`; `src/Rag.NET/Retrieval/DeepResearchRetriever.cs:84` and `:179`. Line numbers point at the cancellation rethrow the new line follows. The sufficiency check at `:179` has a `JsonException` catch above the catch-all; put the rethrow before both.
- Test: existing `CorrectiveRagBehaviorTests`, `AdaptiveRetrievalBehaviorTests`, `EnsembleBehaviorTests`, `ContextualCompressionRetrievalBehaviorTests`, `TagRetrieverTests`, `DeepResearchRetrieverTests`; new `tests/Rag.NET.Tests/Retrieval/Behaviors/RefusalPropagationTests.cs` for MultiQuery, Reranking, MMR and RedundancyFilter, which have no test file of their own.

- [ ] **Step 1:** One test per site, named `<Method>_RethrowsRefusal` (two for MultiQuery: `HandleAsync_ExpansionRefusal_Propagates` and `HandleAsync_VariantRetrievalRefusal_Propagates`; two for DeepResearch: sub-query and sufficiency). Each makes the model dependency throw the refusal: the chat client, embedder, sparse embedder, `IReranker`, `IQueryExpander` or `IContextualCompressor`. Each asserts `Assert.Same(refusal, await Assert.ThrowsAsync<BudgetExceededException>(...))`. The ContextualCompression test makes the compressor throw the refusal, which pins Review Focus 1.
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~Retrieval"`. Expected: the new tests FAIL.
- [ ] **Step 3:** Add the rethrows.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(retrieval): let a refusal through the degrading retrieval behaviours`.

### Task 4: Cache behaviours stop re-running the inner pipeline

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/InnerPipelineException.cs`
- Modify: `src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs:31-50`, `ResultCacheBehavior.cs` (the same block)
- Test: `tests/Rag.NET.Tests/Retrieval/Behaviors/EmbeddingCacheBehaviorTests.cs`; new `ResultCacheBehaviorTests.cs` beside it

**Interfaces:**
- Produces: `internal sealed class InnerPipelineException(Exception inner) : Exception(null, inner)`, the marker the factory throws around anything `next()` throws.

The factory wraps `next()` in `try { ... } catch (Exception ex) when (ex is not OperationCanceledException) { throw new InnerPipelineException(ex); }`. The outer handler gets `catch (InnerPipelineException wrapped) { ExceptionDispatchInfo.Throw(wrapped.InnerException!); }` before the cache-failure catch-all, so the original exception keeps its stack and type. Use a wrapper, not a local flag, so a stampede joiner that never ran the factory also recognises it (Review Focus 2). Doc-comment the class with that reason.

- [ ] **Step 1:** Tests, for each of the two behaviours:
  - `HandleAsync_InnerFailure_PropagatesWithoutRerunning`: `next` throws `InvalidOperationException("downstream")`. The same instance propagates, and `next` ran exactly once.
  - `HandleAsync_InnerRefusal_Propagates`: `next` throws the refusal; `Assert.Same`.
  - `HandleAsync_CacheFailure_FallsBackToNextOnce`: a `HybridCache` substitute whose `GetOrCreateAsync` throws without invoking the factory. The result comes from `next`, which ran exactly once.
  - `HandleAsync_ConcurrentJoiner_SeesInnerFailure`: use a real `HybridCache` from `services.AddHybridCache()`. Run two `HandleAsync` calls with the same key, where `next` blocks on a `TaskCompletionSource` and then throws. Both calls throw the original exception, and `next` ran once.
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~CacheBehavior"`. Expected: the inner-failure, refusal and joiner tests FAIL, because today `next` runs twice.
- [ ] **Step 3:** Implement as above.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(retrieval): stop the cache behaviours re-running the pipeline when it throws`.

### Task 5: Contextual compressors

**Files:**
- Modify: `src/Rag.NET.QueryTechniques/ContextualCompression/LlmAbstractiveCompressor.cs:62`, `ExtractiveCompressor.cs:65` and `:109`
- Test: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/LlmAbstractiveCompressorTests.cs`, `ExtractiveCompressorTests.cs`

- [ ] **Step 1:** `CompressAsync_RethrowsRefusal` for the abstractive compressor (the chat client refuses). For the extractive compressor, `CompressAsync_QueryEmbeddingRefusal_Propagates` (the embedder refuses on the first call) and `CompressAsync_SourceEmbeddingRefusal_Propagates` (the embedder succeeds for the query, then refuses).
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~Compressor"`. Expected: the new tests FAIL.
- [ ] **Step 3:** Add the rethrows.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(query-techniques): let a refusal through the contextual compressors`.

### Task 6: Answer engines

**Files:**
- Modify: `src/Rag.NET.AnswerEngines/SelfAssessmentConfidenceScorer.cs:65`, `RefineAnswerEngine.cs:94`, `MapReduceAnswerEngine.cs:209`, `FlareAnswerEngine.cs:194` and `:243`
- Test: `tests/Rag.NET.Tests/AnswerGeneration/SelfAssessmentConfidenceScorerTests.cs`, `RefineAnswerEngineTests.cs`, `MapReduceAnswerEngineTests.cs`, `FlareAnswerEngineTests.cs`

- [ ] **Step 1:** Tests:
  - `ScoreAsync_RethrowsRefusal`.
  - `AskAsync_RefineStepRefusal_Propagates`: the first answer call succeeds, the refine call refuses.
  - `AskAsync_MapStepRefusal_Propagates`.
  - `AskAsync_ScorerRefusal_Propagates` and `AskAsync_LookaheadRefusal_Propagates` (FLARE; the lookahead's retriever refuses).
  - `AskStreamingAsync_LookaheadRefusal_Propagates` (FLARE streaming delegates to `AskAsync`; this pins it).
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~AnswerGeneration"`. Expected: the new tests FAIL.
- [ ] **Step 3:** Add the rethrows.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(answer-engines): let a refusal through FLARE, refine, map-reduce and self-assessment`.

### Task 7: Conversation memory

**Files:**
- Modify: `src/Rag.NET/Memory/ConversationMemoryPipeline.cs:96`, `src/Rag.NET.Memory/PersistentConversationMemory.cs:144` (search only; the persist catch at `:125` wraps a store write after the embedding, so check it too: if its `try` includes the embedder call, rethrow there as well and add a test)
- Test: `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs`, `PersistentConversationMemoryTests.cs`

- [ ] **Step 1:** `Summary_RethrowsRefusal` (the summarising chat client refuses) and `SearchAsync_RethrowsRefusal` (the embedder refuses). Use the public method names those classes expose.
- [ ] **Step 2:** Run `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~Memory"`. Expected: the new tests FAIL.
- [ ] **Step 3:** Add the rethrows.
- [ ] **Step 4:** Re-run. Expected: all pass.
- [ ] **Step 5:** Commit `fix(memory): let a refusal through conversation summary and memory search`.

### Task 8: Docs, sweep check, full verification

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/ModelCallException.cs:44-46`, `docs/guide/resilience.md:150`, `docs/guide/retrieval.md:287`

- [ ] **Step 1:** In `ModelCallException`, replace "The same degrading catches on the query path do not yet; that is tracked as #948." with a sentence saying the query path's degrading catches do the same, so a refusal fails the query (#948). In `resilience.md`, replace the "The query path does not do this yet…" sentence: a refusal on the query path fails the query (#948). `RetrieveAsync` returns `RagError.StorageFailed` with the refusal as `Inner`, and `AskAsync` throws an `InvalidOperationException`, until #937. In `retrieval.md:287`, "it fails the document" becomes "it fails the document or the query".
- [ ] **Step 2:** Sweep check. Run `grep -rn -A1 "catch (OperationCanceledException)" src --include=*.cs | grep -v /obj/` over `src/Rag.NET/Retrieval`, `src/Rag.NET.AnswerEngines`, `src/Rag.NET.QueryTechniques`, `src/Rag.NET.Security`, `src/Rag.NET/Memory`, `src/Rag.NET.Memory`. Every site in the design's two tables has the rethrow, and no site outside them got one.
- [ ] **Step 3:** Run every test project: `for p in $(ls -d tests/*/ | grep -v "Testing/\|IntegrationTests\|E2ETests"); do dotnet test "$p" || echo "FAILED $p"; done`. Expected: no FAILED lines. Then, because the docs' C# fences are compiled against the packages, run pack-validate the way `ci.yml` does: `dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version=0.0.0-local` followed by `dotnet test tests/Rag.NET.PackageValidation.Tests -c Release`. Check the job for any env var it sets that names the package version; if one exists, set it to the same value.
- [ ] **Step 4:** Commit `docs(core): say the query path's degrading catches now let a refusal through`.
