# Call-refused marker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `CallRefusedException`, a marker for deliberate refusals, so call-site wraps can let a refusal through and report every other failure as a `ModelCallException` (#930 layer 1).

**Architecture:** `CallRefusedException : InvalidOperationException` lives in `Rag.NET.Abstractions`. `BudgetExceededException` and the benchmark caches' refusals derive from it or throw it. The three places that exempt `BudgetExceededException` by type exempt the marker instead. Four model-calling sites get a four-step wrap: cancellation, refusal and `ModelCallException` are rethrown, and anything else becomes a `ModelCallException`. `PipelineIngestor` already maps `ModelCallException` to `RagError.ModelCallFailed`, so it does not change.

**Tech Stack:** .NET, xUnit v3, NSubstitute, Microsoft.Extensions.AI.

**Spec:** `docs/plans/2026-11-09-call-refused-marker-design.md`

## Global Constraints

- Branch `feat/call-refused-marker`. The operator merges; do not merge.
- Additive public API only: `feat`, no `!`.
- Commit headers stay within 100 characters (commitlint). No nested parentheses in commit bodies (release-please). No `Co-Authored-By` trailer and no session links.
- The wrap order is fixed: `OperationCanceledException` → `CallRefusedException` → `ModelCallException` → anything else. A failure is translated, never swallowed.
- Every translated `ModelCallException` keeps the original exception as `InnerException`.
- Before pushing, run every test project. Enumerate them with `find tests -name '*.csproj'`; don't list them by hand.

## Review Focus

1. **A wrong-count result from an embedder.** `EmbeddingBehavior` checks that the generator returned one vector per text. The design moves that check inside the wrap, so a count mismatch becomes a `ModelCallException`. Pinned in Task 4.
2. **A `BudgetExceededException` raised under a nested wrap.** For example, RAPTOR's embedder wrapped in `CostTrackingEmbeddingGenerator`. It must arrive as `BudgetExceededException`, not as a `ModelCallException` around it. Pinned in Tasks 4–7 by the refusal pass-through test at each site.
3. **A wrap inside a wrap:** an embedder that already throws `ModelCallException`, such as a vision-style subclass. It must not be wrapped twice. Pinned in Task 4.
4. **Cancellation during `Parallel.ForEachAsync`**, in batched embedding and community reports. The caller's token cancels, and the exception must stay an `OperationCanceledException`. Pinned in Tasks 4 and 7.
5. **The GraphRAG replay guards in the integration tests.** `GraphRagReportReplayTests` pins a refuse-on-miss throw through `CommunityDetectionBehavior`. These are the guards that failed in #508. They need the BEIR cache, which `source ~/.cache/ragnet-beir/env.sh` provides. Run them in Task 7.

---

### Task 1: The marker, and `BudgetExceededException` on it

**Files:**
- Create: `src/Rag.NET.Abstractions/Models/CallRefusedException.cs`
- Modify: `src/Rag.NET.Abstractions/Models/BudgetExceededException.cs`
- Modify: `src/Rag.NET.Abstractions/Models/ModelCallException.cs` (the remarks)
- Test: `tests/Rag.NET.Tests/Models/CallRefusedExceptionTests.cs`

**Interfaces:**
- Produces: `public class CallRefusedException : InvalidOperationException` in namespace `Rag.NET.Models`, with constructors `(string message)` and `(string message, Exception? innerException)`. `BudgetExceededException : CallRefusedException`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void BudgetExceededException_IsACallRefusal_AndStillAnInvalidOperationException()
{
    Exception ex = new BudgetExceededException(CostWindow.Day, limit: 1m, spend: 1m);
    Assert.IsAssignableFrom<CallRefusedException>(ex);
    Assert.IsAssignableFrom<InvalidOperationException>(ex);
}

[Fact]
public void CallRefusedException_KeepsMessageAndInner()
{
    var inner = new IOException("x");
    var ex = new CallRefusedException("refused", inner);
    Assert.Equal("refused", ex.Message);
    Assert.Same(inner, ex.InnerException);
}
```

- [ ] **Step 2: Run them and confirm they fail to compile**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~CallRefusedExceptionTests"`. Expected: a build error, because `CallRefusedException` does not exist yet.

