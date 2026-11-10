# A deliberate refusal fails the document — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `CallRefusedException` thrown inside any degrading ingestion component propagates to `PipelineIngestor` and fails the document.

**Architecture:** One line per site: `catch (CallRefusedException) { throw; }` directly after the existing `OperationCanceledException` rethrow and before every other catch. Genuine failures keep degrading exactly as today.

**Tech Stack:** .NET, xUnit v3, NSubstitute.

**Spec:** `docs/plans/2026-11-09-refusal-fails-document-design.md`

## Global Constraints

- Branch `fix/refusal-fails-document`, based on `main`. Commit identity: `-c user.name="Marcel Roozekrans" -c user.email="claude@roozekrans.nl"`.
- Conventional commits, header ≤ 100 chars, no `Co-Authored-By: Claude` trailer, no session links, no nested parentheses in bodies.
- The refusal used in every test: `new BudgetExceededException(CostWindow.Day, 1m, 1m)`; assert with `Assert.Same(refusal, ex)` so it is proven untouched, not wrapped.
- Rule order at every site: OCE rethrow → `CallRefusedException` rethrow → anything else. A `using Rag.NET.Models;` may be needed.
- Do not touch the query path (#948) or add a `RagError` case (#937).

## Review Focus

- A genuine provider failure (`InvalidOperationException`, `HttpRequestException`) at each site still degrades. The existing fallback tests cover it; they must still pass unchanged.
- A refusal from a sync-over-async sanitiser surfaces as the refusal itself, not an `AggregateException`: `GetAwaiter().GetResult()` unwraps it, and the sanitiser tests assert the exact type.
- A refusal on the second of several calls (late chunker sections, tag loop, proposition passages) still stops the document; the tests throw on every call, so the first call is enough to prove propagation.
- `LlmChunkSanitiser` must not fall back to the regex sanitiser on a refusal; the test asserts the throw, which excludes a returned string.
- End to end, the refusal arrives at `PipelineIngestor` as `RagError.StorageFailed` with `Inner` being the same refusal (Task 1).

---

### Task 1: Core ingestion behaviours, plus the end-to-end proof

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/LlmMetadataExtractionBehavior.cs:83`
- Modify: `src/Rag.NET/Ingestion/Behaviors/TagIngestionBehavior.cs:45`
- Modify: `src/Rag.NET/Ingestion/Behaviors/SparseEmbeddingBehavior.cs:43-46`
- Test: `tests/Rag.NET.Tests/Ingestion/Behaviors/LlmMetadataExtractionBehaviorTests.cs`, `tests/Rag.NET.Tests/Ingestion/TagIngestionBehaviorTests.cs`, `tests/Rag.NET.Tests/Ingestion/Behaviors/SparseEmbeddingBehaviorTests.cs`, `tests/Rag.NET.Tests/Ingestion/PipelineIngestorResultTests.cs`

- [ ] **Step 1: Write the failing tests**, one per behaviour, named `HandleAsync_CallRefusal_PropagatesUnwrapped`: build the behaviour as its existing tests do, with the chat client / embedder / sparse generator substitute throwing `refusal`; `var ex = await Assert.ThrowsAsync<BudgetExceededException>(...)`; `Assert.Same(refusal, ex)`.
  In `PipelineIngestorResultTests` add `IngestAsync_RefusalInsideADegradingBehaviour_FailsTheDocument`, shaped like the existing refusal test at line 92 but with `SparseEmbeddingBehavior` (sparse generator throwing `refusal`) in place of `EmbeddingBehavior`: `Assert.False(result.IsSuccess)`, `var error = Assert.IsType<RagError.StorageFailed>(result.Error)`, `Assert.Same(refusal, error.Inner)`.
- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~CallRefusal|FullyQualifiedName~RefusalInsideADegradingBehaviour"`. Expected: 4 failures (no exception thrown / result is success).
- [ ] **Step 3: Add the rethrow** at the three sites.
- [ ] **Step 4: Run the Rag.NET.Tests project in full.** Expected: all pass, including the existing fallback tests.
- [ ] **Step 5: Commit** `fix(ingestion): let a refusal through metadata, tag and sparse embedding behaviours`.

### Task 2: Chunkers

**Files:**
- Modify: `src/Rag.NET.Chunking/PropositionChunkingStrategy.cs:56`, `src/Rag.NET.Chunking/LateChunkingStrategy.cs:47`, `src/Rag.NET.Chunking.Templates/ResumeChunkingStrategy.cs:42`
- Test: `tests/Rag.NET.Tests/Chunking/PropositionChunkingStrategyTests.cs`, `tests/Rag.NET.Tests/Chunking/LateChunkingStrategyTests.cs`, `tests/Rag.NET.Chunking.Templates.Tests/ResumeChunkingStrategyTests.cs`

- [ ] **Step 1: Write the failing tests**, `ChunkAsync_CallRefusal_PropagatesUnwrapped` in each file, mirroring `SemanticChunkingStrategyTests.ChunkAsync_CallRefusal_PassesThroughUnwrapped`: the chat client (proposition, resume) or token embedder (late) throws `refusal`; enumerate the result fully; assert the exact instance.
- [ ] **Step 2: Run and see them fail** (filter `~CallRefusal` in both test projects). Expected: 3 failures.
- [ ] **Step 3: Add the rethrow.** In `LateChunkingStrategy` it goes before the `TokenEmbeddingContractException` catch.
- [ ] **Step 4: Run `Rag.NET.Tests` and `Rag.NET.Chunking.Templates.Tests` in full.** Expected: all pass.
- [ ] **Step 5: Commit** `fix(chunking): let a refusal through the proposition, late and resume chunkers`.

### Task 3: GraphRAG

**Files:**
- Modify: `src/Rag.NET.GraphRag/MindMapExtractor.cs:59-62`
- Test: `tests/Rag.NET.GraphRag.Tests/MindMapExtractorTests.cs`, `tests/Rag.NET.GraphRag.Tests/GraphEntityExtractionBehaviorTests.cs`

- [ ] **Step 1: Write the tests.** `ExtractAsync_CallRefusal_PropagatesUnwrapped` in `MindMapExtractorTests` (chat client throws `refusal`; exact instance). In `GraphEntityExtractionBehaviorTests` add `HandleAsync_ChatCallRefusal_PropagatesUnwrapped`: the extraction chat client throws `refusal`; exact instance. This one pins today's behaviour and is expected to pass already.
- [ ] **Step 2: Run them.** Expected: the mind-map test fails, the graph test passes.
- [ ] **Step 3: Add the rethrow** in `MindMapExtractor`.
- [ ] **Step 4: Run `Rag.NET.GraphRag.Tests` in full.** Expected: all pass.
- [ ] **Step 5: Commit** `fix(graphrag): let a refusal through mind-map extraction`.

### Task 4: LLM chunk sanitisers

**Files:**
- Modify: `src/Rag.NET.Security/LlmChunkSanitiser.cs:51`, `src/Rag.NET.Security/Pii/LlmPiiChunkSanitiser.cs:56`
- Test: `tests/Rag.NET.Security.Tests/LlmChunkSanitiserTests.cs`, `tests/Rag.NET.Security.Tests/LlmPiiChunkSanitiserTests.cs`

- [ ] **Step 1: Write the failing tests**, `Sanitise_CallRefusal_PropagatesUnwrapped`: chat client throws `refusal`; `var ex = Assert.Throws<BudgetExceededException>(() => sut.Sanitise(text, metadata))`; `Assert.Same(refusal, ex)`.
- [ ] **Step 2: Run and see them fail.** Expected: 2 failures (the regex fallback returned a string).
- [ ] **Step 3: Add the rethrow** at both sites.
- [ ] **Step 4: Run `Rag.NET.Security.Tests` in full.** Expected: all pass.
- [ ] **Step 5: Commit** `fix(security): let a refusal through the LLM chunk sanitisers`.

### Task 5: Docs and full verification

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/ModelCallException.cs` (the "Which components throw this" paragraph)

- [ ] **Step 1: Update the remarks.** Replace "That catch also takes a deliberate refusal, which is tracked as #938." with a sentence saying those catches rethrow a `CallRefusedException` first, so a refusal fails the document (#938), while the same catches on the query path are tracked as #948.
- [ ] **Step 2: Run every test project.** Enumerate them with `dotnet sln Rag.NET.slnx list | grep Tests.csproj` and run each one. Expected: all pass, except the known local-only failures recorded in STATE.md (`WhatIsMyAddress_WithReporterSettings_*`, BEIR skips).
- [ ] **Step 3: Run pack-validate** the way CI does (see `.github/workflows/ci.yml` `pack-validate`), because docs fences and packaging guards are not reached by `dotnet build`.
- [ ] **Step 4: Commit** `docs(core): say the degrading catches now let a refusal through`.
- [ ] **Step 5: Open the PR.** Title: `fix(ingestion): stop a document when a call is refused, instead of ingesting it degraded`. Body: budget-limited ingestion now stops when the budget is spent; until #937 the failure reports as `StorageFailed` with the refusal inside; closes #938; query path in #948. The operator merges.
