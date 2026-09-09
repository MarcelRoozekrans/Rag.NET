# Semantic Ranker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Azure AI Search's semantic ranker, opt-in per instance, declaring its score as ordinal and throwing when the service accepts the request and does not actually rank.

**Architecture:** One new option enables it. The index gains a `SemanticSearch` configuration only when enabled. The dense query then sets `QueryType.Semantic`, results carry `RerankerScore` returned unrescaled, and the store declares `ScoreScale.OpaqueRanking`. A guard throws when the ranker was requested and no reranker score comes back. The hybrid path is untouched.

**Tech Stack:** .NET 10, C#, Azure.Search.Documents 11.7.0, xunit v3, Testcontainers (`ghcr.io/ellerbach/azure-ai-search-simulator`).

**Spec:** `docs/plans/2026-09-09-semantic-ranker-design.md` — read it first. Its §1 records three corrections to the section this phase was originally scoped from; the corrected version is what this plan implements.

## Global Constraints

- **Issue:** #328. **Phase:** 6.2.34. Closes the half 6.2.5 deferred.
- **`KNearestNeighborsCount` is already settable** — 6.2.5's #374 added it to `AzureAISearchOptions`. **Do not re-add it.** What this phase adds is a guard on combining it with the ranker.
- **The dense path only.** `HybridSearchAsync` must be untouched. `ExecuteSearchAsync` is shared between both paths, so the reranker expectation must be passed in rather than read from a field.
- **Everything is off by default.** A caller who does not opt in must see byte-identical behaviour: no index change, no query change, and `ScoreScale.Similarity`, which is the branch every consumer already takes.
- **Do not rescale the reranker score.** Azure's 0–4 is returned as it comes; the declaration is what makes it honest.
- **`MinScore` is not applied when the ranker is on**, by the same rule as 6.2.33's hybrid path.
- **Conventional commits, header at most 100 characters.**
- **Test command:** `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release`. Docker must be running; the suite starts a simulator container. `--filter` works normally on this project.
- Baseline before this phase: **36 passed, 1 pre-existing skip.**

---

### Task 1: The option, and the guard on combining it with `k`

**Files:**
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchOptions.cs`
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchBuilderExtensions.cs` — `ValidateConfigured`
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchBuilderExtensionsTests.cs` — it already exists and already builds a provider through `AddRagNet(rag => rag.UseAzureAISearch(...))`. Add to it; do not create a parallel file.

**Interfaces:**
- Produces: `bool EnableSemanticRanking { get; set; }` on `AzureAISearchOptions`, consumed by Tasks 2–4.

- [ ] **Step 1: Write the failing tests**

These need no simulator — they exercise `UseAzureAISearch`'s eager validation. **`AzureAISearchBuilderExtensionsTests.cs` already exists and is the file to add them to**; it builds a provider through `AddRagNet(rag => rag.UseAzureAISearch(...))` with a dummy endpoint and key, which is exactly the surface these tests need.

Three cases:

```csharp
    /// <summary>
    /// Microsoft's own guidance, already quoted in AzureAISearchOptions' remarks: "Whenever you use
    /// semantic ranking with vectors, set k to 50. Semantic ranker uses up to 50 matches as input.
    /// Specifying less than 50 deprives the semantic ranking models of necessary inputs." The
    /// damage is invisible — worse ranking, no error — so the combination is refused at
    /// registration, where both settings are made deliberately by the same person.
    /// </summary>
    [Fact]
    public void EnablingTheRankerWithKBelowFiftyIsRejected()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o =>
                {
                    o.EnableSemanticRanking = true;
                    o.KNearestNeighborsCount = 10;
                })));

        Assert.Contains("50", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Null is the right default: omitting k is what makes Azure apply its own 50.</summary>
    [Fact]
    public void EnablingTheRankerWithNoExplicitKIsAccepted()
    {
        var provider = new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o => o.EnableSemanticRanking = true))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    /// <summary>Fifty exactly is the documented minimum, not a value to reject.</summary>
    [Fact]
    public void EnablingTheRankerWithKAtFiftyIsAccepted()
    {
        var provider = new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o =>
                {
                    o.EnableSemanticRanking = true;
                    o.KNearestNeighborsCount = 50;
                }))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(provider.GetRequiredService<IVectorStore>());
    }
```

**Check the `configure:` argument name against `UseAzureAISearch`'s signature before writing** — the parameter is the fifth, after `vectorDimensions`, so it must be passed by name as shown or the call binds to the wrong overload position.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release --filter "FullyQualifiedName~Ranker"`
Expected: compile error — `EnableSemanticRanking` does not exist.

