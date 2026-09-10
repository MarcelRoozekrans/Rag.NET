# The Ranker Belongs Where the Text Is — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Azure's semantic ranker moves off `SearchAsync` — which cannot carry a text query by interface contract — onto `HybridSearchAsync`, which already does; and a request for ranking that cannot reach that path throws instead of returning unranked results.

**Architecture:** Three changes that fit together. The Azure store stops reading `_semanticRankingEnabled` in `SearchAsync` and starts reading it in `HybridSearchAsync` (the semantic configuration, `QueryType.Semantic` and `expectRerankerScore` all move as a unit; the guard inside `ExecuteSearchAsync` is untouched and simply starts firing on the other path). `IHybridSearchable` gains one defaulted member, `NativeOnlyCapability`, that names what a store's native hybrid does that client-side fusion cannot reproduce — `null` for every store that exists today, `"semantic ranking"` for Azure when the ranker is on. `EnsembleBehavior` reads that member and refuses to fuse client-side when doing so would silently drop the capability, naming the condition that blocked native dispatch.

**Tech Stack:** .NET 10, C#, xunit v3, NSubstitute, Testcontainers (`ghcr.io/ellerbach/azure-ai-search-simulator`), Azure.Search.Documents.

**Spec:** `docs/plans/2026-09-10-semantic-ranker-hybrid-design.md` — read it first, **and read §0 of this plan second, because two of the design's claims are wrong** and this plan reverses them rather than implementing them.

## Global Constraints

- **Issue:** #539. **Phase:** 6.2.36. **Related, filed and NOT fixed here:** #544.
- **Conventional commits, header at most 100 characters.** CI lints every commit a PR adds, not just the tip.
- **Not a revert of 6.2.34.** The `k >= 50` registration guard, the semantic index configuration, `HybridScoreScale`, and the missing-reranker-score guard are all correct and stay. Only the *path* was wrong.
- **Do not fix #544 here.** `ResilientVectorStore` not implementing `IHybridSearchable` is a pre-existing defect with its own issue. This plan adds a *diagnostic* for it (Task 4) and no fix.
- **`MinScore` is never applied to a reranker score**, on any path. It is Azure's ~0–4 ordinal relevance score. `ExecuteSearchAsync` already enforces this by skipping the threshold whenever `expectRerankerScore` is set; do not add a second place that decides it.
- **No scale change is needed anywhere.** `IHybridSearchable.HybridScoreScale` is already `ScoreScale.OpaqueRanking` (6.2.33) and a reranker score is ordinal for the same reason a fused score is. Any diff that touches `HybridScoreScale` is a mistake.
- **The simulator still lies.** `ghcr.io/ellerbach/azure-ai-search-simulator` accepts `queryType=semantic`, returns HTTP 200, and returns no `rerankerScore` at all. So the *throw* is testable locally and **everything past the throw is not** — unchanged from 6.2.34. Do not write a test that asserts ranked ordering; it would pass without any ranking happening.
- **Baseline is not recorded in this plan and must be measured, not assumed.** Task 0 does it. The Azure suite needs Docker running for Testcontainers.
- **Test commands:**
  - `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release` (needs Docker)
  - `dotnet test tests/Rag.NET.Tests -c Release`
  - `dotnet test tests/Rag.NET.Resilience.Tests -c Release`
  - `dotnet test tests/Rag.NET.Memory.Tests -c Release`

---

## §0. Two things the design gets wrong, found while writing this plan

**Read this before Task 1. Both were found by tracing the design's own §3 dispatch rule against `EnsembleBehavior`'s actual code, and both change what gets built.**

### 0.1 §4's central prediction is backwards: under resilience the throw never fires

The design's §4 says:

> §3's throw will surface it immediately and painfully: resilience plus semantic ranking will throw on every query, and the user cannot resolve it except by disabling resilience.

It will not. Trace it:

1. `EnsembleBehavior` injects `IVectorStore`, which under resilience resolves to `ResilientVectorStore`.
2. `ResilientVectorStore` does not implement `IHybridSearchable` (that is #544).
3. Any throw conditioned on `VectorStore is IHybridSearchable { … }` therefore **does not match**, so it does not throw.
4. The query falls to client-side fusion, whose dense arm calls `SearchAsync` — which after Task 1 no longer ranks and no longer expects a reranker score.

So resilience plus semantic ranking produces **ordinary cosine results, unranked, with no error** — not a painful throw. That is the exact "succeeds while doing nothing" shape this phase exists to remove, and the design predicted its opposite. The correction is Task 4's warning: `EnsembleBehavior` cannot throw for this case (it has no way to ask the decorated instance whether ranking is on — `IVectorStoreDecorator` deliberately exposes only `InnerStoreType`, "so no caller can reach around whatever behaviour the decorator adds"), but it can and must say something.

### 0.2 A silent path the design never names: `UseHybridSearch = false`

`EnsembleBehavior.HandleAsync` returns to `next` before any store probe:

```csharp
if (!opts.UseHybridSearch)
    return await next(ctx, ct).ConfigureAwait(false);
```

A caller who sets `EnableSemanticRanking = true` at registration and never sets `UseHybridSearch = true` per query gets plain dense search, forever, silently. Under 6.2.34 that caller got a throw (the ranker was on the dense path). After this phase they get silence — a *regression in observability* introduced by the fix, unless it is handled.

It is the same rule as §3's, so it gets the same treatment: `UseHybridSearch is false` becomes one of the named blocking conditions, and the ranking check moves **above** the early return. This costs one reordering and closes the hole the phase would otherwise open.

### 0.3 The two questions the design deferred, now settled

**`IScoreScaleAware` on `AzureAISearchVectorStore`: remove it** (design §2, scope item 3). Once the ranker leaves `SearchAsync`, the property returns `ScoreScale.Similarity` unconditionally, which `ScoreScale`'s own remarks define as the meaning of *not implementing the interface*. The docs currently defend implementing it unconditionally:

> a class implements an interface or it does not, at compile time, so a conditional implementation is not expressible. Fixing the value at construction is also what keeps the off case behaviour-preserving

That argument justified *unconditional over conditional*. It says nothing about *implementation over absence*, and its premise — that the value depends on a constructor option — is exactly what this phase deletes. What remains is a member indistinguishable from its own absence, which is the milestone's own defect shape in miniature. Removal is provably behaviour-preserving: `PersistentConversationMemory` probes for `OpaqueRanking` specifically, and `ResilientVectorStore.ScoreScale` already returns `Similarity` for an inner store that does not implement the interface. Task 2 tests both.

**Caveat to weigh at the assumptions gate:** this is an API-visible removal and `0.1.0` has published, so it carries the repo's `breaking-change` meaning. It is semantically neutral — a correct consumer pattern-matches and is unaffected; only a hard cast breaks — and v1.0 is the next milestone, which makes this the cheapest moment it will ever have.

**The capability probe is `IHybridSearchable.NativeOnlyCapability`, a defaulted `string?`** (design §3, scope item 4). One member carries both the predicate and the explanation: `null` means client-side fusion is an honest substitute; a non-null value names what would be lost and is quoted directly into the throw. `HybridScoreScale` is the precedent for adding a defaulted member to this interface pre-1.0, and defaulting to `null` keeps `WeaviateVectorStore` and every future implementer correct with no change.

---

### Task 0: Baseline

**Files:** none.

- [x] **Step 1: Start Docker** — the Azure suite provisions the simulator through Testcontainers and fails confusingly without it.

      *Was not running at session start (the previous session's instance had stopped). Started Docker Desktop; daemon 29.5.2 up after ~30s.*

- [x] **Step 2: Record the baseline for all four suites, before touching anything**

```bash
dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release
dotnet test tests/Rag.NET.Tests -c Release
dotnet test tests/Rag.NET.Resilience.Tests -c Release
dotnet test tests/Rag.NET.Memory.Tests -c Release
```

Write the four pass/skip/fail counts into this file, under this step, as measured numbers. Every later "expect N passed" is relative to them. **Do not background these runs.**

**Measured 2026-09-10, on `feat/6236-ranker-hybrid-plan` at `2592a977`, before any source change:**

| suite | passed | skipped | failed | total | duration |
| --- | --- | --- | --- | --- | --- |
| `Rag.NET.VectorStores.AzureAISearch.Tests` | 45 | 1 | 0 | 46 | 2m 05s |
| `Rag.NET.Tests` | 1487 | 0 | 0 | 1487 | 8s |
| `Rag.NET.Resilience.Tests` | 105 | 0 | 0 | 105 | 1s |
| `Rag.NET.Memory.Tests` | 3 | 0 | 0 | 3 | 355ms |

The one Azure skip is pre-existing: `AzureAISearchVectorStoreTests.Search_WithMetadataFilter_FiltersResults`.

**Correction to Task 2 Step 5, found here:** `Rag.NET.Memory.Tests` holds only 3 tests and none of them probe `IScoreScaleAware`. The tests that actually cover the removal are `PersistentConversationMemoryScoreScaleTests` in **`tests/Rag.NET.Tests/Memory/`**, already inside the 1487. Task 2 Step 5 must therefore compare `Rag.NET.Tests` against 1487, not treat `Rag.NET.Memory.Tests` as the check — running the small suite alone would have proved nothing while appearing to.

---

### Task 1: The ranker moves to the path that has text

**Files:**

- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs` — `SearchAsync` (~line 345), `HybridSearchAsync` (~line 388), `ExecuteSearchAsync` remarks (~line 618)
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchSemanticConfigurationTests.cs`

**Interfaces:**

- Consumes: nothing from earlier tasks.
- Produces: `AzureAISearchVectorStore.HybridSearchAsync` throws `InvalidOperationException` naming the index when ranking is enabled and no `RerankerScore` comes back. `SearchAsync` no longer throws for that reason under any option.

- [x] **Step 1: Rewrite the existing guard test to exercise the hybrid path**

`RequestingTheRankerFromAServiceThatDoesNotRank_Throws` currently calls `SearchAsync`. It must call `HybridSearchAsync`, because that is where the ranker now lives. Keep the `SearchIndexSettle.WaitUntilAsync` polling exactly as it is — it exists because the guard fires inside the result loop, so it only fires once the chunk is searchable, and a fixed delay that expired early would return an empty page, throw nothing, and fail the test reporting the guard as broken when the real cause was indexing latency.

In `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchSemanticConfigurationTests.cs`, replace the `sut.SearchAsync(...)` call inside the polling lambda with:

```csharp
                    await sut.HybridSearchAsync(
                        "semantic ranking candidate",
                        new float[] { 1.0f, 0.0f, 0.0f },
                        new SearchOptions { TopK = 1 },
                        TestContext.Current.CancellationToken);
                    return false;
```

and rename the test to `RequestingTheRankerFromAServiceThatDoesNotRank_ThrowsOnTheHybridPath`.

- [x] **Step 2: Add the test that pins the dense path's new silence**

This is the test that would have caught 6.2.34's error had it existed, so it is the one that matters most. Add to the same file:

```csharp
    /// <summary>
    /// With the ranker enabled, the dense path returns ordinary cosine similarities and does not
    /// throw. Semantic ranking needs query text and <see cref="IVectorStore.SearchAsync"/> takes an
    /// embedding and a <c>SearchOptions</c> of <c>TopK</c>/<c>MinScore</c>/<c>MetadataFilter</c> —
    /// no text, by interface contract — so the ranker cannot run here on any tier in any region
    /// (#539). Before 6.2.36 this call threw; the throw was correct about the service and wrong
    /// about the path.
    /// </summary>
    [Fact]
    public async Task WithTheRankerEnabled_TheDensePathStillReturnsOrdinaryScores()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            new AzureAISearchOptions { EnableSemanticRanking = true });

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var docId = $"ais-{Guid.CreateVersion7():N}";
        await sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "dense path candidate",
                        DocumentId = new DocumentId(docId),
                        ChunkIndex = 0,
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        IReadOnlyList<SearchResult> results = [];
        await SearchIndexSettle.WaitUntilAsync(
            "the stored chunk is searchable on the dense path",
            async () =>
            {
                results = await sut.SearchAsync(
                    new float[] { 1.0f, 0.0f, 0.0f },
                    new SearchOptions { TopK = 1 },
                    TestContext.Current.CancellationToken);
                return results.Count > 0;
            },
            TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.InRange(only.Score, 0.0, 1.0);
    }
```

**Why `InRange(0, 1)` and not an equality:** the assertion has to distinguish a cosine similarity from a reranker score without knowing the exact value the simulator computes. Azure's reranker score is roughly 0–4 and a cosine similarity here is bounded by 1, so the range is the discriminator. Do not assert an exact score — it pins the simulator's scoring formula, not the behaviour.

- [x] **Step 3: Run both tests and watch them fail for the right reasons**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~AzureAISearchSemanticConfigurationTests"`

Expected: `RequestingTheRankerFromAServiceThatDoesNotRank_ThrowsOnTheHybridPath` fails — the hybrid path passes `expectRerankerScore: false`, so nothing throws and `SearchIndexSettle` times out. `WithTheRankerEnabled_TheDensePathStillReturnsOrdinaryScores` fails with `InvalidOperationException` from the dense guard. **If either fails differently, stop** — the second failing with a timeout instead of the exception means the chunk never became searchable and you are testing indexing latency, not the ranker.

- [x] **Step 4: Move the ranking configuration off `SearchAsync`**

In `AzureAISearchVectorStore.SearchAsync`, delete this block entirely:

```csharp
        if (_semanticRankingEnabled)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = SemanticConfigurationName,
            };
        }
```

and change the call below it from `expectRerankerScore: _semanticRankingEnabled` to `expectRerankerScore: false`:

```csharp
        var results = await ExecuteSearchAsync(
                null, searchOptions, options.MinScore, expectRerankerScore: false, cancellationToken)
            .ConfigureAwait(false);
```

- [x] **Step 5: Move it onto `HybridSearchAsync`**

In `HybridSearchAsync`, the `searchOptions` initialiser sets `QueryType = SearchQueryType.Simple`. Leave that as the default and override it after the filter assignment, so the non-ranking path is untouched:

```csharp
        searchOptions.Filter = BuildMetadataFilter(options.MetadataFilter);

        if (_semanticRankingEnabled)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = SemanticConfigurationName,
            };
        }

        // MinScore is deliberately not forwarded: Azure fuses BM25 and vector rankings
        // service-side and the resulting score is ordinal (IHybridSearchable.HybridScoreScale),
        // so a similarity-shaped threshold would filter it arbitrarily. With semantic ranking on,
        // the score is Azure's reranker score instead — also ordinal, also not thresholdable, and
        // already covered by the same HybridScoreScale declaration (6.2.33), which is why moving
        // the ranker here needs no scale change at all.
        var results = await ExecuteSearchAsync(
                textQuery, searchOptions, minScore: 0.0, expectRerankerScore: _semanticRankingEnabled, cancellationToken)
            .ConfigureAwait(false);
```

- [x] **Step 6: Correct `ExecuteSearchAsync`'s remarks, which now say the opposite of the truth**

The first `<para>` currently reads "The field configures the dense path; the hybrid path always passes `false` regardless of it". Replace that whole `<para>` with:

```csharp
    /// <para>
    /// <b><paramref name="expectRerankerScore"/> is passed in, never read from
    /// <c>_semanticRankingEnabled</c> here.</b> The field configures the <i>hybrid</i> path; the
    /// dense path always passes <see langword="false"/> regardless of it, because semantic ranking
    /// needs query text and <see cref="IVectorStore.SearchAsync"/> has none to give — it takes an
    /// embedding and a <c>SearchOptions</c> of <c>TopK</c>/<c>MinScore</c>/<c>MetadataFilter</c>
    /// (#539). Reading the field in this shared method would put the ranker back on the path that
    /// cannot carry it, which is exactly the defect 6.2.36 fixed.
    /// </para>
```

- [x] **Step 7: Update the `HybridSearchAsync` summary remarks**

Its `<remarks>` currently says the fused score "is a rank produced by combining the BM25 and vector rankings server-side". Add one sentence after that first sentence:

```csharp
    /// With <c>EnableSemanticRanking</c> on, that fused result is then reranked by Azure's semantic
    /// ranker and the score returned is the <c>RerankerScore</c> — a different number on the same
    /// ordinal scale, thresholdable by neither.
```

- [x] **Step 8: Run the two tests again**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~AzureAISearchSemanticConfigurationTests"`
Expected: PASS, all tests in the class.

- [x] **Step 9: Run the whole Azure suite** — the index-schema tests (`EnablingTheRanker_AddsASemanticConfigurationToTheIndex`, `WithoutTheRanker_TheIndexHasNoSemanticConfiguration`) must be untouched by this change, because `BuildIndex` was not modified. If either moves, something was changed that should not have been.

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release`
Expected: baseline count from Task 0, plus one (the new dense-path test).

- [x] **Step 10: Commit**

```bash
git add src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchSemanticConfigurationTests.cs
git commit -m "fix(azure-search): move the semantic ranker to the path that carries text (#539)"
```

---

### Task 2: `IScoreScaleAware` leaves the store, and the tests prove nothing moved

**Files:**

- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs:16` (class declaration) and the `ScoreScale` property (~line 108-116)
- ~~Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`~~
- Test: **`tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchCapabilityDeclarationsTests.cs` (new)**
- Modify: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs` — delete two obsolete tests, see below

**Two deviations from this task as written, both found during execution:**

**(a) The tests went in a new container-free file.** `AzureAISearchVectorStoreTests` carries `IAsyncLifetime`, and `AzureAISearchCollection` records that "the simulator container is managed per-class via `IAsyncLifetime`" — which in xunit v3 means a container start per test method. Every assertion in this task and Task 3 is answered by the type or a constructor argument, so a container buys nothing and costs a start each. Measured: the new file's two tests run in **126ms**. Two pre-existing tests in the old class (`WithTheRankerOn/Off_TheStoreDeclares...`) already constructed the store against a dummy endpoint and paid that cost for nothing.

**(b) Two existing tests had to be deleted, which this task did not anticipate.** `WithTheRankerOn_TheStoreDeclaresAnOrdinalScale` and `WithTheRankerOff_TheStoreDeclaresASimilarityScale` (6.2.34's) assert the removed property; they broke the build, not a test run. Deleted rather than adapted: the first asserts *the dense path declares `OpaqueRanking` when the ranker is on*, which is precisely the claim #539 is about. `TheStoreDoesNotDeclareAScoreScale_BecauseTheDefaultIsAlreadyRight` replaces both. `HybridScoreScale_IsOpaqueRanking` is untouched and still passes — the declaration that still carries information.

**Interfaces:**

- Consumes: Task 1's `SearchAsync`, which now returns a genuine cosine similarity under every option.
- Produces: `AzureAISearchVectorStore` no longer implements `IScoreScaleAware`. Consumers reading its dense scale get `ScoreScale.Similarity` from the documented meaning of the interface's absence.

**Read §0.3 before starting.** If the assumptions gate overruled it and the interface stays, skip this task entirely and instead change the property body to the unconditional `public ScoreScale ScoreScale => ScoreScale.Similarity;` with a remark explaining why the value no longer depends on the option — then go to Task 3.

- [x] **Step 1: Write the failing tests**

Add to `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`:

```csharp
    /// <summary>
    /// The dense path returns a genuine cosine similarity under every option once the ranker moved
    /// to hybrid (#539), so a declaration of <c>ScoreScale.Similarity</c> would be exactly equal to
    /// the documented meaning of not implementing the interface. It was removed rather than left as
    /// a member indistinguishable from its own absence.
    /// </summary>
    [Fact]
    public void TheStoreDoesNotDeclareAScoreScale_BecauseTheDefaultIsAlreadyRight()
    {
        Assert.False(typeof(IScoreScaleAware).IsAssignableFrom(typeof(AzureAISearchVectorStore)));
    }

    /// <summary>
    /// The hybrid declaration is the one that still carries information, and it is unchanged: a
    /// fused score and a reranker score are both ordinal (6.2.33).
    /// </summary>
    [Fact]
    public void TheStoreStillDeclaresItsHybridScoreAsOrdinal()
    {
        Assert.True(typeof(IHybridSearchable).IsAssignableFrom(typeof(AzureAISearchVectorStore)));
    }
```

- [x] **Step 2: Run them and watch the first fail**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~TheStoreDoesNotDeclareAScoreScale"`
Expected: FAIL — `Assert.False() Failure`. The second test passes already; it is a control that this task did not remove the wrong interface.

- [x] **Step 3: Remove the interface**

Line 16 becomes:

```csharp
public sealed class AzureAISearchVectorStore : IVectorStore, IHybridSearchable, ICollectionManageable, IChunkLookup, IDisposable
```

Delete the `ScoreScale` property and its whole `<inheritdoc />`/`<remarks>` block (lines ~108–116).

- [x] **Step 4: Run the two tests**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~TheStore"`
Expected: PASS both.

- [x] **Step 5: Prove the removal changed no behaviour anywhere it is probed**

Two consumers probe this interface. Run these suites and expect the Task 0 baseline, unchanged. **`Rag.NET.Tests` is the one that matters** — `PersistentConversationMemoryScoreScaleTests` lives in `tests/Rag.NET.Tests/Memory/`, not in the 3-test `Rag.NET.Memory.Tests`; running only the small suite would have proved nothing while appearing to (see Task 0 Step 2):

```bash
dotnet test tests/Rag.NET.Tests -c Release          # expect 1487
dotnet test tests/Rag.NET.Resilience.Tests -c Release  # expect 105
dotnet test tests/Rag.NET.Memory.Tests -c Release      # expect 3
```

*Measured: 1487 / 105 / 3. All three exactly at baseline.*

`PersistentConversationMemory` pattern-matches `{ ScoreScale: ScoreScale.OpaqueRanking }`, so a store declaring `Similarity` and a store not implementing the interface take the same branch. `ResilientVectorStore.ScoreScale` is `Inner is IScoreScaleAware aware ? aware.ScoreScale : ScoreScale.Similarity`, which returns `Similarity` either way. **If any test in either suite moves, the removal was not behaviour-preserving and the assumption in §0.3 was wrong — stop and report it rather than adjusting the test.**

- [x] **Step 6: Commit**

```bash
git add src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "refactor(azure-search)!: drop IScoreScaleAware, now equal to its own absence (#539)"
```

The `!` marks the API-visible removal. Note it for the PR body so the `breaking-change` label gets applied.

---

### Task 3: The capability probe

**Files:**

- Modify: `src/Rag.NET.Abstractions/Abstractions/IHybridSearchable.cs`
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs`
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchCapabilityDeclarationsTests.cs` — the container-free file created in Task 2, for the same reason

**Interfaces:**

- Consumes: Task 1's placement of the ranker on `HybridSearchAsync`.
- Produces: `string? IHybridSearchable.NativeOnlyCapability { get; }`, defaulting to `null`. `AzureAISearchVectorStore` overrides it to `"semantic ranking"` when `_semanticRankingEnabled`, `null` otherwise. Task 4 consumes it.

- [x] **Step 1: Write the failing test**

Add to `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`. Construct the store directly — no simulator needed, the property is decided at construction:

```csharp
    /// <summary>
    /// The probe <see cref="EnsembleBehavior"/> reads to decide whether client-side fusion is an
    /// honest substitute for this store's native hybrid query. With the ranker on it is not: the
    /// fused-client-side result is correct but unranked, and returning it silently is the defect
    /// #539 is about, one layer up.
    /// </summary>
    [Theory]
    [InlineData(true, "semantic ranking")]
    [InlineData(false, null)]
    public void TheStoreDeclaresWhatClientSideFusionWouldLose(bool rankingEnabled, string? expected)
    {
        using var sut = new AzureAISearchVectorStore(
            new Uri("https://example.search.windows.net"),
            "ragnet-probe",
            new AzureKeyCredential("admin-key-12345"),
            vectorDimensions: 3,
            options: new AzureAISearchOptions
            {
                EnableSemanticRanking = rankingEnabled,
                KNearestNeighborsCount = rankingEnabled ? 50 : null,
            });

        Assert.Equal(expected, ((IHybridSearchable)sut).NativeOnlyCapability);
    }
```

**No network call happens here** — `EnsureInitialisedAsync` runs on the first operation that needs the index, and reading a property is not one. If this test tries to reach the endpoint, something initialises eagerly and that is a separate finding worth reporting.

- [x] **Step 2: Run it and watch it fail to compile**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~TheStoreDeclaresWhatClientSideFusionWouldLose"`
Expected: build error `CS1061` — `IHybridSearchable` does not contain a definition for `NativeOnlyCapability`.

- [x] **Step 3: Add the defaulted member to `IHybridSearchable`**

Insert above `HybridScoreScale` in `src/Rag.NET.Abstractions/Abstractions/IHybridSearchable.cs`:

```csharp
    /// <summary>
    /// What this store's native hybrid query does that client-side fusion cannot reproduce, or
    /// <see langword="null"/> when client-side fusion is an honest substitute. Phrased as a short
    /// noun phrase, because it is quoted into the error a caller sees — <c>"semantic ranking"</c>,
    /// not <c>"this store supports semantic ranking"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The retrieval pipeline refuses rather than degrades when this is non-null.</b> Native
    /// dispatch is conditional — an <see cref="RetrievalOptions.EnsembleOptions"/>, a non-zero
    /// <see cref="RetrievalOptions.MinScore"/>, a sparse arm that would run, or
    /// <see cref="RetrievalOptions.UseHybridSearch"/> left unset each keep the client-side path.
    /// For every store that exists today that is a fair trade: client-side Reciprocal Rank Fusion
    /// computes the same kind of answer the backend would. For a store that declares something
    /// here, it is not — the caller asked for a capability and would receive correct results with
    /// that capability silently absent, which is the failure mode this library treats as an error
    /// rather than a downgrade (#539).
    /// </para>
    /// <para>
    /// <b>Defaulted to <see langword="null"/> rather than required</b>, for the same reason
    /// <see cref="HybridScoreScale"/> is defaulted: it is correct for every implementer that
    /// exists, and a new member on this interface must not break the ones that do.
    /// </para>
    /// <para>
    /// <b>A general capability, not a per-backend flag.</b> The pipeline must not know what Azure's
    /// semantic ranker is; it must know only that this store would lose something. Anything that
    /// makes <c>EnsembleBehavior</c> name a specific backend belongs behind this member instead.
    /// </para>
    /// </remarks>
    string? NativeOnlyCapability => null;
```

- [x] **Step 4: Override it on the Azure store**

Add next to the other `IHybridSearchable` members in `AzureAISearchVectorStore`:

```csharp
    /// <inheritdoc />
    /// <remarks>
    /// Fixed at construction by <see cref="AzureAISearchOptions.EnableSemanticRanking"/>. Client-
    /// side fusion would return correct, unranked results with no error — which is what #539's
    /// reporter would have received had 6.2.34's guard not existed.
    /// </remarks>
    public string? NativeOnlyCapability => _semanticRankingEnabled ? "semantic ranking" : null;
```

- [x] **Step 5: Run the test**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~TheStoreDeclaresWhatClientSideFusionWouldLose"`
Expected: PASS, both `InlineData` rows.

- [x] **Step 6: Confirm Weaviate took the default without being touched**

Run: `dotnet build src/Rag.NET.VectorStores.Weaviate -c Release`
Expected: builds clean, no new warnings. `WeaviateVectorStore` implements `IHybridSearchable` and must not need an edit — that is the whole point of defaulting the member.

- [x] **Step 7: Commit**

```bash
git add src/Rag.NET.Abstractions/Abstractions/IHybridSearchable.cs src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "feat(abstractions): IHybridSearchable declares what client-side fusion would lose (#539)"
```

---

### Task 4: `EnsembleBehavior` refuses to silently drop the capability

**Files:**

- Modify: `src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs`
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`

**Interfaces:**

- Consumes: `IHybridSearchable.NativeOnlyCapability` from Task 3.
- Produces: `EnsembleBehavior.HandleAsync` throws `InvalidOperationException` when the store declares a `NativeOnlyCapability` and the query cannot reach native dispatch. A new log method `RagPipelineLog.NativeHybridHiddenByDecorator`.

**Read §0.1 and §0.2 first.** They are why this task has both a throw and a warning, and why the ranking check sits above the `UseHybridSearch` early return.

- [x] **Step 1: Write the failing tests**

Add to `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`. First the fake — the existing tests use `Substitute.For<IVectorStore>()`, which cannot also be `IHybridSearchable` with a chosen `NativeOnlyCapability`, so this needs a real fake:

```csharp
    private sealed class FakeRankingHybridStore : IVectorStore, IHybridSearchable
    {
        public string? NativeOnlyCapability => "semantic ranking";

        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
            string textQuery, ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("native", 0, 3.5)]);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("dense", 0, 0.9)]);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(DocumentId documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
```

**Match `IVectorStore`'s real member list when you write this** — read the interface rather than trusting the four members above, and implement whatever else it declares with the same trivial bodies. A fake that fails to compile is a five-second fix; a fake that quietly omits a member the behaviour calls is a wrong test.

Then the tests:

```csharp
    /// <summary>
    /// A store that declares a native-only capability must not be fused client-side: the caller
    /// would receive correct results with the capability silently absent (#539).
    /// </summary>
    [Theory]
    [InlineData("MinScore")]
    [InlineData("EnsembleOptions")]
    [InlineData("UseHybridSearch")]
    public async Task HandleAsync_NativeOnlyCapabilityAndCannotDispatchNatively_Throws(string blocker)
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f, 0f })]));

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = new FakeRankingHybridStore(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var options = blocker switch
        {
            "MinScore" => new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 },
            "EnsembleOptions" => new RetrievalOptions { UseHybridSearch = true, EnsembleOptions = new EnsembleOptions() },
            _ => new RetrievalOptions { UseHybridSearch = false },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(MakeCtx(options), ct, (_, _) =>
                ValueTask.FromResult<IReadOnlyList<SearchResult>>([])).AsTask());

        Assert.Contains("semantic ranking", ex.Message, StringComparison.Ordinal);
        Assert.Contains(blocker, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control, and the one that keeps this from becoming a behaviour break: a store
    /// declaring nothing is fused client-side exactly as before.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoNativeOnlyCapability_StillFusesClientSideWithAMinScore()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("doc-1", 0, 0.9)]));

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f, 0f })]));

        var bm25 = Substitute.For<IBm25Index>();
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, MetadataValue>?>())
            .Returns([MakeBm25Hit("doc-1", 0)]);

        var sut = new EnsembleBehavior { Embedder = embedder, VectorStore = vectorStore, Bm25Index = bm25 };

        var output = await sut.HandleAsync(
            MakeCtx(new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 }), ct,
            (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.NotEmpty(output);
    }

    /// <summary>
    /// A store declaring nothing, with hybrid off, still reaches <c>next</c> — the reordering in
    /// Task 4 must not make the early return conditional on anything for ordinary stores.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoNativeOnlyCapabilityAndHybridOff_StillCallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EnsembleBehavior
        {
            Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var nextCalled = false;
        await sut.HandleAsync(MakeCtx(new RetrievalOptions { UseHybridSearch = false }), ct, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        });

        Assert.True(nextCalled);
    }
