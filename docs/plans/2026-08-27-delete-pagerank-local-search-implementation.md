# Delete the PageRank-blend local search — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Status: executed and merged 2026-08-27 in [#408](https://github.com/MarcelRoozekrans/Rag.NET/pull/408) (`c3e4aa94`),
> verified on `main` by content rather than by the PR's label.** The step checkboxes below were
> never ticked during execution and are left as written — the record of what shipped is the merge
> and the ROADMAP's 6.2.1 entry, not this file's boxes. Two caveats belong with it: the change
> touched **21 files across five projects**, not the 17 the design estimated, and the
> `Rag.NET.E2ETests` GraphRag tests **never ran** (no Docker daemon on the build machine), so their
> rewritten local-search assertion is verified by reading only.

**Goal:** Stop shipping `GraphLocalSearchBehavior` and its three options from `Rag.NET.GraphRag`, while keeping the three pinned figures it produced reproducible through a frozen copy in the measurement harness.

**Architecture:** The behaviour moves out of the published package into `Rag.NET.Benchmarks.Quality.IntegrationTests` as `LegacyPageRankLocalSearch`, a fixture whose only job is to reproduce figures measured against deleted code. The shipped local search is the Microsoft-spec implementation under `LocalSearch/`, untouched. `GraphRagRetrievalOptions` loses its three local-search properties and is renamed `GraphRagGlobalSearchOptions`.

**Tech Stack:** .NET 10, C#, xunit.v3, BenchmarkDotNet, ZeroAlloc.Validation source generator.

**Spec:** [`2026-08-27-delete-pagerank-local-search-design.md`](2026-08-27-delete-pagerank-local-search-design.md)
**Impact analysis:** [`2026-08-27-delete-pagerank-local-search-impact-analysis.md`](2026-08-27-delete-pagerank-local-search-impact-analysis.md)

## Global Constraints

- **`TreatWarningsAsErrors=true`** (`Directory.Build.props`). Any warning fails the build. This is the completeness guarantee for the deletion — do not suppress it to get a green build.
- **`dotnet test --filter` is silently ignored in this repo.** `TestingPlatformDotnetTestSupport` with `xunit.v3` discards the VSTest filter and runs **all** test classes. Every test command below invokes the runner directly with `-class`. Before relying on a filtered run, verify the reported test count matches what you expect.
- **Do not set `RAGNET_BEIR_LONG_RUNS=1` or `RAGNET_GRAPHRAG_ANSWERS_GENERATE=1`** for any task in this plan. No task here needs a generation run; those variables unlock expensive tests.
- **Commit message format:** conventional commits, header ≤ 100 chars, types from `.commitlintrc.yml` (`bench build chore ci docs feat fix perf refactor revert style test`). CI lints every commit a PR adds.
- **The `retrieval:` parameter name is not renamed.** Only the type. (Decision 3.)
- **`docs/plans/**` and `.superpowers/sdd/**` are never updated** — historical records.

## Task boundaries and why

Six tasks. Task 1 is project plumbing that Task 2 cannot compile without. Tasks 2–3 establish the fixture and prove it reproduces the figures **while the original still exists** — the only point at which that comparison is possible. Task 4 is the deletion, atomic with the doc update. Tasks 5–6 are prose and the rename.

---

### Task 1: Give the harness access to the internals the fixture needs

**Why this is a task and not a footnote:** the fixture calls `GraphChunkSearch` (`internal` to `Rag.NET.GraphRag`) and `RagTelemetrySource` (in `src/Shared`, linked per-project). `Rag.NET.GraphRag.csproj` grants `InternalsVisibleTo` to `Rag.NET.GraphRag.Tests` **only**, and `src/Shared/RagTelemetrySource.cs` is not linked into the harness. Without this task, Task 2 does not compile.

