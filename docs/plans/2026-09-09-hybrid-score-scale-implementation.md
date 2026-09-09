# Hybrid Score Scale Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the two stores that return backend-fused hybrid scores declare that scale through the capability system, and stop applying a similarity-shaped `MinScore` to it.

**Architecture:** `IHybridSearchable` gains a defaulted `HybridScoreScale` returning `ScoreScale.OpaqueRanking` — correct for every implementer that exists, since a native hybrid fuses. Azure AI Search and Weaviate stop passing `options.MinScore` into their hybrid result mapping, and say so in their remarks. `IScoreScaleAware` is untouched and keeps meaning the dense path.

**Tech Stack:** .NET 10, C#, xunit v3, Testcontainers (Weaviate container, Azure AI Search simulator).

**Spec:** `docs/plans/2026-09-09-hybrid-score-scale-design.md` — read it first. Its §0 carries a **correction**: the retrieval pipeline already refuses the native path when `MinScore` is non-zero, so this is a direct-caller and API-clarity fix, not a live pipeline defect. Do not restate it as a live defect anywhere.

## Global Constraints

- **Issues:** #530. **Phase:** 6.2.33, **part 1 of 2**.
- **#328's semantic ranker is part 2 and is NOT in this plan.** The phase covers both, and the split is deliberate rather than a narrowing: #530's half is fully verifiable against the local containers, while #328's needs a real Azure resource (Basic tier or higher, billable) and ships behind a guard with `<VerifiedByReason>`. Mixing a locally-provable change with an unverifiable one in a single plan would let the second hide behind the first's green run. Part 2 gets its own plan once this merges.
- **This is scoped to the hybrid path only.** The dense path's `MinScore` must keep working on both stores. That capability regressing is the single worst outcome of this change.
- **Do not change `IScoreScaleAware`, `ScoreScale`, or any consumer.** No caller is taught to probe `HybridScoreScale` in this phase — that is recorded as deliberately out of scope in the design's §5.
- **`HybridScoreScale` is a default interface member.** In C# these are **not** inherited into the implementing class's public surface: `store.HybridScoreScale` will not compile on a concrete reference, `((IHybridSearchable)store).HybridScoreScale` will. Tests must access it through the interface. This is expected, not a problem to work around.
- **Conventional commits, header at most 100 characters.** CI lints every commit a PR adds.
- **Test command:** `dotnet test tests/Rag.NET.VectorStores.Weaviate.Tests -c Release` and the same for `tests/Rag.NET.VectorStores.AzureAISearch.Tests`. Docker must be running; both start containers.
- **`--filter` works normally on these two projects.** Only `Rag.NET.Benchmarks.Quality.IntegrationTests` ignores it (#529).
- **`MetadataValue`'s constructors are private** — use the public implicit conversions if any test needs one.
- **`RagSearchOptions` is an alias local to the store files** (`using RagSearchOptions = Rag.NET.Models.Options.SearchOptions;`), introduced to disambiguate from Azure's own `SearchOptions`. **The test projects do not use it** — they write `new SearchOptions { ... }` directly, and both existing test files already do. Use what the test file uses.

---

### Task 1: Declare the hybrid scale on the capability interface

**Files:**
- Modify: `src/Rag.NET.Abstractions/Abstractions/IHybridSearchable.cs`
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs` and `tests/Rag.NET.VectorStores.Weaviate.Tests/WeaviateVectorStoreTests.cs`

**Interfaces:**
- Consumes: `ScoreScale` from `Rag.NET.Abstractions` — same namespace as `IHybridSearchable`, so no using is needed.
- Produces: `ScoreScale HybridScoreScale { get; }` on `IHybridSearchable`, defaulted. Task 2 and Task 3 rely on the default rather than overriding it.

- [ ] **Step 1: Write the failing tests**

Both store test classes already have a fixture and a hybrid test; append to each. Azure:

```csharp
    /// <summary>
    /// The hybrid path's scores come from Azure's own fusion of BM25 and vector results, so they
    /// are ordinal rather than similarities and must not be thresholded. The store declares that
    /// through the capability system rather than leaving every caller to re-derive it — the
    /// retrieval pipeline already refuses the native path when a MinScore is set, and a direct
    /// caller of HybridSearchAsync deserves the same fact.
    /// </summary>
    [Fact]
    public void HybridScoreScale_IsOpaqueRanking()
    {
        // Accessed through the interface: a default interface member is not on the class's surface.
        Assert.Equal(ScoreScale.OpaqueRanking, ((IHybridSearchable)_sut).HybridScoreScale);
    }
```

Weaviate's is identical apart from the field name its fixture uses for the store under test — read the file and match it.

Add `using Rag.NET.Abstractions;` to each test file if it is not already imported.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~HybridScoreScale"`
Expected: **compile error** — `IHybridSearchable` has no `HybridScoreScale`. That is this task's RED.

- [ ] **Step 3: Add the member**

In `IHybridSearchable.cs`, inside the interface, above `HybridSearchAsync`:

```csharp
    /// <summary>
    /// The scale of the scores <see cref="HybridSearchAsync"/> returns. Defaults to
    /// <see cref="ScoreScale.OpaqueRanking"/>, which is what a native hybrid produces: the backend
    /// fuses a keyword ranking with a vector ranking, and a fused rank carries no similarity
    /// meaning — only order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="IScoreScaleAware.ScoreScale"/> because one store instance serves
    /// both paths.</b> The same store answers <see cref="IVectorStore.SearchAsync"/> with a genuine
    /// cosine similarity and this method with a fused score. That interface requires its value to be
    /// constant for the instance's lifetime, so a single property cannot describe both honestly.
    /// </para>
    /// <para>
    /// <b>Defaulted rather than required</b> because it is correct for every implementer that
    /// exists — a store whose hybrid query genuinely returns similarities overrides it and says why.
    /// </para>
    /// <para>
    /// <b>This is a declaration, not a filter.</b> Implementations must not apply
    /// <see cref="Rag.NET.Models.Options.SearchOptions.MinScore"/> to a score on this scale; a
    /// threshold shaped for similarities filters a fused rank arbitrarily.
    /// </para>
    /// </remarks>
    ScoreScale HybridScoreScale => ScoreScale.OpaqueRanking;
```

- [ ] **Step 4: Run both suites to verify GREEN**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release` then the Weaviate suite.
Expected: all pass, including the two new tests. **No store code has changed yet** — the default supplies the value.

- [ ] **Step 5: Commit**

```bash
git add src/Rag.NET.Abstractions/Abstractions/IHybridSearchable.cs tests/Rag.NET.VectorStores.AzureAISearch.Tests tests/Rag.NET.VectorStores.Weaviate.Tests
git commit -m "feat(abstractions): let a native hybrid declare its fused score scale (#530)"
```

---

### Task 2: Azure AI Search stops thresholding its fused score

**Files:**
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs` — `HybridSearchAsync` (the `ExecuteSearchAsync` call around line 369)
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

**Interfaces:**
- Consumes: `HybridScoreScale` from Task 1 (via the default).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to the Azure store test class. Read the existing hybrid test first and match its fixture, field names and how it stores chunks:

```csharp
    /// <summary>
    /// MinScore does not apply to a fused score. Azure fuses BM25 and vector rankings service-side
    /// and returns a rank-shaped score whose magnitude is not comparable to a cosine threshold, so
    /// applying one filters arbitrarily. A caller reaching HybridSearchAsync directly is the case
    /// that matters: the retrieval pipeline already keeps a request with a MinScore off this path.
    /// </summary>
    [Fact]
    public async Task HybridSearchAsync_DoesNotFilterByMinScore()
    {
        var ct = TestContext.Current.CancellationToken;
        await StoreTwoChunksAsync(ct);

        var results = await _sut.HybridSearchAsync(
            "alpha",
            new[] { 1f, 0f, 0f },
            new SearchOptions { TopK = 10, MinScore = 0.9 },
            ct);

        Assert.NotEmpty(results);
    }
```

**`StoreTwoChunksAsync` is a placeholder name, not a method that exists.** Read the existing hybrid test in this file — it already seeds the index and searches — and reuse exactly its seeding approach and field names. Assert on the count that seeded data justifies rather than on a number copied from here. **`MinScore = 0.9` is chosen to exceed any plausible fused score**; the assertion is that the page is not emptied by it.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~DoesNotFilterByMinScore"`
Expected: FAIL — the collection is empty, because `ExecuteSearchAsync` filters on `score < minScore`.

**If it passes before the change, stop and report it.** That would mean the simulator's fused scores happen to exceed 0.9, and the test proves nothing; the threshold needs raising or the assertion rethinking.

- [ ] **Step 3: Stop passing MinScore on the hybrid path**

In `HybridSearchAsync`, change the `ExecuteSearchAsync` call to pass no threshold:

```csharp
        // MinScore is deliberately not forwarded: Azure fuses BM25 and vector rankings service-side
        // and the resulting score is ordinal (IHybridSearchable.HybridScoreScale), so a
        // similarity-shaped threshold would filter it arbitrarily. The dense path below still
        // applies it, because there the score is a real cosine similarity.
        var results = await ExecuteSearchAsync(textQuery, searchOptions, minScore: 0.0, cancellationToken)
            .ConfigureAwait(false);
```

Leave `SearchAsync`'s call untouched — it must keep passing `options.MinScore`.

- [ ] **Step 4: Extend the method's remarks**

The class documents *why* on its members. State that `MinScore` does not apply on this path and why, so it is discoverable at the call site as well as through the capability probe. Two or three sentences, matching the file's density.

- [ ] **Step 5: Run the whole Azure suite**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release`
Expected: all pass. **Watch the dense-path tests especially** — if a `MinScore` test on `SearchAsync` fails, the change was applied to the wrong call.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.VectorStores.AzureAISearch tests/Rag.NET.VectorStores.AzureAISearch.Tests
git commit -m "fix(azureaisearch): stop applying MinScore to a fused hybrid score (#530)"
```

---

### Task 3: Weaviate stops thresholding its fused score

**Files:**
- Modify: `src/Rag.NET.VectorStores.Weaviate/WeaviateVectorStore.cs` — the `MapResults(hits, ScoreFromHybridScore, options.MinScore)` call at line 142
- Test: `tests/Rag.NET.VectorStores.Weaviate.Tests/WeaviateVectorStoreTests.cs`

**Interfaces:**
- Consumes: `HybridScoreScale` from Task 1 (via the default).
- Produces: nothing.

- [ ] **Step 1: Write the failing test**

Mirror Task 2's test against the Weaviate fixture — read the existing hybrid test in this file and match how it seeds objects and what it queries. Same shape: seed, hybrid-search with `MinScore = 0.9`, assert the page is not empty. The doc comment should name Weaviate's own fusion rather than copying Azure's wording.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Rag.NET.VectorStores.Weaviate.Tests -c Release --filter "FullyQualifiedName~DoesNotFilterByMinScore"`
Expected: FAIL — empty collection, filtered at `WeaviateVectorStore.cs:412`.

Same instruction as Task 2: **if it passes before the change, stop and report it.**

- [ ] **Step 3: Stop passing MinScore on the hybrid path**

```csharp
        // MinScore is deliberately not forwarded: this score is Weaviate's own hybrid fusion of a
        // keyword ranking and a vector ranking (IHybridSearchable.HybridScoreScale), so it is
        // ordinal and a similarity-shaped threshold would filter it arbitrarily. SearchAsync above
        // still applies it, where the score is a converted cosine distance.
        var results = MapResults(hits, ScoreFromHybridScore, minScore: 0.0);
```

Leave the dense call at line 116 untouched.

- [ ] **Step 4: Extend the method's remarks**, as in Task 2.

- [ ] **Step 5: Run the whole Weaviate suite**

Run: `dotnet test tests/Rag.NET.VectorStores.Weaviate.Tests -c Release`
Expected: all pass, dense-path `MinScore` behaviour included.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.VectorStores.Weaviate tests/Rag.NET.VectorStores.Weaviate.Tests
git commit -m "fix(weaviate): stop applying MinScore to a fused hybrid score (#530)"
```

---

### Task 4: Mutation sweep

A declaration is exactly the kind of change that can be reverted without any test noticing. On every backend in #318 this sweep found something.

**Files:** nothing permanently — each mutation is applied, tested, then reverted with `git checkout --`.

- [ ] **Step 1: Run each mutation and record the catcher**

| # | mutation | expected catcher |
| --- | --- | --- |
| 1 | Change `IHybridSearchable`'s default to `ScoreScale.Similarity` | both `HybridScoreScale_IsOpaqueRanking` tests |
| 2 | Restore `options.MinScore` in Azure's hybrid call | Azure's `DoesNotFilterByMinScore` |
| 3 | Restore `options.MinScore` in Weaviate's hybrid call | Weaviate's `DoesNotFilterByMinScore` |
| 4 | Pass `0.0` instead of `options.MinScore` in Azure's **dense** `SearchAsync` | an existing dense `MinScore` test — **if nothing catches this, the dense capability is unprotected and that is a finding worth its own test** |
| 5 | Pass `0.0` instead of `options.MinScore` in Weaviate's **dense** call | same |

Apply, run that store's suite, record **the test names that actually failed**, then `git checkout -- src/`.

- [ ] **Step 2: Act on any survivor**

A surviving mutation is a missing test, not an acceptable gap. Mutations 4 and 5 are the ones most likely to survive — they protect the capability this phase must not regress, and nothing in this plan adds a test for them. If they survive, write the test, verify it fails against the mutation, and keep it.

**Never record a mutation as caught without naming the test that failed.**

- [ ] **Step 3: Verify the tree is clean**

Run: `git status --short`
Expected: empty apart from any test you deliberately added.

---

### Task 5: Documentation

**Files:**
- Modify: `docs/guide/vector-stores.md`
- Modify: `docs/planning/ROADMAP.md` — the Phase 6.2.33 block

- [ ] **Step 1: Document the scale in the guide**

The guide's feature matrix lists "Hybrid search (native): Yes (`IHybridSearchable`)" for Azure and Weaviate and says nothing about what those scores mean. Add that a native hybrid returns a fused, ordinal score, that `MinScore` does not apply to it, and that the pipeline keeps a request with a `MinScore` on the client-side path for that reason.

**Do not claim the pipeline filters anything.** That sentence was false about metadata filtering and was corrected in 6.2.31; the accurate statement here is that the pipeline *declines the native path*, which is a different thing.

- [ ] **Step 2: Record the outcome in the ROADMAP block**

Add what the phase found, in the style of its neighbours — including that the severity was corrected mid-design when `CanDispatchNatively` turned out to guard the pipeline already. **Do not change the `[status: ...]` marker or add a `**Completed:**` line**; a separate step does that.

- [ ] **Step 3: Run both suites once more, then commit**

```bash
git add docs/
git commit -m "docs(vector-stores): say what a native hybrid score is, and that MinScore does not apply"
```

---

### Task 6: Pre-push review and PR

- [ ] **Step 1: Run every affected test project**

Both store suites, plus `tests/Rag.NET.Tests` (it holds `EnsembleBehaviorTests`, which exercises the native-dispatch decision this phase's reasoning depends on).

- [ ] **Step 2: Run `pre-push-review`**

- [ ] **Step 3: Open the PR**

Title: `feat(abstractions)!: let a native hybrid declare its fused score scale (#530)`. The `!` is for the added interface member; it is defaulted, so no implementer breaks, and the body should say so plainly rather than implying a bigger break.

The body must carry the corrected severity: the pipeline already refuses the native path when `MinScore` is set, so this is a direct-caller and API-clarity fix. **Do not describe it as a live pipeline defect.**

- [ ] **Step 4: Stop.** The merge is the operator's.