```

- [x] **Step 2: Run them and watch the throw tests fail**

Run: `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~EnsembleBehaviorTests"`
Expected: the three `HandleAsync_NativeOnlyCapabilityAndCannotDispatchNatively_Throws` rows fail (no exception thrown); the three controls pass.

- [x] **Step 3: Turn `CanDispatchNatively` into something that can say *why* not**

The throw has to name the blocking condition, and today the three conditions are collapsed into one bool. Replace `CanDispatchNatively` with a method returning the blocker's name, and keep the bool as its negation so line 52 reads the same:

```csharp
    /// <summary>
    /// The name of the first request setting that keeps this query on the client-side path, or
    /// <see langword="null"/> when the store's native hybrid query can serve it. Native fusion
    /// happens inside the backend, so it cannot apply <see cref="EnsembleOptions"/> weights, cannot
    /// run a sparse (SPLADE) arm, and does not apply <see cref="RetrievalOptions.MinScore"/> at all
    /// — a native implementer's fused score is on its own scale, not the dense arm's similarity
    /// scale.
    /// </summary>
    /// <remarks>
    /// Returns the option's name rather than a bool because a store declaring
    /// <see cref="IHybridSearchable.NativeOnlyCapability"/> turns this from a routing decision into
    /// an error, and an error that says "cannot dispatch natively" without saying which of four
    /// settings caused it is not actionable.
    /// </remarks>
    private string? NativeDispatchBlocker(RetrievalOptions opts) =>
        !opts.UseHybridSearch ? nameof(RetrievalOptions.UseHybridSearch)
        : opts.EnsembleOptions is not null ? nameof(RetrievalOptions.EnsembleOptions)
        : opts.MinScore is not 0.0 ? nameof(RetrievalOptions.MinScore)
        : SparseArmWouldRun(opts) ? nameof(RetrievalOptions.UseSparseSearch)
        : null;

    private bool CanDispatchNatively(RetrievalOptions opts) => NativeDispatchBlocker(opts) is null;
```