**Files:**
- Modify: `src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj` (the `AssemblyAttribute` ItemGroup, ~line 30-39)
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj`

**Interfaces:**
- Produces: `GraphChunkSearch.SearchAsync` and `RagTelemetrySource.ActivitySource` become visible to `Rag.NET.Benchmarks.Quality.IntegrationTests`.

- [ ] **Step 1: Add the IVT grant**

In `src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj`, inside the existing `ItemGroup` that already contains the `Rag.NET.GraphRag.Tests` grant, add a second attribute. Keep the existing comment; add your own explaining the new grant:

```xml
    <!-- The measurement harness carries LegacyPageRankLocalSearch, a frozen copy of the deleted
         GraphLocalSearchBehavior that keeps three pinned figures reproducible. It calls
         GraphChunkSearch, which is internal. See
         docs/plans/2026-08-27-delete-pagerank-local-search-design.md. -->
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.Benchmarks.Quality.IntegrationTests</_Parameter1>
    </AssemblyAttribute>
```

- [ ] **Step 2: Link the shared telemetry source into the harness**

In `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj`, add to an `ItemGroup` containing `Compile` items (create one if none exists):

```xml
    <!-- RagTelemetrySource is internal-per-assembly and linked, not referenced. The fixture in
         LegacyPageRankLocalSearch.cs starts an activity exactly as the deleted behaviour did. -->
    <Compile Include="..\..\src\Shared\RagTelemetrySource.cs" Link="Telemetry\RagTelemetrySource.cs" />
```

- [ ] **Step 3: Verify the solution still builds**

```bash
dotnet build tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj -v q --nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. A warning here fails CI — most likely cause is a duplicate `RagTelemetrySource` if the project already linked it transitively; if so, remove your `Compile` line and re-run.

- [ ] **Step 4: Commit**

```bash
git add src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj
git commit -m "build(graphrag): let the quality harness see the internals the legacy fixture needs"
```

---

### Task 2: Add the frozen fixture and its options, with the ten tests that guard it

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/LegacyPageRankLocalSearch.cs`
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/LegacyPageRankLocalSearchTests.cs`
- Read (source of the copy): `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs`, `tests/Rag.NET.GraphRag.Tests/GraphLocalSearchBehaviorTests.cs`

**Interfaces:**
- Produces: `LegacyPageRankLocalSearch(IGraphStore, LegacyPageRankOptions, GraphChunkStore, IEmbeddingGenerator<string, Embedding<float>>) : IRetrievalBehavior` — same four-parameter primary constructor and same `HandleAsync` signature as the deleted `GraphLocalSearchBehavior`, so call sites change only the type name.
- Produces: `LegacyPageRankOptions` with `double PageRankWeight` (**default 0.3**), `int LocalSearchDepth` (default 1), `int LocalTopEntities` (default 10).

- [ ] **Step 1: Copy the behaviour file verbatim, then make exactly four edits**

```bash
cp src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs tests/Rag.NET.Benchmarks.Quality.IntegrationTests/LegacyPageRankLocalSearch.cs
```

**Copy, do not retype.** The fixture's whole value is that it computes what the pinned figures measured; a hand-transcribed 161-line copy risks exactly the silent divergence this fixture exists to prevent (impact analysis R5). The four edits:

1. `namespace Rag.NET.GraphRag;` → `namespace Rag.NET.Benchmarks.Quality.IntegrationTests;`
2. Add `using Rag.NET.GraphRag;` to the using block (it now needs `GraphChunkSearch`, `GraphChunkStore` from that namespace).
3. `public sealed class GraphLocalSearchBehavior(` → `internal sealed class LegacyPageRankLocalSearch(`
4. The constructor's second parameter `GraphRagRetrievalOptions options` → `LegacyPageRankOptions options`

- [ ] **Step 2: Replace the class doc comment**

Delete the existing `<summary>`/`<remarks>` block and put this in its place. The comment is load-bearing — it is what stops a future reader tidying a fixture whose fidelity three published numbers depend on:

