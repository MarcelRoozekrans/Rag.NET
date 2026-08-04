# Package Decomposition Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `Rag.NET` core stop shipping the dependencies of features nobody switched on, consolidate three satellite families, and give every package its own verified README.

**Architecture:** Four opt-in clusters move out of core into satellite packages that carry both the implementation and its builder method, following the existing `PgVectorBuilderExtensions` convention. Three satellite families merge where a user would never want one without the other *and* the dependency closure is already identical. Namespaces never change, so no consumer's source breaks.

**Tech Stack:** .NET 10, xUnit v3, `dotnet pack`, `dotnet nuget why`, reflection over compiled assemblies.

**Design:** `docs/plans/2026-08-04-package-decomposition-design.md`

---

## Ground rules for every task

- **Warnings are errors.** No `#pragma`, `SuppressMessage`, `NoWarn`, or `TreatWarningsAsErrors=false`. MA0051 (≤60-line methods), MA0048 (one public type per file), ERP022, EPC12/13, ZA0601.
- **xUnit v3**, `TestContext.Current.CancellationToken`, no sleeps.
- **Never `git add -A`** — explicit paths only. No `.nupkg`, dataset, model or cache file committed.
- **`Rag.NET.Benchmarks.Quality.Tests` runs with `--logger trx`**, output never piped through `head`/`tail`/`grep`.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **This phase changes no behaviour.** It moves code and rewrites project files. If a task cannot preserve behaviour, stop and report rather than adjusting behaviour to fit.

**Baselines:** `Rag.NET.Tests` 1318, `RepoConventions` 34 (33 + 1 by-design skip), `Benchmarks.Quality` 163, `Evaluation` 388, `PackageValidation` 15.

**New-package template** — copy this shape from `src/Rag.NET.VectorStores.PgVector/Rag.NET.VectorStores.PgVector.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.<Name></RootNamespace>
    <PackageId>Rag.NET.<Name></PackageId>
    <Description><one sentence describing THIS package, not the library></Description>
    <VerifiedBy><unit|container|none — see PackageVerificationTests></VerifiedBy>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.<Name>.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

---

# PART 1 — Decomposition and consolidation

## Task 1: The registration audit — this gates everything

**No code changes. This task decides how many extractions happen.**

For each of the four clusters, establish whether its types are reachable on the **default** path — i.e. what `AddRagNet()` composes when the user calls no opt-in method.

**Files to read:**
- `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` (`AddRagNet`, line 20)
- `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs:11-30` (the default `_types` list)
- `src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs`
- `src/Rag.NET/DependencyInjection/RagBuilder.cs`, `RagBuilderExtensions.cs`

**What is already known — verify, do not re-derive:**

| Cluster | Status going in |
|---|---|
| SQLite (7 `Sqlite*` stores) | **Not** in either pipeline builder. Behind `UseSqlitePersistence()` / `UseContentHashRecordManager()`. Expected clean. |
| Resilience (3 `Resilient*` types) | **Not** in either pipeline builder. Behind `ConfigureResilience()`. Expected clean. |
| Caching (2 behaviours) | **IS** in `RetrievalPipelineBuilder._types` — `ResultCacheBehavior` at index 1, `EmbeddingCacheBehavior` at index 12 of 17. **This is the risk.** |
| Tokenizer (`ConversationMemoryPipeline`, `CostAccounting`) | Unknown. Determine it. |

**Step 1: For the caching cluster, answer the decisive question.**

Being present in `_types` does not prove the behaviour *runs*. Determine: with no `UseCaching()` call, are `ResultCacheBehavior`/`EmbeddingCacheBehavior` (a) instantiated and no-op without a cache, or (b) absent because resolution fails/filters them? Read how `_types` is consumed to build the pipeline.

**Step 2: Write the decision record.**

Create `docs/plans/2026-08-04-registration-audit.md` recording, per cluster: reachable-by-default yes/no, the evidence (file:line), and the verdict — **extract** or **stays in core, with the reason**.

**Step 3: Commit.**

```bash
git add docs/plans/2026-08-04-registration-audit.md
git commit -m "docs(plans): registration audit gating the core extractions"
```

**Report:** the four verdicts. If the caching cluster is default-reachable, say so plainly — Task 5 changes shape and that is the correct outcome, not a failure.

---

## Task 2: Characterisation test — the safety net before anything moves

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/DefaultCompositionTests.cs`