- [ ] **Step 3: Implement**

- Add `CallRefusedException`. Its summary states the contract from the design: thrown *instead of* calling a model, on purpose, by this library or by a decorator around a client. It is never a provider failure and must never be retried, fallen back on, or translated into `ModelCallException`.
- Change `BudgetExceededException`'s base from `InvalidOperationException` to `CallRefusedException`, keeping the computed message.
- Rewrite the last two `<para>` blocks of `ModelCallException`'s remarks:
  - the seam now exists, and wraps rethrow `CallRefusedException` before translating;
  - the components that throw this: the vision parser, `EmbeddingBehavior`, `SemanticChunkingStrategy`, `RaptorIngestionBehavior`, `CommunityDetectionBehavior`;
  - `LateChunkingStrategy` degrades locally, like the components in the earlier paragraph.

- [ ] **Step 4: Run the tests and confirm they pass**

- [ ] **Step 5: Commit**: `feat(core): add CallRefusedException as the base for deliberate refusals`

### Task 2: Exempt the marker where `BudgetExceededException` was exempted

**Files:**
- Modify: `src/Rag.NET.Resilience/FallbackChatClient.cs:274`
- Modify: `src/Rag.NET.Resilience/ResilienceBuilderExtensions.cs:207-213`, the code and its comment
- Modify: `src/Rag.NET.Parsers.Vision/ImageDocumentParser.cs:78`, the catch and its comment
- Modify: `docs/guide/resilience.md:193` and `:211`: say "any `CallRefusedException`, of which `BudgetExceededException` is one"
- Test: `tests/Rag.NET.Resilience.Tests/Resilience/FallbackChatClientTests.cs`, `tests/Rag.NET.Resilience.Tests/DependencyInjection/ConfigureResilienceTests.cs`, `tests/Rag.NET.Parsers.Vision.Tests/VisionProviderFailureTests.cs`

**Interfaces:**
- Consumes: `CallRefusedException` (Task 1).

- [ ] **Step 1: Write the failing tests.** Add one test next to each existing budget test:
  - `IsTransient_CallRefusedException_IsNotTransient`, which asserts `Assert.False(FallbackChatClient.IsTransient(new CallRefusedException("cache miss: upstream unavailable")))`. The message contains the transient keyword "unavailable", so the test proves that the type check runs before the keyword match.
  - `CallRefusedException_IsNotRetried`, a copy of `BudgetExceededException_IsNotRetried` at `ConfigureResilienceTests.cs:551` that throws `new CallRefusedException("refused")` and asserts one attempt.
  - In `VisionProviderFailureTests`, a copy of the budget pass-through test that throws `new CallRefusedException("refused")` and asserts `Assert.ThrowsAsync<CallRefusedException>`, the exact type, not wrapped.
- [ ] **Step 2: Run them and confirm all three fail**
- [ ] **Step 3: Replace `BudgetExceededException` with `CallRefusedException`** in the three checks, and update the comments to name the marker.
- [ ] **Step 4: Run the three test projects and confirm they pass**
- [ ] **Step 5: Commit**: `feat(resilience): never retry, fall back on, or translate any deliberate refusal`

### Task 3: The benchmark caches throw the marker

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/Rag.NET.Benchmarks.Quality.csproj`: add `<ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />`
- Modify: `src/Rag.NET.Benchmarks.Quality/EmbeddingCache.cs:218` and `:251`
- Modify: `src/Rag.NET.Benchmarks.Quality/GraphExtractionCache.cs:283` (`MissingEntry`)
- Modify: `src/Rag.NET.Benchmarks.Quality/HypotheticalCache.cs:191` (`MissingEntry`)
- Modify: `benchmarks/Rag.NET.Benchmarks.Quality.GraphExtractions/CachedGraphRagClient.cs:267` (no model to fill a miss), `:417` (an unrenderable response format) and `:476` (`Unkeyable`)
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/{EmbeddingCacheTests,GraphExtractionCacheTests,HypotheticalCacheTests,CachedGraphRagClientOptionsTests}.cs` and `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/GraphRagReportReplayTests.cs:89,108`