```csharp
/// <summary>
/// A frozen copy of the deleted <c>GraphLocalSearchBehavior</c>, kept so the figures measured
/// through it stay reproducible.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a measurement fixture, not an implementation. Do not improve it.</b> It was removed
/// from <c>Rag.NET.GraphRag</c> on 2026-08-27 because it blends PageRank into dense retrieval
/// scores, which is not in Microsoft's local search at all — that blend was the entire −0.02761
/// nDCG@10 charged to GraphRAG in Milestone 5.2. The shipped local search is
/// <c>Rag.NET.GraphRag.LocalSearch.IGraphRagSearch</c>, measured at 0.3459 overall and 0.8603 on
/// inference.
/// </para>
/// <para>
/// Three pinned figures execute this code and nothing else can reproduce them:
/// <c>MultiHopRagAnswerReproduction</c>'s local arm (0.2102 at weight 0.3),
/// <c>BeirReproduction</c>'s GraphRag nDCG (0.56897), and the blend ablation in
/// <c>BeirGraphRagCorpusTests</c>. <b>Any behavioural change here silently changes what those
/// three numbers mean</b>, without failing anything — which is why
/// <c>LegacyPageRankLocalSearchTests</c> moved here with it.
/// </para>
/// </remarks>
```

- [ ] **Step 3: Write the options record**

Append to `LegacyPageRankLocalSearch.cs`:

```csharp
/// <summary>
/// The three <c>GraphRagRetrievalOptions</c> properties deleted alongside the behaviour, at the
/// values they carried when the pinned figures were measured.
/// </summary>
/// <remarks>
/// <b><c>PageRankWeight</c> defaults to 0.3, not 0.</b> 0.3 was the shipped default when the
/// figures were taken; it became 0 in #296, which made the blend the identity. Defaulting to 0
/// here would make the fixture a no-op and silently turn every arm into its own control.
/// </remarks>
internal sealed class LegacyPageRankOptions
{
    /// <summary>PageRank-versus-similarity blend weight. 0.3 when the figures were measured.</summary>
    public double PageRankWeight { get; set; } = 0.3;

    /// <summary>Hop depth for local entity traversal.</summary>
    public int LocalSearchDepth { get; set; } = 1;

    /// <summary>How many entity chunks seed the traversal.</summary>
    public int LocalTopEntities { get; set; } = 10;
}
```

- [ ] **Step 4: Copy the ten unit tests**

```bash
cp tests/Rag.NET.GraphRag.Tests/GraphLocalSearchBehaviorTests.cs tests/Rag.NET.Benchmarks.Quality.IntegrationTests/LegacyPageRankLocalSearchTests.cs
```

Edits: namespace → `Rag.NET.Benchmarks.Quality.IntegrationTests`; add `using Rag.NET.GraphRag;`; class name → `LegacyPageRankLocalSearchTests`; every `new GraphLocalSearchBehavior(` → `new LegacyPageRankLocalSearch(`; every `new GraphRagRetrievalOptions {` → `new LegacyPageRankOptions {`. **Do not change any assertion or any expected value** — an assertion changed to make a test pass is the fixture drifting, which is the failure this task exists to prevent.

The ten tests are `HandleAsync_FindsEntitiesAndTraversesNeighbors`, `HandleAsync_BlendsPageRankWithSimilarity`, `HandleAsync_NoEntityResults_ReturnsStandardResults`, `HandleAsync_RespectsLocalSearchDepth`, `HandleAsync_EntitiesWithZeroPageRank_BlendingStable`, `HandleAsync_ChunksFromDifferentDocumentsSharingChunkIndex_BothSurvive`, `HandleAsync_DuplicateChunkWithinOneDocument_CollapsesToHighestScore`, `AtTheDefaultWeight_NoGraphWalkHappensAtAll`, `AtANonZeroWeight_TheWalkStillHappens`, `AtTheDefaultWeight_DuplicatesAreStillCollapsed`.

**`AtTheDefaultWeight_*` needs care:** "the default weight" meant 0 on the shipped type and means 0.3 on `LegacyPageRankOptions`. Those three tests must set `PageRankWeight = 0` explicitly rather than relying on the default, or they assert the opposite of what they did. Change the construction, never the assertion.

- [ ] **Step 5: Run the moved tests**

```bash
dotnet run --project tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Debug -- -class '*LegacyPageRankLocalSearchTests*'
```

Expected: **10 passed, 0 failed.** Verify the runner reports 10 — a lower number means `-class` matched nothing and you are reading a vacuous green.