- [ ] **Step 3: Add the option**

In `AzureAISearchOptions.cs`:

```csharp
    /// <summary>
    /// Whether the dense search path asks Azure's semantic ranker to rerank results. Off by
    /// default; opting in changes what <see cref="Rag.NET.Models.SearchResult.Score"/> means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The score becomes ordinal.</b> With the ranker on, the store returns Azure's
    /// <c>RerankerScore</c> — a 0–4 relevance score — unrescaled, and declares
    /// <see cref="Rag.NET.Abstractions.ScoreScale.OpaqueRanking"/>. It is not converted into a
    /// similarity, because an invented similarity is worse than an honest ordinal, and
    /// <c>MinScore</c> is therefore not applied on that path.
    /// </para>
    /// <para>
    /// <b>Per instance, not per request.</b> It reshapes the score of the ordinary search path,
    /// and <see cref="Rag.NET.Abstractions.IScoreScaleAware"/> requires that scale to be constant
    /// for the instance's lifetime — callers probe it once and may cache the answer.
    /// </para>
    /// <para>
    /// <b>Requires a service that actually ranks.</b> Semantic ranking needs Basic tier or higher
    /// in a supporting region. A service that cannot rank answers the request successfully and
    /// returns ordinary scores, so the store throws rather than publish a number it cannot
    /// describe.
    /// </para>
    /// </remarks>
    public bool EnableSemanticRanking { get; set; }
```

Update `KNearestNeighborsCount`'s remarks: the paragraph currently ending "Semantic ranking is not implemented here yet — #328 stays open for it — so this note is for anyone configuring the index themselves in the meantime" is now false. Replace with the guard: setting `k` below 50 alongside `EnableSemanticRanking` is refused at registration.

- [ ] **Step 4: Add the validation**

In `ValidateConfigured`, beside the existing `k < 1` check:

```csharp
        if (options.EnableSemanticRanking && options.KNearestNeighborsCount is { } semanticK && semanticK < 50)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                semanticK,
                "KNearestNeighborsCount must be at least 50 when EnableSemanticRanking is set, or " +
                "null to use Azure's own default of 50. Microsoft documents that the semantic " +
                "ranker uses up to 50 matches as input and that fewer deprives it of necessary " +
                "inputs — a quality loss with no error to notice.");
        }
```

- [ ] **Step 5: Run to verify GREEN, then the whole suite**

Run the filtered tests, then `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests -c Release`.
Expected: 36 + 3 new passing, 1 pre-existing skip. **No behaviour changed yet** — nothing reads the flag.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.VectorStores.AzureAISearch tests/Rag.NET.VectorStores.AzureAISearch.Tests
git commit -m "feat(azureaisearch): add the semantic ranking option and guard its k (#328)"
```

---

### Task 2: The index gains a semantic configuration when enabled

**Files:**
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs` — the constructors (lines 27–76), `BuildIndex` (line 117)
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/`

**Interfaces:**
- Consumes: `AzureAISearchOptions.EnableSemanticRanking` from Task 1.
- Produces: a private field for the flag, and `BuildIndex` aware of it. Tasks 3 and 4 consume the field.

**`BuildIndex` is `private static`** (line 117). It needs the flag, so either make it an instance method or add a parameter. **Prefer the parameter** — it keeps the method a pure function of its inputs, which is what makes it readable, and `CreateCollectionAsync` also calls it.

- [ ] **Step 1: Write the failing tests**

Two, both against the simulator, reading the index definition back through `SearchIndexClient.GetIndexAsync`:

```csharp
    /// <summary>
    /// The ranker needs a semantic configuration on the index, and the store owns its name rather
    /// than exposing it — one configuration, built from the fields the store already defines.
    /// </summary>
    [Fact]
    public async Task EnablingTheRanker_AddsASemanticConfigurationToTheIndex()

    /// <summary>
    /// And an index built without the ranker carries none: a configuration nothing uses is clutter,
    /// and adding it unconditionally would rewrite every existing index for no benefit.
    /// </summary>
    [Fact]
    public async Task WithoutTheRanker_TheIndexHasNoSemanticConfiguration()