**Interfaces:**
- Consumes: `CallRefusedException` (Task 1).

The five refusals that come after a real model call keep `InvalidOperationException`, and so do their tests:
- `EmbeddingCache:298`, `:310`
- `GraphExtractionCache:264`
- `HypotheticalCache:179`
- `CachedGraphRagClient:302`

The streaming `NotSupportedException` at `CachedGraphRagClient:209` also stays.

- [ ] **Step 1: Change the tests that pin a refusal.** Each `ThrowsAsync<InvalidOperationException>` or `Throws<InvalidOperationException>` that exercises one of the refusals listed above becomes `ThrowsAsync<CallRefusedException>`, or `Throws<CallRefusedException>`. xUnit matches the exact type. Leave the blank-text and wrong-count tests unchanged. Those exact-type assertions are what prove a post-call failure was not marked.
- [ ] **Step 2: Run `tests/Rag.NET.Benchmarks.Quality.Tests` and confirm the changed tests fail**
- [ ] **Step 3: Throw `CallRefusedException` at the refusal sites.** Change the `MissingEntry` and `Unkeyable` factories' return type to `CallRefusedException`.
- [ ] **Step 4: Run `tests/Rag.NET.Benchmarks.Quality.Tests` and confirm it passes.** Then `source ~/.cache/ragnet-beir/env.sh` and run `GraphRagReportReplayTests` in `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`. If the env file is missing, say so; don't call the cache unprovisioned without checking.
- [ ] **Step 5: Commit**: `feat(bench): mark refuse-on-miss cache throws as deliberate refusals`

### Task 4: Wrap `EmbeddingBehavior`

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs`: `EmbedSingleBatchAsync` (`:89`) and the batch lambda in `EmbedBatchedAsync` (`:124`)
- Test: `tests/Rag.NET.Tests/Ingestion/Behaviors/EmbeddingBehaviorTests.cs`

**Interfaces:**
- Consumes: `CallRefusedException`, `ModelCallException(string message, Exception innerException)`.

The wrap covers the `GenerateAsync` call and the count check after it, so a count mismatch becomes a model failure. The message names the document. In the batched path it also names the batch: `$"The embedding model failed for document '{id}' (batch {b}/{n})."`. The count-check exception becomes the `InnerException`.

- [ ] **Step 1: Write the failing tests.** Run each on both paths: one document at or below `EmbedBatchSize` and one above it. Use `[Theory]` with the pending-chunk count as data.
  - `ProviderFailure_SurfacesAsModelCallException`: the embedder throws `new HttpRequestException("429")`. Assert `ThrowsAsync<ModelCallException>`, and that `InnerException` is the same instance as the thrown one.
  - `CallRefusal_PassesThroughUnwrapped`: the embedder throws `new BudgetExceededException(CostWindow.Day, 1m, 1m)`. Assert `ThrowsAsync<BudgetExceededException>`.
  - `ExistingModelCallException_IsNotWrappedTwice`: the embedder throws `new ModelCallException("inner", new IOException())`. Assert the caught exception is the same instance.
  - `Cancellation_PassesThrough`: cancel the token, and have the embedder throw `OperationCanceledException(token)`. Assert `ThrowsAnyAsync<OperationCanceledException>`.
  - `WrongVectorCount_SurfacesAsModelCallException`: the embedder returns one vector fewer than the number of texts. Assert `ModelCallException`, with an `InvalidOperationException` inner whose message names the counts.
- [ ] **Step 2: Run them and confirm the provider-failure and wrong-count tests fail.** The pass-through tests may already pass.
- [ ] **Step 3: Implement the four-step wrap at both sites**
- [ ] **Step 4: Run `tests/Rag.NET.Tests` and confirm it passes**
- [ ] **Step 5: Commit**: `feat(core): report a failed embedding call as ModelCallException`

### Task 5: Wrap `SemanticChunkingStrategy`

**Files:**
- Modify: `src/Rag.NET.Chunking/SemanticChunkingStrategy.cs:82` (the sentence path) and `:128` (the document path)
- Test: `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs` and `SemanticChunkingStrategyDocumentTests.cs`

**Interfaces:**
- Consumes: as in Task 4.

Both calls sit inside async iterators. `yield return` cannot appear inside a `try` that has a `catch`, but the `GenerateAsync` call itself is not inside a yield. Wrap only the awaited call and assign its result to a local. Give the message the section's `DocumentId`.

- [ ] **Step 1: Write the failing tests.** In each file, add provider failure → `ModelCallException`, refusal → `BudgetExceededException` passes through, and an existing `ModelCallException` → the same instance. Cancellation is already pinned at `SemanticChunkingStrategyTests.cs:204`.
- [ ] **Step 2: Run them and confirm the provider-failure tests fail**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run `tests/Rag.NET.Tests` and confirm it passes**
- [ ] **Step 5: Commit**: `feat(chunking): report a failed semantic-chunking embedding call as ModelCallException`

### Task 6: Wrap `RaptorIngestionBehavior`

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs:497-502` (`SummarizeClusterAsync`)
- Test: `tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs`