- [ ] **Step 6: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/LegacyPageRankLocalSearch.cs tests/Rag.NET.Benchmarks.Quality.IntegrationTests/LegacyPageRankLocalSearchTests.cs
git commit -m "test(graphrag): add LegacyPageRankLocalSearch, a frozen copy of the behaviour being deleted"
```

---

### Task 3: Retarget the measurement harness and prove the three figures still reproduce

**This is the gate the whole change turns on.** After Task 4 the original is gone and this comparison becomes impossible.

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/GraphRagRun.cs` (lines ~112-140, ~305, ~324-333, ~361, ~685)
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagCorpusTests.cs` (~line 488)

**Interfaces:**
- Consumes: `LegacyPageRankLocalSearch`, `LegacyPageRankOptions` from Task 2.

- [ ] **Step 1: Retarget `GraphRagRun.cs`**

Change the field at ~line 136 from `new() { PageRankWeight = 0.3 }` typed as `GraphRagRetrievalOptions` to `LegacyPageRankOptions`, and the construction at ~line 333 from `new GraphLocalSearchBehavior(...)` to `new LegacyPageRankLocalSearch(...)`. The four constructor arguments are unchanged. Update the `<see cref="GraphLocalSearchBehavior"/>` references in the doc comments at ~140, ~305, ~361, ~685 to `<see cref="LegacyPageRankLocalSearch"/>`.

- [ ] **Step 2: Retarget the ablation**

In `BeirGraphRagCorpusTests.cs` ~line 488, `var unweighted = new GraphRagRetrievalOptions { PageRankWeight = 0.0 };` becomes `var unweighted = new LegacyPageRankOptions { PageRankWeight = 0.0 };`. The explicit `0.0` is already there, so the default change is harmless — leave it explicit.

- [ ] **Step 3: Build**

```bash
dotnet build tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj -v q --nologo
```

Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Reproduce the three pinned figures**

```bash
dotnet run --project tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release -- -class '*BeirReproduction*'
dotnet run --project tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release -- -class '*MultiHopRagAnswerReproduction*'
dotnet run --project tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release -- -class '*BeirGraphRagCorpusTests*'
```

Expected, to the precision recorded: `BeirReproduction` GraphRag **0.56897**; `MultiHopRagAnswerReproduction` local arm **0.2102**; the ablation showing `PageRankWeight = 0` reproducing the candidate-set control on **2,255 of 2,255** queries.

These replay cached answers and should generate nothing. **If any figure differs at the recorded precision, STOP and do not proceed to Task 4** — the copy is not faithful, and once the original is deleted there is nothing to diff against. Report the discrepancy.

If the runs skip for a missing corpus, the BEIR cache is at `~/.cache/ragnet-beir`; a skip is not a pass, and Task 4 must not proceed on one.

- [ ] **Step 5: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/GraphRagRun.cs tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagCorpusTests.cs
git commit -m "test(graphrag): reproduce the three pinned figures through the legacy fixture"
```

---

### Task 4: Delete from `src/`, atomic with the documentation

