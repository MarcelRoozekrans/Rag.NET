# A Size Floor on RAPTOR's Cluster Count — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make RAPTOR build a usable tree over a real corpus, by bounding cluster size rather than cluster count.

**Architecture:** `RaptorOptions` gains `TargetClusterSize`. `SelectClusterCount` computes `floor = ceil(count / TargetClusterSize)` and uses it as a **floor on k, never a replacement for BIC**: below `BicMaxK` the floor only raises BIC's answer; above it BIC is unaffordable, so the floor is used directly and the model is fitted once. Below the target nothing changes at all, which is what keeps the blast radius reviewable.

**Tech Stack:** .NET 10, C#, xunit.v3, ZeroAlloc.Validation source generators.

**Spec:** `docs/plans/2026-08-22-raptor-cluster-size-floor-design.md`

**Issue:** #345.

## Global Constraints

- **`TreatWarningsAsErrors=true`** in `Directory.Build.props`. Analyzer diagnostics fail the build — MA0006, MA0051, MA0158, EPC13/MA0134, HLQ012/HLQ013/HLQ004, ZA1104 and CS9035 have all bitten in this package.
- **`StringComparison.Ordinal`** on string comparisons, **`StringComparer.Ordinal`** on string-keyed dictionaries, **`CultureInfo.InvariantCulture`** on number formatting.
- **Conventional commits, subject ≤ 100 characters** — commitlint enforces this and a 133-character subject has failed CI here before.
- **`main` is protected; work on a feature branch.**
- **Design records live in `docs/plans/`.** `DocsCodeExamplesTests` compiles every C# example elsewhere under `docs/` against the produced packages.
- **Below the target, behaviour must not change.** When `count <= TargetClusterSize`, `floor` is 1 and `Math.Max(bic, 1) == bic`. Every existing test runs in that regime, so **no existing assertion should move.** If one moves, stop and report it — it is a finding, not an adjustment to absorb.
- **Do not fix #348** (`Umap.Fit`'s O(n²) cost and LOH traffic), **#337** (the variance floor), **#336** or **#338**. They are filed. Report anything new; fix nothing outside this plan.

---

### Task 1: `TargetClusterSize`, with validation

The option first, so the behaviour change in Task 2 has something to read. Nothing consumes it yet.

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorOptions.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorOptionsValidationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `RaptorOptions.TargetClusterSize` — `int`, default `100`, validated `> 1`.

- [ ] **Step 1: Write the failing validation tests**

Read `RaptorOptionsValidationTests.cs` first and follow its existing shape — the file already tests `MaxClusters`, `MaxTreeDepth` and `ReducedDimensionality` bounds through the generated validator.

`result.Failures` is an **array**, and the file has a settled idiom for asserting on it — project the
property names, then `Assert.Contains` with `StringComparer.Ordinal`. Follow it exactly.

```csharp
[Fact]
public void TargetClusterSize_DefaultsTo100()
{
    Assert.Equal(100, new RaptorOptions().TargetClusterSize);
}

[Theory]
[InlineData(1)]
[InlineData(0)]
[InlineData(-1)]
public void TargetClusterSizeOfOneOrLess_IsRejected(int target)
{
    // A target of 1 puts every chunk in its own cluster — #333's degenerate shape reached from
    // the other direction, and a level that never reduces. Zero and negatives are nonsense that
    // would make ceil(count / target) divide by zero or go negative.
    var result = new RaptorOptionsValidator().Validate(new RaptorOptions { TargetClusterSize = target });

    Assert.False(result.IsValid);

    var failures = result.Failures;
    var reported = new string[failures.Length];
    for (var i = 0; i < failures.Length; i++)
    {
        reported[i] = failures[i].PropertyName;
    }

    Assert.Contains(nameof(RaptorOptions.TargetClusterSize), reported, StringComparer.Ordinal);
}
```