This test must exist and pass **before** any extraction, and must still pass after all of them. It is the guard against §2's central risk.

**Step 1: Write the test.**

Assert the exact, ordered list of behaviour types the default `AddRagNet()` composition produces — both retrieval and ingestion pipelines. Hard-code the expected order as a literal array so any reordering is visible in the diff.

```csharp
[Fact]
public void DefaultRetrievalPipeline_ComposesTheSameBehavioursInTheSameOrder()
{
    var services = new ServiceCollection();
    services.AddRagNet(rag => { /* no opt-in calls */ });

    var actual = ResolveRetrievalBehaviourOrder(services);

    Assert.Equal(
        new[]
        {
            "SelfQueryBehavior", "ResultCacheBehavior", "LostInTheMiddleBehavior",
            /* … the full 17, in order … */
        },
        actual);
}
```

Add the equivalent for a **configured** composition that calls `UseCaching()`, `ConfigureResilience()` and `UseSqlitePersistence()`, so the opt-in paths are pinned too.

**Step 2: Run it.** It must PASS immediately — it describes today's behaviour.

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DefaultCompositionTests"
```

If it does not pass, the expected array is wrong; fix the array, never the product code.

**Step 3: Commit.**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/DefaultCompositionTests.cs
git commit -m "test(di): pin the default and configured pipeline composition before extraction"
```

---

## Task 3: Extract `Rag.NET.Storage.Sqlite`

Lowest-risk extraction — do this one first to establish the pattern.

**Files:**
- Create: `src/Rag.NET.Storage.Sqlite/Rag.NET.Storage.Sqlite.csproj`
- Move (`git mv`, preserving history) from `src/Rag.NET/Storage/`: `SqliteBm25Index.cs`, `SqliteContentHashStore.cs`, `SqliteCostLedger.cs`, `SqliteDocumentStore.cs`, `SqliteEmbeddingVersionStore.cs`, `SqliteParentChunkStore.cs`, `SqliteStoreHelper.cs`
- Create: `src/Rag.NET.Storage.Sqlite/SqliteBuilderExtensions.cs` — holds `UseSqlitePersistence`, `UseContentHashRecordManager`, and the SQLite cost-ledger/embedding-version wiring lifted from `RagBuilder.cs:243-274` and `RagBuilderExtensions.cs:102-355`
- Modify: `src/Rag.NET/Rag.NET.csproj` — remove `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`, `RagBuilderExtensions.cs`, `ServiceCollectionExtensions.cs` — remove the moved methods and any `Sqlite*` reference
- Create: `tests/Rag.NET.Storage.Sqlite.Tests/` — move the corresponding tests out of `tests/Rag.NET.Tests/`
- Modify: `Rag.NET.slnx` — add both new projects

**Keep the namespaces.** The moved types stay in `Rag.NET.Storage` / `Rag.NET.DependencyInjection` as they are today. Only their assembly changes.

**Step 1: Create the project and move the files.**

Use `git mv` so history follows. Add to `Rag.NET.slnx`.

**Step 2: Build. Expect failures in core** where removed methods were referenced. Fix by deleting the core-side declarations, not by re-adding references.

**Step 3: Verify the dependency actually left core.**

```bash
dotnet list src/Rag.NET/Rag.NET.csproj package --include-transitive | grep -i sqlite
```

Expected: **no output.** If SQLite still appears, something in core still references it — find it and move it; do not proceed.

**Step 4: Run the characterisation test from Task 2 and the full suite.**