**Atomic:** the doc guard (`DocsCodeExamplesTests`) resolves every identifier in a ` ```csharp ` fence under `docs/**` against the real public API. `docs/guide/graphrag.md` uses all four deleted members in fences, so splitting this task leaves the build red between commits.

**Files:**
- Delete: `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs`
- Delete: `tests/Rag.NET.GraphRag.Tests/GraphLocalSearchBehaviorTests.cs`
- Modify: `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs`, `src/Rag.NET.GraphRag/RagBuilderExtensions.cs`
- Modify: `tests/Rag.NET.GraphRag.Tests/GraphRagOptionsValidationTests.cs`, `PipelinePlacementTests.cs`, `RagBuilderExtensionsTests.cs`, `GraphRagTelemetryTests.cs`
- Modify: `tests/Rag.NET.E2ETests/GraphRagFunctionalTests.cs`, `benchmarks/Rag.NET.Benchmarks/GraphRagBenchmarks.cs`
- Modify: `docs/guide/graphrag.md`

- [ ] **Step 1: Delete the two files**

```bash
git rm src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs tests/Rag.NET.GraphRag.Tests/GraphLocalSearchBehaviorTests.cs
```

- [ ] **Step 2: Remove the three properties**

In `GraphRagRetrievalOptions.cs` delete `LocalSearchDepth` (~line 21), `LocalTopEntities` (~line 34), `PageRankWeight` with its `[InclusiveBetween]` and `[Must]` attributes (~lines 40-79), and the `PageRankWeightIsFinite` method (~lines 81-84), each with its doc comment. Keep `GlobalBatchSize`, `GlobalReportCandidates`, `GlobalChatClient` and the `[Validate]` attribute on the class.

- [ ] **Step 3: Drop the DI registration**

In `RagBuilderExtensions.cs`, delete the `services.AddSingleton<GraphLocalSearchBehavior>(sp => new GraphLocalSearchBehavior(...));` block (~lines 173-178) from `RegisterRetrievalBehaviors`. Leave the `GraphGlobalSearchBehavior` and `GraphChunkRoutingBehavior` registrations. Update the `<see cref="GraphLocalSearchBehavior"/>` in the remarks at ~line 32 to name `LocalSearch.IGraphRagSearch` instead.

- [ ] **Step 4: Fix the dependent tests**

- `GraphRagOptionsValidationTests.cs` — delete the four methods `NonPositiveLocalSearchDepth_ThrowsAtRegistration`, `NonPositiveLocalTopEntities_ThrowsAtRegistration`, `PageRankWeightOutsideUnitRange_ThrowsAtRegistration`, `PageRankWeightAtUnitRangeBounds_IsAccepted`, with their `[Theory]`/`[InlineData]` attributes. Keep every `Global*` test.
- `RagBuilderExtensionsTests.cs` — delete the assertion at ~line 103 that the behaviour is registered. Keep ~line 19 (options type still registered).
- `PipelinePlacementTests.cs` — delete the test asserting the type is absent from the default chain (~line 86) and the one that adds it by hand (~lines 142-149).
- `GraphRagTelemetryTests.cs` — delete the test constructing the behaviour at ~lines 87-89. Its telemetry assertions cover deleted code.
- `GraphRagFunctionalTests.cs` — remove `.Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))` at ~line 177. Update the prose at ~line 263 to name the replacement.
- `GraphRagBenchmarks.cs` — delete the `_localSearchBehavior` field (~line 38), its construction (~lines 81-83) and its benchmark method. Keep the global-search benchmark (Decision 2).

- [ ] **Step 5: Update the guide, in this same task**

In `docs/guide/graphrag.md`: remove `.Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))` from the fence at ~line 91, and the three `options.LocalSearchDepth` / `options.LocalTopEntities` / `options.PageRankWeight` lines from the fence at ~lines 143-145. Replace the surrounding prose with a short note that keeps the finding rather than only the removal:

> The PageRank blend was removed in v1.0. It scored local search by mixing PageRank into dense
> similarity, which is not part of Microsoft's local search; at its shipped default the blend
> demoted the very chunks the graph walk had reached, and at weight 0 it reproduced the plain
> candidate set on 2,255 of 2,255 queries. Local search is now
> `LocalSearch.IGraphRagSearch`, measured at 0.3459 overall and 0.8603 on inference questions.

Keep the existing Known Limitations entries.

- [ ] **Step 6: Build the whole solution**

```bash
dotnet build -v q --nologo
```

Expected: `0 Warning(s) 0 Error(s)`. `TreatWarningsAsErrors` means any reference you missed appears here. Fix and re-run until clean.

- [ ] **Step 7: Run the guards**

```bash
dotnet run --project tests/Rag.NET.PackageValidation.Tests -c Debug -- -class '*DocsCodeExamplesTests*'
dotnet run --project tests/Rag.NET.GraphRag.Tests -c Debug
dotnet run --project tests/Rag.NET.E2ETests -c Debug -- -class '*GraphRagFunctionalTests*'
```

Expected: all pass. A `DocsCodeExamplesTests` failure names the exact file and identifier — that is Step 5 incomplete.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(graphrag)!: delete the PageRank-blend local search and its three options"
```

The `!` marks the breaking change; release-please reads it.

---

### Task 5: Prose sweep

**Files:** `tests/…Quality.IntegrationTests/AnswerArm.cs`, `BeirReproduction.cs`, `BeirRunBudget.cs`, `MultiHopRagAnswerReproduction.cs`, `BeirGraphRagAnswerTests.cs` (doc comments at ~lines 26-27 describe the arm as running through the behaviour), `GraphRagFunctionsTests.cs`, `src/Rag.NET.GraphRag/LocalSearch/LocalSearchContextBuilder.cs`, `src/Rag.NET.GraphRag/README.md`, `docs/reference/features.md`

- [ ] **Step 1: Update the references**

Each mentions `GraphLocalSearchBehavior` or `PageRankWeight` in comments or prose describing what was measured. Do **not** rewrite history — these say what was measured and when, and that stays true. Add that the code now lives in `LegacyPageRankLocalSearch` in the harness. `BeirRunBudget.cs:911` `nameof`s the ablation test method; leave it unless Task 3 renamed that method.

- [ ] **Step 2: Build and commit**

```bash
dotnet build -v q --nologo
git add -A
git commit -m "docs(graphrag): point the measurement prose at the relocated legacy behaviour"
```

---

### Task 6: Rename the options type

**Atomic** — the source generator emits `GraphRagRetrievalOptionsValidator` from `[Validate]`, so the class rename and its call site must land together or the build fails inside generated code.

**Files:** `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs` (renamed), `GraphGlobalSearchBehavior.cs`, `RagBuilderExtensions.cs`, plus the 11 remaining files that name the type.

- [ ] **Step 1: Rename file and type**

```bash
git mv src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs src/Rag.NET.GraphRag/GraphRagGlobalSearchOptions.cs
```

Rename the class to `GraphRagGlobalSearchOptions` and update its `<summary>` to say it configures GraphRAG global search.

- [ ] **Step 2: Update every reference, including the generated validator's call site**

Replace `GraphRagRetrievalOptions` with `GraphRagGlobalSearchOptions` across `src/`, `tests/` and `benchmarks/`. **In `RagBuilderExtensions.cs` ~line 72, `new GraphRagRetrievalOptionsValidator()` becomes `new GraphRagGlobalSearchOptionsValidator()`** — the generator renames it with the class, and this is the edit whose omission produces a confusing generated-code error.

**Leave the `retrieval:` parameter name alone** (Decision 3) — it is a repo-wide convention shared with `AddRagNet` and `UseRaptor`.

- [ ] **Step 3: Verify nothing is left**

```bash
grep -rn "GraphRagRetrievalOptions" --include=*.cs src/ tests/ benchmarks/ | grep -v "/bin/\|/obj/"
```

Expected: no output.

- [ ] **Step 4: Full build and suite**

```bash
dotnet build -v q --nologo
dotnet run --project tests/Rag.NET.GraphRag.Tests -c Debug
dotnet run --project tests/Rag.NET.PackageValidation.Tests -c Debug
```

Expected: all pass, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(graphrag)!: rename GraphRagRetrievalOptions to GraphRagGlobalSearchOptions"
```

---

## Done when

- `GraphLocalSearchBehavior`, `PageRankWeight`, `LocalSearchDepth`, `LocalTopEntities` appear nowhere in `src/`.
- The three pinned figures reproduce to recorded precision through `LegacyPageRankLocalSearch` (proved at Task 3, re-checkable at any time).
- `dotnet build` is clean with `TreatWarningsAsErrors`, and `DocsCodeExamplesTests` and `pack-validate` pass.
- `docs/guide/graphrag.md` documents the removal **and keeps the finding that caused it**.

## Out of scope

Any change to `LocalSearch/`; re-measuring anything; the second-corpus RAPTOR arm; the other 6.2.1 sweep threads (HyDE, reranking, hybrid BM25, late chunking, SPLADE, the answer engines, the vector-store parity leg, the pipeline-parity test).