- [ ] **Step 2: Run them to verify they fail**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~TargetClusterSize"
```

Expected: FAIL to compile — `TargetClusterSize` does not exist.

- [ ] **Step 3: Add the option**

In `src/Rag.NET.Raptor/RaptorOptions.cs`, after `MaxClusters` and its `MaxClustersIsSet` companion:

```csharp
    /// <summary>
    /// The largest number of chunks a cluster should hold, which bounds how much text one
    /// summarisation prompt can contain. Default: 100.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a floor on the cluster count, not a cap.</b> <c>SelectClusterCount</c> computes
    /// <c>ceil(count / TargetClusterSize)</c> and never chooses a <c>k</c> below it, so no cluster
    /// exceeds this size. Below the target the option does nothing: the floor is 1, and BIC picks
    /// <c>k</c> exactly as it did before this option existed.
    /// </para>
    /// <para>
    /// <b>Why it exists.</b> Before it, <c>k</c> was capped at 10 per level regardless of the
    /// level's size, and the joined cluster text had no bound at all. On a 17,648-chunk corpus the
    /// smallest possible largest cluster was 1,765 chunks — about 730,000 characters, roughly
    /// 183,000 tokens against a 128,000-token context. The tree could not be built at any <c>k</c>
    /// the cap allowed (#345).
    /// </para>
    /// <para>
    /// <b>It counts chunks, not tokens.</b> At the stock <c>ChunkingOptions.MaxChunkSize</c> of 512
    /// characters, 100 chunks is at most ~51,000 characters — comfortably inside a 128,000-token
    /// context. A larger chunk size, or a model with a smaller context, wants a smaller target.
    /// </para>
    /// <para>
    /// Must be greater than 1 — enforced by the validation attribute. A target of 1 would put every
    /// chunk in its own cluster, which is #333's degenerate shape reached from the other direction
    /// and a level that never reduces.
    /// </para>
    /// </remarks>
    [GreaterThan(1)]
    public int TargetClusterSize { get; set; } = 100;
```

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~TargetClusterSize"
```

Expected: PASS.

- [ ] **Step 5: Run the whole package's tests**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Raptor.Tests
```

Expected: all PASS. **Nothing should have moved** — the option is not read by anything yet.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.Raptor/RaptorOptions.cs tests/Rag.NET.Raptor.Tests/RaptorOptionsValidationTests.cs
git commit -m "feat(raptor): add TargetClusterSize, unused for now (#345)"
```

---