```bash
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.Storage.Sqlite.Tests
```

`Rag.NET.Tests` will drop by however many tests moved — **state the new number and the arithmetic**; do not let it drift silently.

**Step 5: Commit** with the measured before/after closure count in the body.

---

## Task 4: Extract `Rag.NET.Resilience`

Same pattern as Task 3. **Largest win: 15 transitive packages.**

**Files:**
- Create: `src/Rag.NET.Resilience/Rag.NET.Resilience.csproj` (takes `Microsoft.Extensions.Resilience`, `System.Threading.RateLimiting`)
- Move from `src/Rag.NET/Resilience/`: `ResilientVectorStore.cs`, `ResilientSparseVectorStore.cs`, `ResilientEmbeddingGenerator.cs`, `RateLimitedChatClient.cs`, `RateLimitedEmbeddingGenerator.cs`, `TokenBucketRateLimiterAdapter.cs`, `FallbackChatClient.cs`
- Create: `src/Rag.NET.Resilience/ResilienceBuilderExtensions.cs` — `ConfigureResilience` lifted from `RagBuilder.cs:~330-375`, plus the rate-limiting and fallback registrations from `RagBuilderExtensions.cs:137-271`
- Modify: `src/Rag.NET/Rag.NET.csproj` — remove `Microsoft.Extensions.Resilience`, `System.Threading.RateLimiting`

**Decide deliberately:** `CostTrackingChatClient`, `CostTrackingEmbeddingGenerator`, `CostAccounting`, `InMemoryCostLedger` are cost concerns, not resilience. They do **not** move here. If they need the tokenizer, that is Task 6's problem.

**Step 3 verification:**

```bash
dotnet list src/Rag.NET/Rag.NET.csproj package --include-transitive | grep -iE "polly|resilience|telemetry|compliance"
```

Expected: **no output.** Record the closure count — it should fall by ~15.

---

## Task 5: Extract `Rag.NET.Caching` — the risky one

**Do not start until Task 1's verdict says extract.** If Task 1 found the behaviours default-reachable in a way that cannot be preserved, implement the seam described below and record why.

**Files:**
- Create: `src/Rag.NET.Caching/Rag.NET.Caching.csproj` (takes `Microsoft.Extensions.Caching.Hybrid`)
- Move: `src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs`, `ResultCacheBehavior.cs`
- Create: `src/Rag.NET.Caching/CachingBuilderExtensions.cs` — `UseCaching`
- Modify: `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs:11-30` — remove the two `typeof(...)` entries

**The seam already exists.** `RetrievalPipelineBuilder.Add<T>(after:, before:)` inserts at a named position. `UseCaching()` must re-insert both behaviours at their exact original positions:

- `ResultCacheBehavior` — `before: typeof(LostInTheMiddleBehavior)`
- `EmbeddingCacheBehavior` — `before: typeof(FilterBehavior)`

**Step 1: Before moving anything, extend Task 2's test** with a case that calls `UseCaching()` and asserts the resulting order is **byte-identical to today's default order**. Run it — it must pass before the move.

**Step 2:** Move the files, remove the two entries, wire `UseCaching()` with the `before:` anchors.

**Step 3: Run the characterisation tests.** The `UseCaching()` case must produce the identical 17-entry order. The no-opt-in case must now produce 15 entries — **and that is a deliberate, recorded change**: a user who never calls `UseCaching()` no longer has cache behaviours in their pipeline. Update the default-case expectation to the 15 with a comment stating why.

**If the two orders do not match exactly, stop.** Report the difference rather than adjusting the anchors until green.

**Step 4:**

```bash
dotnet list src/Rag.NET/Rag.NET.csproj package --include-transitive | grep -i "caching.hybrid\|caching.memory"
```

Expected: no output.

---

## Task 6: The tokenizer cluster — conditional

**Only proceed if Task 1's verdict was "extract".** Otherwise skip, and record in the commit for Task 11 that it stays in core with Task 1's reason.