**Interfaces:**
- Consumes: as in Task 4.

Wrap the chat call and the embedding call separately, so each message names its own stage:
- `$"The model could not summarise RAPTOR cluster {clusterId} at level {level}."`
- `$"The embedding model failed for the summary of RAPTOR cluster {clusterId} at level {level}."`

- [ ] **Step 1: Write the failing tests.** Use the file's existing chat-client and embedder fakes. For each of the chat and embedding calls, write three tests:
  - provider failure → `ModelCallException`, and the message contains "summarise" or "embedding" respectively;
  - `BudgetExceededException` → passes through;
  - `CallRefusedException("cache miss")` → passes through as that exact type. This is the #508 regression.
- [ ] **Step 2: Run them and confirm the provider-failure tests fail**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run `tests/Rag.NET.Raptor.Tests` and confirm it passes**
- [ ] **Step 5: Commit**: `feat(raptor): report a failed summary or embedding call as ModelCallException`

### Task 7: Wrap `CommunityDetectionBehavior`

**Files:**
- Modify: `src/Rag.NET.GraphRag/CommunityDetectionBehavior.cs:254`, inside the `Parallel.ForEachAsync` lambda
- Test: `tests/Rag.NET.GraphRag.Tests/CommunityDetectionBehaviorTests.cs`

**Interfaces:**
- Consumes: as in Task 4.

The message is `$"The model could not write the report for community {communities[i].Id}."`.

- [ ] **Step 1: Write the failing tests:**
  - provider failure → `ModelCallException`;
  - `CallRefusedException` → passes through as that exact type;
  - `BudgetExceededException` → passes through;
  - cancellation through the caller's token → `OperationCanceledException`.
- [ ] **Step 2: Run them and confirm the provider-failure test fails**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run `tests/Rag.NET.GraphRag.Tests` and confirm it passes.** Then run `GraphRagReportReplayTests` and the rest of `Rag.NET.Benchmarks.Quality.IntegrationTests` with the BEIR env sourced. These are the guards #508 broke.
- [ ] **Step 5: Commit**: `feat(graphrag): report a failed community-report call as ModelCallException`

### Task 8: Full verification

- [ ] Run `dotnet build Rag.NET.slnx -warnaserror`. Expected: 0 warnings and 0 errors.
- [ ] Run every project from `find tests -name '*.csproj'`. Expected: all green, except the local-only `WhatIsMyAddress_WithReporterSettings_*` E2E failure that STATE.md records.
- [ ] Run the repo's `pack-validate` step as CI defines it in `.github/workflows/ci.yml`. It is the only check that compiles the docs examples and the packaging guards.