```

Read the existing fixture in `AzureAISearchChunkLookupTests` for how the simulator is started and how a store is constructed against it, and match it. **The simulator accepts a semantic configuration and echoes it back — verified 2026-09-09** — so reading the index definition is a real assertion here even though the simulator will not *use* the configuration.

- [ ] **Step 2: Run to verify they fail**

Expected: the enabled case fails because no store constructor accepts the flag yet — a compile error.

- [ ] **Step 3: Thread the flag and build the configuration**

Add `private readonly bool _semanticRankingEnabled;` and set it from `options?.EnableSemanticRanking ?? false` in the private constructor. Pass it to `BuildIndex` at both call sites.

In `BuildIndex`, when the flag is set, add a `SemanticSearch` with one `SemanticConfiguration` whose `PrioritizedFields` name the `text` field as a content field. Name the configuration with a `private const string` — it is an implementation detail, not a knob.

- [ ] **Step 4: Run the whole suite**

Expected: all pass. **Existing tests construct the store without the flag, so their indexes must be unchanged** — if any existing test fails, the flag is leaking into the disabled path.

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(azureaisearch): put a semantic configuration on the index when the ranker is on (#328)"
```

---

### Task 3: The query asks for ranking, and the guard catches a service that does not

**This is the task the phase exists for.** The guard is what makes the feature shippable without an Azure account.

**Files:**
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs` — `SearchAsync` (line 309), `ExecuteSearchAsync` (line 551)
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/`

**Interfaces:**
- Consumes: `_semanticRankingEnabled` from Task 2.
- Produces: nothing later tasks depend on.

**`ExecuteSearchAsync` is shared with `HybridSearchAsync`.** Add a parameter — `bool expectRerankerScore` — passed `_semanticRankingEnabled` from `SearchAsync` and **`false` from `HybridSearchAsync`**. Do not read the field inside the shared method; the hybrid path must be unaffected whatever the instance is configured to do.

- [ ] **Step 1: Write the failing test**

```csharp
    /// <summary>
    /// <b>The simulator accepts semantic ranking and does not perform it</b>, which is exactly the
    /// failure this guard exists for. Measured 2026-09-09: it takes a semantic index configuration
    /// (HTTP 201) and a semantic query (HTTP 200 with results) and returns no rerankerScore at all.
    /// A real service does the same when the tier or region lacks the ranker, or when the
    /// configuration name does not match — every one of those a silent downgrade to ordinary
    /// scoring, with the caller believing results were reranked.
    /// </summary>
    /// <remarks>
    /// This test therefore asserts the guard against a service that genuinely does not rank, rather
    /// than against a mock. It is the strongest evidence available without a billable Azure
    /// resource, and it is why the feature can ship at all.
    /// </remarks>
    [Fact]
    public async Task RequestingTheRankerFromAServiceThatDoesNotRank_Throws()
```

Store a chunk, search, assert an `InvalidOperationException` whose message names the index.

- [ ] **Step 2: Run to verify it fails**

Expected: **no exception** — the search returns results scored the ordinary way. That failure *is* the defect the guard prevents, observed.

- [ ] **Step 3: Ask for ranking**

In `SearchAsync`, when `_semanticRankingEnabled`, set on the `SearchOptions`:

```csharp
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = SemanticConfigurationName,
            };
```

- [ ] **Step 4: Read the reranker score, and guard**

In `ExecuteSearchAsync`, when `expectRerankerScore`:

- take the score from `result.SemanticSearch?.RerankerScore`;
- when it is null, **throw** `InvalidOperationException` naming the index and saying semantic ranking was requested but the service returned no reranker score, so results would be ordinary scores presented as reranked;
- do **not** apply `minScore` on this path.

When `expectRerankerScore` is false, the existing behaviour is unchanged in every respect.

Document the guard on the method, in the file's style: what it catches, and that a service which cannot rank answers successfully rather than failing.

- [ ] **Step 5: Run the whole suite**

Expected: all pass. Every existing test runs with the ranker off and must be untouched.

- [ ] **Step 6: Commit**

```bash
git commit -m "feat(azureaisearch): request semantic ranking, and throw when the service does not rank (#328)"
```

---

### Task 4: Declare the scale

**Files:**
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs` — the class declaration (line 16)
- Test: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/`

**The store implements `IScoreScaleAware` unconditionally** and returns a value fixed at construction: `OpaqueRanking` when the ranker is on, `Similarity` when off. **A class cannot conditionally implement an interface** — that was an error in the original design, corrected in the spec's §1.

Declaring `Similarity` when off is **behaviour-preserving**: `PersistentConversationMemory` tests `is not IScoreScaleAware { ScoreScale: OpaqueRanking }` (`:66`), so it takes the same branch either way.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void WithTheRankerOn_TheStoreDeclaresAnOrdinalScale()

    /// <summary>
    /// And with it off the store declares Similarity — not silence. Declaring the default
    /// explicitly is behaviour-preserving, because every consumer branches on OpaqueRanking
    /// specifically, and it means the scale is discoverable in both configurations.
    /// </summary>
    [Fact]
    public void WithTheRankerOff_TheStoreDeclaresASimilarityScale()