`Microsoft.ML.Tokenizers` is also pulled by `Rag.NET.QueryTechniques`, which core references directly — so removing it from core's own `.csproj` may not remove it from core's closure. **Verify with `dotnet nuget why` before claiming a win.** If QueryTechniques keeps it in the closure regardless, the extraction saves nothing and should not be done; record that finding.

---

## Task 7: Merge `Rag.NET.Parsers.Office`

**Files:**
- Create: `src/Rag.NET.Parsers.Office/Rag.NET.Parsers.Office.csproj` (takes `DocumentFormat.OpenXml`)
- Move all `.cs` from `src/Rag.NET.Parsers.Word/`, `.Excel/`, `.PowerPoint/` (460 lines total)
- Delete the three old projects and their `.csproj`
- Merge the three test projects into `tests/Rag.NET.Parsers.Office.Tests/`
- Modify: `Rag.NET.slnx`

**Namespaces stay.** `Rag.NET.Parsers.Word` types keep the `Rag.NET.Parsers.Word` namespace inside the `Rag.NET.Parsers.Office` assembly. This is deliberate: it keeps consumer source unchanged. Set `<RootNamespace>Rag.NET.Parsers.Office</RootNamespace>` but do not rewrite existing namespace declarations.

**Step: verify no behaviour changed** — the merged test project must run the same number of tests as the three separately. State the arithmetic.

---

## Task 8: Merge `Rag.NET.DataProviders.Microsoft365`

Same pattern. Sources: `DataProviders.Exchange`, `.MicrosoftTeams`, `.OneDrive`, `.SharePoint` (1,363 lines). Takes `Microsoft.Graph`, `Microsoft.Kiota.Abstractions`, `Azure.Identity`, and `ProjectReference` to `Rag.NET.DataProviders`.

---

## Task 9: Merge the `Chunking` family

Fold `Rag.NET.Chunking.TokenAware` and `Rag.NET.Chunking.Semantic` into `Rag.NET.Chunking`. Delete the two projects; merge their tests.

**Watch:** `Chunking.Templates` has a `ProjectReference` to `Rag.NET.Chunking` — verify it still builds. `Chunking.CSharp` stays separate (Roslyn).

---

## Task 10: Move the Templates parsers

**Files:**
- Move `src/Rag.NET.Chunking.Templates/EmailTemplateDocumentParser.cs` → `src/Rag.NET.Parsers.Email/`
- Move `src/Rag.NET.Chunking.Templates/QAPairsDocumentParser.cs` → a parser home (`Rag.NET.Parsers.Office` if it reads Excel via ClosedXML; decide and state why)
- Modify `src/Rag.NET.Chunking.Templates/*.csproj` — remove `MimeKit`, `CsvHelper`, `ClosedXML`

**Verify the dependency direction does not invert.** If the moved parser needs template types, `Rag.NET.Parsers.Email` would need a reference back to `Chunking.Templates` — which is acceptable only if it does not create a cycle. If it does, keep a thin seam and record why.

**Step: prove the leak is gone.**

```bash
dotnet list src/Rag.NET.Chunking.Templates/*.csproj package --include-transitive | grep -iE "mimekit|csvhelper|closedxml"
```

Expected: no output.

---

## Task 11: The closure guards and the counts

**Files:**
- Modify: `tests/Rag.NET.PackageValidation.Tests/ProducedPackageTests.cs:39` — `ExpectedPackageCount`
- Create: `tests/Rag.NET.PackageValidation.Tests/DependencyClosureTests.cs`

**Step 1: Update the count as a stated decision.**

Arithmetic: 70 − 2 (Office) − 3 (M365) − 2 (Chunking) + N (extractions actually performed) = the new number. **Write the arithmetic in the comment at `ProducedPackageTests.cs:31-39`.** Never adjust the constant until the test goes green.

**Step 2: Write the closure guards.**