### Task 2: The floor in `SelectClusterCount`

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorClusterSizeFloorTests.cs` (new file)

**Interfaces:**
- Consumes: `RaptorOptions.TargetClusterSize` from Task 1.
- Produces: no signature change. `SelectClusterCount(float[][] reduced, int count, Activity? activity)` keeps its shape; only what it returns changes. A new private constant `BicMaxK = 10`.

- [ ] **Step 1: Write the failing test — a level that today produces an over-target cluster**

`tests/Rag.NET.Raptor.Tests/RaptorClusterSizeFloorTests.cs`. Use the shared `RaptorTestContext` helpers (`ChatClient`, `Embedder`, `CreateContext`, `SetupChatClient`, `SetupEmbedder`) exactly as the other test classes in this project do — read one of them first.

**Assert the maximum cluster size, not `k`.** The size is what the prompt bound is about; asserting `k` would pin an implementation detail the spec deliberately leaves free.

```csharp
[Fact]
public async Task ALevelLargerThanTheTarget_ProducesNoClusterAboveIt()
{
    // 600 leaves at a target of 100 needs at least 6 clusters. Before the floor, k was capped
    // at 10 by BIC's maxK and could be as low as 2 — a 300-chunk cluster whose joined text has
    // no bound at all (#345). The corpus case is 17,648 chunks against the same cap.
    _helpers.SetupChatClient("a summary");
    _helpers.SetupEmbedder(dims: 8);
    var options = new RaptorOptions { TargetClusterSize = 100, MaxTreeDepth = 1 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
    var ctx = _helpers.CreateContext(chunkCount: 600);

    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
        new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

    var summaries = ctx.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
    Assert.True(summaries.Count >= 6,
        $"600 leaves at a target of 100 needs at least 6 clusters; got {summaries.Count}");
}
```

`MaxTreeDepth = 1` keeps the test to the one level under examination and keeps it fast.

**The summary count is the observable proxy for cluster count** — one summary per cluster (`BuildLevelAsync` appends one per `ClusterGroup`), so `summaries.Count >= 6` means no cluster held more than 100 of the 600 leaves.

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~RaptorClusterSizeFloorTests"
```

Expected: FAIL — BIC picks some k ≤ 10 with no floor, and on 600 near-collinear fake vectors it will pick a small one, so fewer than 6 summaries are produced.

**If it passes**, stop and report: BIC is already choosing ≥ 6 on this fixture, so the test does not exercise the defect. Raise the leaf count or lower the target until it fails, and say what you needed.

- [ ] **Step 3: Implement the floor**

In `RaptorIngestionBehavior`, add the named constant beside the class's other fields:

```csharp
    /// <summary>
    /// The largest <c>k</c> BIC selection is allowed to search. Was an inline literal in
    /// <see cref="SelectClusterCount"/>; named because the floor below is now compared against it.
    /// </summary>
    /// <remarks>
    /// It cannot simply be raised: <c>GaussianMixtureModel.SelectK</c> fits every <c>k</c> from 1 to
    /// this value, so the sweep's cost is linear in it and each fit is EM over the whole level. At
    /// corpus scale a value large enough to bound cluster size would be orders of magnitude more
    /// work than the single fit the derived path uses instead (#345).
    /// </remarks>
    private const int BicMaxK = 10;
```

Then replace the `k` assignment in `SelectClusterCount`:

```csharp
        // The smallest k that keeps every cluster at or under the target size. This is a FLOOR,
        // not a cap: below the target it is 1 and changes nothing, so BIC keeps choosing exactly
        // as it did before this existed.
        var sizeFloor = (int)System.Math.Ceiling(count / (double)options.TargetClusterSize);

        int k;
        if (options.MaxClusters.HasValue && options.MaxClusters.Value >= sizeFloor)
        {
            k = System.Math.Min(options.MaxClusters.Value, count - 1);
        }
        else if (options.MaxClusters.HasValue)
        {
            // The cap cannot be honoured without producing a cluster above the target — an
            // unsendable prompt. The floor is a correctness bound and the cap is a preference,
            // so the floor wins and the span records that it did.
            activity?.SetTag("raptor.cluster.maxclusters.overridden", true);
            k = sizeFloor;
        }
        else if (sizeFloor <= BicMaxK)
        {
            // BIC still chooses; the floor can only raise its answer.
            k = System.Math.Max(
                GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(count, BicMaxK)),
                sizeFloor);
        }
        else
        {
            // BIC is unaffordable at this scale — it would fit every k up to sizeFloor. Derive k
            // and fit once.
            k = sizeFloor;
        }

        k = System.Math.Min(k, count - 1);
```

Leave both existing rejection checks (`k <= 1` and `k >= count`) exactly as they are, including their comments.

- [ ] **Step 4: Run the new test to verify it passes**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~RaptorClusterSizeFloorTests"
```

Expected: PASS.

- [ ] **Step 5: Mutation-check the floor**

Comment out the `sizeFloor` clauses so `k` falls through to plain BIC, re-run the test, and confirm it **fails**. Restore, re-run, confirm it **passes**. **Report both observations with the numbers you saw.**

This package has shipped four separate checks that could not fail for what they claimed — #332's regression test, `SetupEmbedder`, `CorpusRebuildCount`, and the `raptorfiltered` under-fill condition. A test asserted but never seen to fail is not evidence here.

- [ ] **Step 6: Add the `MaxClusters` override test**

```csharp
[Fact]
public async Task MaxClustersYieldsToTheFloor_WhenHonouringItWouldExceedTheTarget()
{
    // MaxClusters is a preference; the size floor is a correctness bound. A cap of 2 over 600
    // leaves at a target of 100 would mean 300-chunk clusters and an unsendable prompt.
    _helpers.SetupChatClient("a summary");
    _helpers.SetupEmbedder(dims: 8);
    var options = new RaptorOptions { TargetClusterSize = 100, MaxClusters = 2, MaxTreeDepth = 1 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
    var ctx = _helpers.CreateContext(chunkCount: 600);

    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
        new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

    var summaries = ctx.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
    Assert.True(summaries.Count >= 6,
        $"MaxClusters = 2 must yield to the floor of 6; got {summaries.Count} clusters");
}
```

**And assert the telemetry tag**, in a second test — the spec requires the override be observable, not
merely correct. `RaptorTelemetryTests` shows the idiom: an `ActivityListener` collecting spans, then
`span.GetTagItem("...")`. Read it and follow it.

```csharp
[Fact]
public async Task WhenMaxClustersYieldsToTheFloor_TheSummarizeSpanRecordsIt()
{
    // A silently-exceeded cap is exactly what the doc comment promises not to do. The tag is how
    // a user finds out why their configured cap did not hold.
    var activities = new List<Activity>();
    using var listener = new ActivityListener
    {
        ShouldListenTo = source => string.Equals(source.Name, "Rag.NET", StringComparison.Ordinal),
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activities.Add,
    };
    ActivitySource.AddActivityListener(listener);

    _helpers.SetupChatClient("a summary");
    _helpers.SetupEmbedder(dims: 8);
    var options = new RaptorOptions { TargetClusterSize = 100, MaxClusters = 2, MaxTreeDepth = 1 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
    var ctx = _helpers.CreateContext(chunkCount: 600);

    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
        new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

    var summarize = activities.Single(a => string.Equals(a.OperationName, "ragnet.raptor.summarize", StringComparison.Ordinal));
    Assert.Equal(true, summarize.GetTagItem("raptor.cluster.maxclusters.overridden"));
}
```

If the exact `ActivityListener` construction differs in `RaptorTelemetryTests`, copy that file's
version rather than this one — it is the working reference.

- [ ] **Step 7: Add the unchanged-behaviour test**

```csharp
[Fact]
public async Task ALevelSmallerThanTheTarget_IsUnaffectedByTheFloor()
{
    // The floor is 1 here, so Math.Max(bic, 1) == bic and BIC chooses alone. This is the regime
    // every pre-existing test runs in, and it must be untouched.
    _helpers.SetupChatClient("a summary");
    _helpers.SetupEmbedder(dims: 8);
    var withFloor = new RaptorOptions { TargetClusterSize = 100, MaxTreeDepth = 1 };
    var wideTarget = new RaptorOptions { TargetClusterSize = 10_000, MaxTreeDepth = 1 };

    var a = await SummaryCountAsync(withFloor, chunkCount: 24);
    var b = await SummaryCountAsync(wideTarget, chunkCount: 24);

    Assert.Equal(a, b);
    Assert.True(a > 0, "the fixture must actually build a level for this comparison to mean anything");
}
```

`SummaryCountAsync` is a private helper you write in this file: it builds a behaviour with the given options, runs `HandleAsync` over a context of the given size, and returns the count of chunks carrying `raptor_level`.

- [ ] **Step 8: Run the full package suite**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Raptor.Tests
```

Expected: **all PASS with no pre-existing assertion moved.** Every existing test runs below the default target of 100, where the floor is 1 and inert.

**If any existing test moves, stop and report it.** Do not adjust it. The spec's central claim is that this regime is untouched, and a moved assertion falsifies that claim — which is worth more than the fix.

- [ ] **Step 9: Commit**

```bash
git add src/Rag.NET.Raptor/RaptorIngestionBehavior.cs tests/Rag.NET.Raptor.Tests/RaptorClusterSizeFloorTests.cs
git commit -m "fix(raptor): floor the cluster count so no cluster exceeds the target size (#345)"
```

---

### Task 3: Document it, and correct what #345 made wrong

**Files:**
- Modify: `docs/guide/raptor.md`
- Modify: `docs/reference/opentelemetry.md`

**Interfaces:**
- Consumes: everything from Tasks 1–2.
- Produces: no code.

- [ ] **Step 1: Replace the Known Limitation with the fix**

`docs/guide/raptor.md` has a Known Limitations entry for #345 saying the corpus tree cannot be built. That is no longer true. Replace it with a description of `TargetClusterSize`: what it does, its default, that it counts chunks rather than tokens, and the arithmetic — 100 chunks at the stock 512-character `MaxChunkSize` is at most ~51,000 characters.

Keep the entries for #336 and #338; they are still open.

- [ ] **Step 2: Correct the `MaxClusters` advice, again**

The guide's `MaxClusters` description must now say that the cap yields to `TargetClusterSize` where honouring it would produce an over-target cluster, and that telemetry records when that happens. **A documented option that is not honoured in one case must say so** — a user whose cap is silently exceeded and cannot find out why is worse off than one who reads the rule.

- [ ] **Step 3: Add the new telemetry tag**

`docs/reference/opentelemetry.md` lists `ragnet.raptor.summarize`'s tags. Add `raptor.cluster.maxclusters.overridden`, describing when it is set.

- [ ] **Step 4: Verify the docs examples still compile**

`DocsCodeExamplesTests` compiles every C# example under `docs/` except `docs/plans/`. If you added or changed a `csharp` block in either file, run:

```
rm -rf artifacts/packages
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="0.1.1-local.1"
dotnet test tests/Rag.NET.PackageValidation.Tests -c Release
```

Expected: `DocsCodeExamplesTests` passes. `EveryPackageCarriesTheVersionGitVersionDerives` will fail on the made-up version string — that is a local artifact of packing by hand, not a real failure. If you changed no `csharp` block, skip this step and say so.

- [ ] **Step 5: Commit**

```bash
git add docs/guide/raptor.md docs/reference/opentelemetry.md
git commit -m "docs(raptor): TargetClusterSize replaces #345's known limitation"
```

---

## Notes for the executor

**The claim to protect is that below the target nothing changes.** It is what makes this fix reviewable, and Task 2 Step 8 is where it gets tested. A moved assertion there is a finding to surface, not a number to update.

**Assert cluster size, never `k`.** The spec deliberately leaves the exact `k` free above the floor. A test pinning `k` would fail the moment BIC's answer shifts for an unrelated reason, and would be testing the implementation rather than the bound.

**Do not fix what you find.** #348 (`Umap.Fit`'s O(n²) cost and ~2.5 GB of LOH traffic), #337 (the variance floor), #336 and #338 are all filed and open. #348 in particular will still make a corpus build slow or memory-hungry after this fix — raising `k` does not help, because UMAP runs before clustering. Report anything new; fix nothing.

**This unblocks a measurement but does not run it.** Phase 6.2.1's Tasks 4–6 (`docs/plans/2026-08-21-raptor-real-protocol-implementation.md`) spend real money and are gated on the operator. Do not start them.