```

Unlike `HybridScoreScale`, this is a normal interface implementation, so it is reachable on a concrete reference — no cast needed.

- [ ] **Step 2: Run to verify they fail** — compile error, the store does not implement the interface.

- [ ] **Step 3: Implement it**

Add `IScoreScaleAware` to the class declaration and:

```csharp
    /// <inheritdoc />
    /// <remarks>
    /// Fixed at construction, as the interface requires. With semantic ranking on, this store
    /// returns Azure's <c>RerankerScore</c>, which is a relevance rank rather than a similarity;
    /// with it off, the dense path's cosine similarity is exactly what the default assumes.
    /// </remarks>
    public ScoreScale ScoreScale =>
        _semanticRankingEnabled ? ScoreScale.OpaqueRanking : ScoreScale.Similarity;
```

- [ ] **Step 4: Run the whole suite, then commit**

```bash
git commit -m "feat(azureaisearch): declare the score scale the ranker changes (#328)"
```

---

### Task 5: Mutation sweep

**Files:** nothing permanently — apply, test, `git checkout -- src/`.

- [ ] **Step 1: Run each mutation, record the named catcher**

| # | mutation | expected catcher |
| --- | --- | --- |
| 1 | delete the guard's throw; return the ordinary score instead | `RequestingTheRankerFromAServiceThatDoesNotRank_Throws` |
| 2 | drop the `k < 50` validation | `EnablingTheRankerWithKBelowFiftyIsRejected` |
| 3 | make `ScoreScale` always return `OpaqueRanking` | `WithTheRankerOff_TheStoreDeclaresASimilarityScale` |
| 4 | make `ScoreScale` always return `Similarity` | `WithTheRankerOn_TheStoreDeclaresAnOrdinalScale` |
| 5 | add the semantic configuration unconditionally | `WithoutTheRanker_TheIndexHasNoSemanticConfiguration` |
| 6 | pass `expectRerankerScore: true` from `HybridSearchAsync` | **nothing may catch this** — see below |
| 7 | apply `minScore` on the ranked path | may be uncaught; see below |

**Rows 6 and 7 are the point of this sweep.** Row 6 mutates the boundary between the two paths — if nothing catches it, the hybrid path's independence from the ranker is unprotected. Row 7 checks whether "MinScore is not applied when ranking" is actually pinned.

**A survivor is a missing test, not an acceptable gap.** Write it, prove it fails against the mutation, revert the mutation, keep the test.

**Never record a mutation as caught without naming the test that failed.**

- [ ] **Step 2: Verify the tree is clean** — `git status --short`, only tests you added.

---

### Task 6: Documentation

- [ ] **Step 1: `docs/guide/vector-stores.md`** — the Azure section gains the ranker: how to enable it, that the score becomes ordinal and `MinScore` stops applying, that `k` must be null or at least 50, and that a service which cannot rank causes a throw rather than a silent downgrade. Cross-link the score-scale section.

- [ ] **Step 2: `docs/planning/ROADMAP.md`**, the Phase 6.2.34 block — record what the phase found, in its neighbours' style. **Do not change the `[status: ...]` marker or add `**Completed:**`.**

Include: that half of #328 was already shipped and the design said otherwise until the code was read; that the simulator's defect became the guard's test fixture; and the sweep's results.

- [ ] **Step 3: `<VerifiedByReason>`** — if the project's ledger requires it for this package, state the one claim that needs a resource: that Azure's ranker populates `RerankerScore` and reorders. Everything else is verified locally. **Check whether this package already carries a `VerifiedBy` value and do not downgrade it.**

- [ ] **Step 4: Commit**

---

### Task 7: Pre-push review and PR

- [ ] **Step 1: Run the Azure suite and `tests/Rag.NET.Tests`** (the latter holds the consumers that probe `IScoreScaleAware`). **Do not background either run.**
- [ ] **Step 2: Run `pre-push-review`.**
- [ ] **Step 3: Open the PR.** Title: `feat(azureaisearch): add Azure AI Search semantic ranking (#328)`. Not breaking — everything is off by default and the disabled path is byte-identical. The body must be honest that one claim is unverified without a billable resource, and precise about which.
- [ ] **Step 4: Stop.** The merge is the operator's.