**Order matters and is not arbitrary:** `UseHybridSearch` is checked first because it is the condition a caller is most likely to have simply forgotten, and reporting a `MinScore` problem to someone who never turned hybrid on would send them to the wrong place.

- [x] **Step 4: Add the refusal, above the early return**

At the top of `HandleAsync`, replace:

```csharp
        var opts = ctx.Options;

        if (!opts.UseHybridSearch)
            return await next(ctx, ct).ConfigureAwait(false);
```

with:

```csharp
        var opts = ctx.Options;

        // Checked before the UseHybridSearch early return, not after: a caller who enabled a
        // native-only capability at registration and left UseHybridSearch unset would otherwise
        // get dense search forever, silently unranked -- the same defect one layer up (#539).
        if (VectorStore is IHybridSearchable { NativeOnlyCapability: { } nativeOnly }
            && NativeDispatchBlocker(opts) is { } blocker)
        {
            throw new InvalidOperationException(
                $"{VectorStore.GetType().Name} is configured for {nativeOnly}, which only its " +
                $"native hybrid query performs, but this request cannot use that path because " +
                $"{blocker} keeps it on client-side fusion. Client-side fusion would return " +
                $"correct results with {nativeOnly} silently absent, so the request is refused " +
                $"rather than downgraded. Either adjust {blocker}, or turn off {nativeOnly} on the " +
                "store. Note: registering Rag.NET.Resilience also prevents native hybrid dispatch " +
                "for a different reason and is not detected here — see issue #544.");
        }

        if (!opts.UseHybridSearch)
            return await next(ctx, ct).ConfigureAwait(false);
```