```csharp
[Fact]
public void ExtractedPackagesAreAbsentFromTheCoreClosure()
{
    // Core must not reference SQLite, Polly/Resilience, or HybridCache — the whole point
    // of the extraction. A re-added ProjectReference would silently undo it.
}

[Fact]
public void MergedPackagesDeclareTheUnionOfWhatTheirSourcesDeclared()
{
    // "No consumer pays more" is enforced, not asserted in a commit message.
}
```

Read each produced `.nupkg`'s nuspec, as `ProducedPackageTests` already does — **not** the `.csproj`, so the guard tests what ships.

**Step 3: Prove each guard fails.** Temporarily re-add `Microsoft.Data.Sqlite` to core, repack, confirm red, revert. **A guard you cannot prove bites is not a guard** — this repository has shipped three of those.

**Step 4:** Full suite at baselines, `dotnet pack` at 0 warnings, and record the new package count.

---

# PART 2 — READMEs and the chooser

## Task 12: Build the README guard first

**Build the guard before writing any README**, so every README is written against a check that already works.

**Files:**
- Create: `tests/Rag.NET.PackageValidation.Tests/PackageReadmeTests.cs`

**Step 1: Write the three failing tests.**

```csharp
[Fact] public void EveryPackageShipsItsOwnReadme()        // exists, and is NOT byte-identical to the repo README
[Fact] public void EveryReadmeNamesItsOwnPackageId()      // the `dotnet add package X` line matches the nuspec id
[Fact] public void EveryReadmeExampleResolvesAgainstTheAssembly()  // the one that matters
```

The third: parse each README's ```csharp fence, extract referenced type names and builder-method names, and assert each resolves as a public member of that package's compiled assembly by reflection. This catches the renamed method, the removed overload and the aspirational example — the defect that let `features.md` instruct users to do something impossible.

**Step 2: Run.** All three FAIL — 70 packages currently share the repo README.

**Step 3:** Do not implement yet. Commit the failing tests behind the count they will reach, and let Tasks 13–14 turn them green.

---

## Task 13: README template and the core packages

Write READMEs for `Rag.NET`, `Rag.NET.Abstractions`, and the three newly-extracted packages, using the design §5 structure: what it is / install / setup / working example / link.

**The example must be real** — the guard checks it. Prefer lifting from `samples/Rag.NET.Sample` or an existing test, which are compiled.

---

## Task 14: READMEs for the remaining packages

Batch by family (data providers, parsers, vector stores, chunking). Each still needs its **own** setup call and example — a templated README that says the same thing for every package fails `EveryReadmeNamesItsOwnPackageId` and defeats the purpose.

Run `PackageReadmeTests` after each batch. Finish only when all three tests are green with zero exclusions — **no allow-list.**

---

## Task 15: The package chooser

**Files:** create `docs/guide/choosing-packages.md`; link from `docs/guide/getting-started.md` and the root `README.md`.

State what the audit found: `Rag.NET` brings `Abstractions` transitively, each connector brings the `DataProviders` base, the default chunker is already in core, and the opt-in features now each name their package. Include the "I want X + Y" worked example that motivated this phase.

---

## Task 16: Close the phase

Update `docs/planning/ROADMAP.md` and `docs/planning/MILESTONE.md`. Record:

- the measured before/after closure count for core (**the headline: 43 → ~15**),
- the final package count with its arithmetic,
- **every cluster that did not extract, and why** — Task 1's verdicts are the record,
- the `Rag.NET.Mcp.Tool` 19 MB question, still open, owned before 6.3.

Do not tick a DoD box this phase did not make true.

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release                      # 0 warnings
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages # 0 warnings
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.PackageValidation.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.Evaluation.Tests
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests --logger trx
```

Then, the claim this phase exists to make true:

```bash
dotnet list src/Rag.NET/Rag.NET.csproj package --include-transitive | grep -c ">"
```

**Report the number against the 49 measured on 2026-08-04.**