- [x] **Step 5: Run the tests**

Run: `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~EnsembleBehaviorTests"`
Expected: PASS, all of them — the three new throw rows, the three controls, and every pre-existing test in the class. **The pre-existing ones are the real check here**: none of them uses a store that declares a capability, so all must be untouched by the new branch.

- [x] **Step 6: Add the decorator warning (§0.1's correction)**

`EnsembleBehavior` cannot throw for the resilience case — it cannot ask a decorated instance whether ranking is on, and `IVectorStoreDecorator` exposes only `InnerStoreType` deliberately, "so no caller can reach around whatever behaviour the decorator adds". Reading that `Type` for a diagnostic is exactly what the interface is for. Add to `src/Rag.NET/Logging/RagPipelineLog.cs`, following the existing source-generated pattern in that file (copy the shape of `EnsembleNativeHybrid` — same `LoggerMessage` attribute style, next free `EventId`):

```csharp
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Native hybrid search is unavailable because the registered store is wrapped by {Decorator}, which does not forward IHybridSearchable; {InnerStore} supports it. Falling back to client-side fusion. See issue #544.")]
    public static partial void NativeHybridHiddenByDecorator(ILogger logger, string decorator, string innerStore);
```

Then in `HandleAsync`, immediately after the `UseHybridSearch` early return:

```csharp
        if (VectorStore is not IHybridSearchable
            && VectorStore is IVectorStoreDecorator decorator
            && typeof(IHybridSearchable).IsAssignableFrom(decorator.InnerStoreType))
        {
            RagPipelineLog.NativeHybridHiddenByDecorator(
                ctx.Logger, VectorStore.GetType().Name, decorator.InnerStoreType.Name);
        }
```

**A warning, not a throw, and the distinction is load-bearing.** Throwing here would break every existing user who registers resilience alongside Azure AI Search or Weaviate *without* the ranker — they get correct client-side results today and have no defect to fix. The warning is for the case that is genuinely wrong and cannot be detected precisely from here.

- [x] **Step 7: Test the warning fires, and does not fire for an ordinary store**

Add to `EnsembleBehaviorTests`, with a minimal fake decorator (do not reference `Rag.NET.Resilience` from this test project — the point is that the diagnostic is package-agnostic):

```csharp
    private sealed class FakeDecoratorOverHybridStore : IVectorStore, IVectorStoreDecorator
    {
        public Type InnerStoreType => typeof(FakeRankingHybridStore);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("dense", 0, 0.9)]);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(DocumentId documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
```

Assert on a capturing `ILogger` (`FakeLogger` from `Microsoft.Extensions.Diagnostics.Testing` if the repo already references it — **check first, and use whatever the repo's other log assertions use rather than adding a package**). Two cases: the decorator fake warns; `Substitute.For<IVectorStore>()` does not.

Run: `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~EnsembleBehaviorTests"`
Expected: PASS.

- [x] **Step 8: Run the full pipeline suite**

Run: `dotnet test tests/Rag.NET.Tests -c Release`
Expected: Task 0's baseline plus the new tests. Nothing pre-existing moves.

*Measured: **1496**, from a baseline of 1487 — nine new tests, nothing pre-existing moved. But it did not go that way first time, and the detour is the most useful thing this task produced:*

**Six pre-existing tests failed when the refusal first shipped, and the cause was a real gap in the contract.** `HandleAsync_MinScoreConfigured_KeepsClientFusion` and five siblings use `Substitute.For<IVectorStore, IHybridSearchable>()`, and **NSubstitute returns `string.Empty` for an unconfigured `string` property, not `null`**. The pattern `{ NativeOnlyCapability: { } declared }` matched `""`, so the refusal fired for every substituted hybrid store — and rendered:

> `ObjectProxy is configured for , which only its native hybrid query performs …  Either adjust MinScore, or turn off  on the store.`

An error naming nothing the caller can act on. The fix was **not** to configure the six substitutes. `NativeOnlyCapability` exists to be quoted into that message, so a blank value is a declaration of nothing usable and the pipeline now treats blank as absent (`string.IsNullOrWhiteSpace`), documented on the interface and pinned by `HandleAsync_BlankNativeOnlyCapability_IsTreatedAsNoDeclaration`. The six tests then passed untouched, which is the correct outcome: they assert behaviour that must not change, and it was this task's change that broke them.

**Also: `MA0051` rejected `HandleAsync` at 63 lines against a 60-line cap.** Both guards were extracted into `RefuseIfNativeOnlyCapabilityIsUnreachable` and `WarnIfADecoratorHidesNativeHybrid`, which reads better than the inline form regardless of the analyzer.

- [x] **Step 9: Commit**

```bash
git add src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs src/Rag.NET/Logging/RagPipelineLog.cs tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs
git commit -m "feat(retrieval): refuse client-side fusion that would drop a native-only capability (#539)"
```

---

### Task 5: Documentation, rewritten rather than amended

**Files:**

- Modify: `docs/guide/vector-stores.md` — "Semantic ranking" (~line 507), "Score scale" (~line 918), "Native hybrid search" (~line 552)
- Modify: `docs/guide/retrieval.md` — "How the hybrid path is selected" (table, prose and the mermaid diagram)
- Modify: `docs/guide/observability.md` — the resilience capability-surface paragraph, which claimed native hybrid is "not retried" when #544 shows it is **not reached**
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchOptions.cs` — `EnableSemanticRanking`'s own XML doc opened "Whether the dense search path…", the same error one layer down; Step 5's sweep found it

**The semantic-ranking section is wrong in the same way the code was** — it documents the dense path throughout, including a sentence explicitly saying `HybridSearchAsync` is untouched. Rewrite it; do not amend it.

- [x] **Step 1: Rewrite "Semantic ranking" in `docs/guide/vector-stores.md`**

Replace the paragraph beginning "Enabling it changes the dense `SearchAsync` path only" and the bullet list under it. The new content must say:

- Enabling it changes the **native hybrid** path only. `SearchAsync` is untouched and keeps its cosine similarity, **because semantic ranking needs query text and `SearchAsync` has none to give** — quote Microsoft: *"A query with `search=*` or an empty search string … won't work because there's nothing to measure semantic relevance against"*.
- The index configuration bullet is unchanged (it was always right — `BuildIndex` is not modified by this phase).
- `SearchResult.Score` becomes the `RerankerScore` **on `HybridSearchAsync`**, and the scale declaration that covers it is `IHybridSearchable.HybridScoreScale`, already `OpaqueRanking` from 6.2.33 — **not** `IScoreScaleAware`, which the store no longer implements.
- `MinScore` is not applied, unchanged, and now for one reason on one path instead of two.
- The `k >= 50` registration guard is unchanged and still applies — it governs the vector arm's recall, which the hybrid path still uses.
- The throw is unchanged in kind and moved in place; add the **new** `EnsembleBehavior` refusal, with the four blocking conditions named.
- A note that registering `Rag.NET.Resilience` prevents native hybrid dispatch entirely (#544), that the pipeline logs a warning when it detects this, and that ranking is therefore silently unavailable in that combination until #544 is fixed.

- [x] **Step 2: Correct the "Score scale" section**

Two edits. The table row for `ScoreScale.Similarity` must drop "and `AzureAISearchVectorStore` explicitly, when semantic ranking is off"; the row for `OpaqueRanking` must drop "`AzureAISearchVectorStore`, when semantic ranking is on" and leave `FederatedVectorStore`. Then **delete the whole "Azure AI Search implements `IScoreScaleAware` unconditionally" paragraph** and replace it with a short one saying the store no longer implements the interface, that the dense path is a genuine cosine similarity under every option since #539, and that `Similarity` — the documented meaning of the interface's absence — is therefore already the right answer.

- [x] **Step 3: Correct "Native hybrid search"**

Its last sentence says plain `SearchAsync` "is why the store is treated as similarity-scaled (see Score scale)". Keep the claim, fix the link target — it now follows from the interface's absence, not from a declaration.

- [x] **Step 4: Update `docs/guide/retrieval.md`'s dispatch rule**

The four conditions that keep the client-side path are unchanged, but their consequence is no longer uniform: for a store declaring a native-only capability, each is now an error rather than a routing decision. Add that, naming `UseHybridSearch` as the fourth condition — it was always true and was never listed, because until now it had no consequence worth naming.

- [x] **Step 5: Check for other stale references**

```bash
grep -rn "EnableSemanticRanking\|semantic ranking\|IScoreScaleAware" docs/ README.md
```

Fix anything that still describes the dense path or the removed interface. **Read each hit** — several are about `FederatedVectorStore` or the `k` guard and are correct as they stand.

- [x] **Step 6: Commit**

```bash
git add docs/
git commit -m "docs(vector-stores): the ranker documentation followed the code onto the wrong path (#539)"
```

---

### Task 6: Mutation sweep

**Files:** nothing permanently — apply, test, revert.

- [x] **Step 1: Run each mutation, record the line mutated AND the named catcher**

**Record the site, not just the description.** 6.2.31's sweep was made unreproducible by naming a mutation without naming where it was applied; 6.2.34 and 6.2.35 fixed that habit — keep it.

| # | mutation | site | expected catcher |
| --- | --- | --- | --- |
| 1 | `expectRerankerScore: _semanticRankingEnabled` → `false` | `HybridSearchAsync`'s `ExecuteSearchAsync` call | `RequestingTheRankerFromAServiceThatDoesNotRank_ThrowsOnTheHybridPath` |
| 2 | restore the `if (_semanticRankingEnabled)` block in `SearchAsync` | `SearchAsync` | `WithTheRankerEnabled_TheDensePathStillReturnsOrdinaryScores` — **this is the mutation that reintroduces 6.2.34's actual defect**, and the row that matters most |
| 3 | `NativeOnlyCapability` → `"semantic ranking"` unconditionally | `AzureAISearchVectorStore` | `TheStoreDeclaresWhatClientSideFusionWouldLose(false, null)` |
| 4 | drop `!opts.UseHybridSearch` from `NativeDispatchBlocker` | `EnsembleBehavior` | `HandleAsync_NativeOnlyCapabilityAndCannotDispatchNatively_Throws("UseHybridSearch")` — §0.2's hole, and the row proving the reordering is load-bearing |
| 5 | move the refusal back **below** the `UseHybridSearch` early return | `EnsembleBehavior.HandleAsync` | same row as #4. If #4 and #5 have the same catcher and nothing else, say so — two mutations sharing one catcher is a finding about test coverage shape, not a pass |
| 6 | `throw` → return client-side fusion (delete the refusal block) | `EnsembleBehavior.HandleAsync` | all three rows of the `Throws` theory |
| 7 | invert the decorator warning's probe to `VectorStore is IHybridSearchable` | `EnsembleBehavior.HandleAsync` | Task 4 Step 7's warning tests |
| 8 | re-add `IScoreScaleAware` to the class declaration with `=> ScoreScale.Similarity` | `AzureAISearchVectorStore:16` | `TheStoreDoesNotDeclareAScoreScale_BecauseTheDefaultIsAlreadyRight`. **Expect this to be caught only by a test asserting the interface's absence, which is a weak assertion by construction** — it pins a structural fact with no behavioural consequence. If it survives, that is the honest result: the removal is behaviour-neutral and no behavioural test can catch its reversal. Record it as a survivor with that reasoning rather than inventing a test that pretends otherwise. That is 6.2.34 row 7's lesson — a survivor that is unreachable is a finding, not a gap. |


**Results, measured 2026-09-10. Nine rows run (row 2 split into two forms), eight caught, one survivor that produced a new test.**

| # | mutation | site | outcome |
| --- | --- | --- | --- |
| 1 | `expectRerankerScore: _semanticRankingEnabled` → `false` | `HybridSearchAsync`'s `ExecuteSearchAsync` call | **caught** — `RequestingTheRankerFromAServiceThatDoesNotRank_ThrowsOnTheHybridPath` |
| 2a | restore **both** the `if (_semanticRankingEnabled)` block *and* `expectRerankerScore: _semanticRankingEnabled` in `SearchAsync` — 6.2.34's defect, exactly | `SearchAsync` | **caught** — `WithTheRankerEnabled_TheDensePathStillReturnsOrdinaryScores` |
| 2b | restore **only** the `if (_semanticRankingEnabled)` block, leaving `expectRerankerScore: false` | `SearchAsync` | **SURVIVED.** See below |
| 3 | `NativeOnlyCapability` → `"semantic ranking"` unconditionally | `AzureAISearchVectorStore` | **caught** — `TheStoreDeclaresWhatClientSideFusionWouldLose(false, null)` |
| 4 | drop `!opts.UseHybridSearch` from `NativeDispatchBlocker` | `EnsembleBehavior` | **caught** — `..._Throws(blocker: "UseHybridSearch")`, and *only* that row |
| 5 | move `RefuseIfNativeOnlyCapabilityIsUnreachable` below the `UseHybridSearch` early return | `EnsembleBehavior.HandleAsync` | **caught** — the same single row as #4 |
| 6 | delete the `RefuseIfNativeOnlyCapabilityIsUnreachable` call | `EnsembleBehavior.HandleAsync` | **caught** — all three rows of the theory |
| 7 | invert the decorator probe to `VectorStore is IHybridSearchable` | `WarnIfADecoratorHidesNativeHybrid` | **caught** — `HandleAsync_DecoratorHidesHybridCapability_WarnsNamingTheInnerStore` |
| 8 | re-add `IScoreScaleAware` with `=> ScoreScale.Similarity` | `AzureAISearchVectorStore:16` | **caught** — `TheStoreDoesNotDeclareAScoreScale_BecauseTheDefaultIsAlreadyRight`, and by nothing else: `Rag.NET.Tests` stayed at **1496**, unmoved |

**Row 2b is the sweep's finding, and it was not predicted.** The plan split row 2 only while running it, and the halves behave differently. Setting `QueryType = Semantic` on the dense path *without* also expecting a reranker score passes **every test in the repository**: the score returned is `result.Score`, an ordinary similarity in `[0, 1]`, so `WithTheRankerEnabled_TheDensePathStillReturnsOrdinaryScores` is satisfied and the guard never fires because nothing asked it to.

It is a plausible edit — a future reader "restoring the semantic configuration to the dense path" while leaving the guard alone — and it sends Azure a semantic query with no search text, which Microsoft says has "nothing to measure semantic relevance against": a malformed request against a billable feature. **It is also this phase's own defect shape**, which makes an uncaught mutation of it the least acceptable one to leave open.

So it got a test rather than a note: `TheDenseQueryNeverAsksForSemanticRanking_EvenWithTheRankerEnabled` asserts **on the wire**, through a `DelegatingHandler` capturing the outgoing request body, that the dense query never mentions semantic ranking at all. Re-running 2b against it: **caught.** This is the opposite call from 6.2.34 row 7 and 6.2.35 row 4, where survivors were recorded rather than tested — the difference is that those were *unreachable* or pinned a coincidence, and this one is reachable, meaningful, and directly on the phase's subject.

**Rows 4 and 5 share a single catcher**, as the task anticipated. That is a coverage-shape finding rather than a pass: two structurally different mutations — deleting a condition, and moving the call site past the return that condition exists to precede — are both held by `..._Throws(blocker: "UseHybridSearch")` alone. If that one theory row is ever deleted or weakened, both mutations go uncaught together.

**Row 8 behaved exactly as predicted, and the prediction is the point.** It is caught only by a structural `IsAssignableFrom` assertion, and the entire 1496-test pipeline suite does not move. That is not a gap to close: it is positive evidence that removing `IScoreScaleAware` was behaviour-preserving, which was §0.3's whole argument. No behavioural test can distinguish the removal from its reversal, because there is no behavioural difference.

**Rows 1 and 2a were run against the final form of their catching tests** (both were rewritten in Task 1 *before* the sweep). The wire-level test added afterwards can only add catchers, never remove them, so those rows stand without a re-run.

- [x] **Step 2: Re-run every row whose catching test you changed** — rewriting a catching test invalidates its row (6.2.35's lesson).

- [x] **Step 3: Verify the tree is clean** — `git status --short`, only files you meant to change.

---

### Task 7: Roadmap, review and PR

- [ ] **Step 1: Comment on #539** with what the fix was and the two things §0 found — that the design's resilience prediction was backwards, and that `UseHybridSearch = false` was an unnamed silent path. @StefH filed against a real Azure resource and is the one person who can confirm the ranker actually ranks once this ships; say so and ask.

- [ ] **Step 2: Comment on #544** noting that 6.2.36 shipped a warning for the decorated case and that the throw deliberately does not fire there — so #544 is now the only thing standing between a resilience user and silent unranked results.

- [ ] **Step 3: `docs/planning/ROADMAP.md`**, the Phase 6.2.36 block — record what the phase found in its neighbours' style, including §0's two corrections, the `IScoreScaleAware` removal and its reasoning, and the sweep's row-8 result whichever way it goes. **Do not change the `[status: ...]` marker and do not add `**Completed:**`** — `complete-phase` does that after the merge.

- [ ] **Step 4: Update the design doc's §4** with a struck-through correction rather than a deletion — the prediction was wrong in a specific and instructive direction (it assumed a probe would fire on a decorator that hides the interface, which is the very defect the section is about), and this repository keeps those.

- [ ] **Step 5: Run every affected suite.** Enumerate them, do not recall them:

```bash
dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release
dotnet test tests/Rag.NET.Tests -c Release
dotnet test tests/Rag.NET.Resilience.Tests -c Release
dotnet test tests/Rag.NET.Memory.Tests -c Release
dotnet test tests/Rag.NET.VectorStores.Weaviate.Tests -c Release
```

Weaviate is in the list because Task 3 added a member to an interface it implements. **Do not background any of these.** Compare every count against Task 0's baseline.

- [ ] **Step 6: Run `pre-push-review`.** Record the verdict and the report path here. Fix warnings before the PR, not after.

- [ ] **Step 7: Open the PR.** Body must name #539 as fixed, #544 as related-and-not-fixed, and flag the `breaking-change` label for Task 2's interface removal. Record the number here.

---

## Self-review

**Spec coverage** — design §5's six in-scope items: (1) ranker moves to `HybridSearchAsync` → Task 1. (2) dense path stops reading the flag → Task 1 Step 4, tested by Step 2. (3) settle `IScoreScaleAware` → §0.3 decides, Task 2 implements. (4) `EnsembleBehavior` throw with a general capability probe → Tasks 3 and 4. (5) docs rewritten not amended → Task 5. (6) `k >= 50` guard stays → no task, and Task 1 Step 9 checks the index-schema tests did not move. Out-of-scope items are honoured: #544 is filed and only diagnosed, and nothing reverts 6.2.34.

**Beyond the spec** — two additions, both from §0 and both flagged for the assumptions gate: `UseHybridSearch` as a fourth blocking condition (§0.2), and the decorator warning (§0.1). Neither is discretionary polish; each closes a silent path this phase would otherwise open or leave open.

**Type consistency** — `NativeOnlyCapability` is `string?` in the interface (Task 3 Step 3), the override (Step 4), the test (Step 1), and the `{ NativeOnlyCapability: { } nativeOnly }` pattern in Task 4 Step 4. `NativeDispatchBlocker` returns `string?` and `CanDispatchNatively` stays `bool` so line 52's existing call site is unchanged. `FakeRankingHybridStore` is defined in Task 4 Step 1 and reused by `FakeDecoratorOverHybridStore` in Step 7.

**Known weak spot, stated rather than hidden** — Task 2's tests assert a structural fact (`IsAssignableFrom`) with no behavioural consequence, which mutation row 8 will likely expose as a survivor. That is inherent: the removal is behaviour-preserving by design, so no behavioural test can distinguish it from its reversal. The test is worth keeping as a statement of intent; it is not worth pretending it is strong.
