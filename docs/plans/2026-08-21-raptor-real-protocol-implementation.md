# RAPTOR Under the Real Protocol — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure RAPTOR on MultiHop-RAG with a real model and a real corpus, differenced against controls, and pin the figures — the first measurement RAPTOR has ever had.

**Architecture:** A `RaptorRun` class mirrors `GraphRagRun`. Under `Corpus` scope it **bypasses `RaptorIngestionBehavior.HandleAsync` entirely** — writing leaves straight to the leaf store and the vector store — then calls `RaptorTreeRebuilder.RebuildAsync()` **once**. *(Task 1 replaced this plan's original approach of merely suppressing the growth debounce: the debounce baseline after the first document is that article's chunk count, so a second whole-corpus build fires around article 101 of 609, and whether it fires at all depends on which article is ingested first. A measurement harness cannot carry a hidden variable of that shape.)* Four new arms join the existing answer harness, sharing two ingestions (one corpus-scope store, one per-document store) and differing only in retrieval policy. A cheap pilot gates the expensive sweep.

**Tech Stack:** .NET 10, C#, xunit.v3, `Rag.NET.Raptor`, `Rag.NET.Raptor.Store`, ONNX embeddings, `openai/gpt-4o-mini` at temperature 0.

**Spec:** `docs/plans/2026-08-20-raptor-real-protocol-design.md` — **read its 2026-08-21 amendment banner first.** The design was written before Phase 6.2.3 shipped and three of its sections were amended.

## Global Constraints

- **`TreatWarningsAsErrors=true`** in `Directory.Build.props`. Analyzer diagnostics (Meziantou, Roslynator, HLQ*, ZA*) fail the build.
- **`StringComparison.Ordinal`** on string comparisons, **`StringComparer.Ordinal`** on string-keyed dictionaries, **`CultureInfo.InvariantCulture`** on number formatting.
- **Commits are conventional with free scopes**: `<type>(<scope>): <subject>`, **subject ≤ 100 characters** — commitlint enforces this and a 133-character subject failed CI on #340.
- **`main` is protected; work on a feature branch.**
- **Plans and design records live in `docs/plans/`.** `DocsCodeExamplesTests` compiles every C# example under `docs/` against the produced packages and excludes only that directory. A design doc anywhere else under `docs/` fails CI.
- **`ContextChunks = 6`** — the top-k for every answer arm. Do not change it; every pinned figure is at top-6.
- **The answer model is `openai/gpt-4o-mini` at temperature 0**, prompt on `BeirGraphRagAnswerTests`. Changing either invalidates every pinned figure.
- **Never weaken an assertion to make a test pass.** If a figure moves, that is the finding — report it.

---

### Task 1: `RaptorRun` — ingest the corpus and rebuild once

The harness needs a RAPTOR equivalent of `GraphRagRun`. The critical behaviour is **not** ingesting normally: at the shipped `CorpusGrowthThreshold = 0.10`, ingesting 609 articles triggers **48 whole-corpus rebuilds**, each re-clustering everything so far and summarising it.

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/RaptorRun.cs`
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/RaptorRunTests.cs`

**Interfaces:**
- Consumes: `Rag.NET.Raptor` (`RaptorOptions`, `RaptorTreeScope`, `RaptorRetrievalOptions`, `RaptorRetrievalMode`, `RaptorTreeRebuilder`, `RaptorCorpusDocumentId`), `Rag.NET.Raptor.Store` (`SqliteRaptorLeafStore`).
- Produces:
  - `internal sealed class RaptorRun : IAsyncDisposable`
  - `static Task<RaptorRun> BuildAsync(IReadOnlyList<BeirDocument> documents, RaptorTreeScope scope, OnnxEmbeddingGenerator generator, EmbeddingCache embeddings, IChatClient summariser, string leafStorePath, CancellationToken ct)`
  - `Task<IReadOnlyList<SearchResult>> SearchAsync(string query, RaptorRetrievalMode mode, int topK, CancellationToken ct)`
  - `int LeafCount { get; }`, `int SummaryCount { get; }`, `int CorpusRebuildCount { get; }` (counts `RaptorTreeRebuilder.RebuildAsync` invocations), `long SummariserCalls { get; }`

- [ ] **Step 1: Read the template before writing anything**

Read `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/GraphRagRun.cs` in full — especially `BuildAsync` (ingest, then one corpus-level operation) and how it counts replayed requests. `RaptorRun` mirrors its shape. Also read `BeirHarness.EmbedAsync` for how the harness embeds with a cache.

- [ ] **Step 2: Write the failing test — the debounce must be suppressed**

`tests/Rag.NET.Benchmarks.Quality.Tests/RaptorRunTests.cs`. This project is the **fast** tier; it must not need a model or a corpus, so use fakes.

```csharp
[Fact]
public async Task BuildAsync_BuildsTheTreeOnce_NotOncePerGrowthThreshold()
{
    // At the shipped CorpusGrowthThreshold of 0.10, ingesting 609 articles would trigger 48
    // whole-corpus rebuilds, each re-clustering every leaf so far and summarising it. A
    // benchmark must ingest with the debounce suppressed and rebuild once at the end —
    // exactly what RaptorTreeRebuilder documents itself for.
    var documents = FakeDocuments(count: 40, chunksEach: 3);

    await using var run = await BuildRunAsync(documents, RaptorTreeScope.Corpus);

    Assert.Equal(1, run.CorpusRebuildCount);
    Assert.True(run.SummaryCount > 0, "the rebuild must actually produce a tree");
}
```

`FakeDocuments` and `BuildRunAsync` are helpers you write in this file. `BuildRunAsync` wires a fake `IChatClient` returning a fixed summary and a fake embedder producing seeded, **varying** vectors — see `RaptorTestContext` in `tests/Rag.NET.Raptor.Tests/` for the shape, and note its seed is derived per document id precisely because a per-call reseed made every vector identical and hid two defects.

- [ ] **Step 3: Run it to verify it fails**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests --filter "FullyQualifiedName~RaptorRunTests"
```

Expected: FAIL to compile — `RaptorRun` does not exist.

- [ ] **Step 4: Implement `RaptorRun`**

```csharp
internal sealed class RaptorRun : IAsyncDisposable
{
    private readonly InMemoryVectorStore _store = new();
    private readonly SqliteRaptorLeafStore? _leafStore;
    private readonly RaptorRetrievalOptions _retrieval = new();
    private int _treeBuildCount;

    /// <summary>
    /// Ingests the corpus and, under <see cref="RaptorTreeScope.Corpus"/>, builds the tree
    /// exactly once at the end.
    /// </summary>
    /// <remarks>
    /// <b>CorpusGrowthThreshold is set to its maximum, not its default.</b> The default of 0.10
    /// rebuilds whenever the corpus grows 10%, which is right for a live corpus and ruinous for a
    /// bulk load: 609 articles trigger 48 whole-corpus rebuilds. One early build still happens —
    /// the behaviour's baseline starts at -1, so the first ingest always builds — but it is over
    /// one document's chunks and costs almost nothing. The tree that is measured comes from the
    /// single <see cref="RaptorTreeRebuilder.RebuildAsync"/> call after ingestion.
    /// </remarks>
    public static async Task<RaptorRun> BuildAsync(
        IReadOnlyList<BeirDocument> documents,
        RaptorTreeScope scope,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        IChatClient summariser,
        string leafStorePath,
        CancellationToken ct)
    {
        var run = new RaptorRun(scope, generator, embeddings, summariser, leafStorePath);
        await run.IngestAsync(documents, ct);

        if (scope == RaptorTreeScope.Corpus)
        {
            var produced = await run._rebuilder!.RebuildAsync(ct);
            run._treeBuildCount = 1;
            run.SummaryCount = produced;
        }

        return run;
    }

    /// <summary>How many corpus tree builds this run performed. A benchmark expects exactly one.</summary>
    public int CorpusRebuildCount => _treeBuildCount;
}
```

Set `CorpusGrowthThreshold = 100.0` (the validated maximum) on the `RaptorOptions` you construct, so ingestion does not rebuild. Construct `RaptorIngestionBehavior` and `RaptorTreeRebuilder` directly rather than through DI — `GraphRagRun` constructs its behaviours directly for the same reason, and the harness is not exercising registration.

Under `RaptorTreeScope.PerDocument`, do **not** call the rebuilder — per-document trees are built during ingestion. Leave `CorpusRebuildCount` at 0 for that scope and say so in the property's doc comment.

- [ ] **Step 5: Run the test to verify it passes**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests --filter "FullyQualifiedName~RaptorRunTests"
```

Expected: PASS.

- [ ] **Step 6: Add the per-document counterpart test**

```csharp
[Fact]
public async Task BuildAsync_PerDocumentScope_BuildsDuringIngestionAndNeverRebuilds()
{
    var documents = FakeDocuments(count: 40, chunksEach: 8);   // 8 clears MinChunksForRaptor's 5

    await using var run = await BuildRunAsync(documents, RaptorTreeScope.PerDocument);

    Assert.Equal(0, run.CorpusRebuildCount);
    Assert.True(run.SummaryCount > 0, "per-document trees are built during ingestion");
}
```

- [ ] **Step 7: Run both, then commit**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests
```

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/RaptorRun.cs tests/Rag.NET.Benchmarks.Quality.Tests/RaptorRunTests.cs
git commit -m "test(benchmarks): add RaptorRun, which rebuilds the corpus tree once"
```

---

### Task 2: The four arms and their empty pins

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerArm.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagAnswerReproduction.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `AnswerArm.RaptorCorpus` = `"raptorcorpus"`, `AnswerArm.Raptor` = `"raptor"`, `AnswerArm.RaptorFiltered` = `"raptorfiltered"`, `AnswerArm.RaptorBoost` = `"raptorboost"`, all added to `AnswerArm.All`.

- [ ] **Step 1: Add the arm constants**

Follow the documentation discipline of the existing six exactly — each says what it isolates, what it is *not*, and what its difference against its control means. Read `AnswerArm.cs` first.

```csharp
    /// <summary>
    /// RAPTOR at its shipped defaults since #340: corpus-level tree, <c>Blend</c>, top-6.
    /// </summary>
    /// <remarks>
    /// <b>This is the arm whose figure is RAPTOR's result on this corpus.</b> Not <see cref="Raptor"/>
    /// — that is the retired per-document variant. Publishing the per-document number as RAPTOR's
    /// would repeat 5.2's misattribution, where a variant's figure was presented as the technique's
    /// and took #316, #323 and #326 to unpick.
    /// </remarks>
    public const string RaptorCorpus = "raptorcorpus";

    /// <summary>
    /// RAPTOR's per-document tree — <c>TreeScope = PerDocument</c>, <c>Blend</c>, top-6.
    /// </summary>
    /// <remarks>
    /// The behaviour that shipped before #340, kept selectable rather than deleted precisely so
    /// this comparison could be run. <c>raptorcorpus − raptor</c> is what the 6.2.3 breaking change
    /// bought, on the corpus it was justified against — the number #331 was filed on and nobody has.
    /// </remarks>
    public const string Raptor = "raptor";

    /// <summary>
    /// The same corpus store as <see cref="RaptorCorpus"/>, with every summary chunk dropped
    /// before the top-6 is taken.
    /// </summary>
    /// <remarks>
    /// <b>A validation gate before it is a result.</b> Against <see cref="Dense"/> it should be
    /// ≈ 0: both see the same article chunks, so a difference means the corpora diverged and no
    /// other figure in the table means anything. Against <see cref="RaptorCorpus"/> it prices what
    /// the summaries do to the answer — negative means displacement, the graph path's finding
    /// (#247) reproduced for RAPTOR.
    /// </remarks>
    public const string RaptorFiltered = "raptorfiltered";

    /// <summary>
    /// The corpus store under <c>Boost</c> at the shipped 1.2, <b>after</b> Phase 6.2.4's
    /// over-fetch fix.
    /// </summary>
    /// <remarks>
    /// <b>Measures a working <c>Boost</c>, not the broken one.</b> Before 6.2.4 the behaviour saw
    /// only the truncated top-k, so it could reorder summaries within the result set but never
    /// promote one into it — provable from the code, and therefore not worth an answer arm.
    /// <c>raptorboost − raptorcorpus</c> is the question reading cannot answer: does promoting
    /// summaries into the context actually help?
    /// </remarks>
    public const string RaptorBoost = "raptorboost";
```

Add all four to `All`, after the existing six.

- [ ] **Step 2: Run the harness's unpinned-arm guard to watch it fail**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~Reproduction"
```

Expected: FAIL — the guard added in #280 fails an arm with no pin entry, in well under a second. **If it passes, stop and report:** the guard is not covering the new arms and the whole pinning discipline is inert for them.

- [ ] **Step 3: Add empty pin entries**

One per new arm, with an empty figure array and a provenance string saying it is unmeasured. Follow the shape of the existing entries in `MultiHopRagAnswerReproduction.Reproductions`:

```csharp
        new(
            "multihop-rag",
            AnswerArm.RaptorCorpus,
            [],
            "NOT YET MEASURED. Phase 6.2.1's RAPTOR sweep fills this. The entry exists so the " +
            "unpinned-arm guard passes while the arm is wired up and before it is run; a figure " +
            "without a run behind it would be worse than an empty array."),
```

- [ ] **Step 4: Run the guard to verify it passes**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~Reproduction"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerArm.cs tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagAnswerReproduction.cs
git commit -m "test(benchmarks): add the four RAPTOR answer arms, unmeasured"
```

---

### Task 3: Wire the arms into the answer harness

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs`

**Interfaces:**
- Consumes: `RaptorRun.BuildAsync` and `RaptorRun.SearchAsync` from Task 1; the four `AnswerArm` constants from Task 2.
- Produces: no new public surface — the arms become selectable through `RAGNET_GRAPHRAG_ANSWERS_ARMS`.

- [ ] **Step 1: Read how the existing arms share work**

Read `BeirGraphRagAnswerTests.cs` lines 320-400. Note that `local`, `control`, `filtered` and `localspec` share **one** local-search pass per query, because re-running it per arm would quadruple the cost for identical candidates. RAPTOR's arms need the same treatment: **two ingestions, not four**, since `raptorcorpus`, `raptorfiltered` and `raptorboost` all read the same corpus store.

- [ ] **Step 2: Build the two runs once, lazily**

Only construct a `RaptorRun` when an arm that needs it was selected — the existing code does this for the local-search pass and the same reasoning applies: an unselected arm must cost nothing.

```csharp
        var needsCorpus = arms.Contains(AnswerArm.RaptorCorpus, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.RaptorFiltered, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.RaptorBoost, StringComparer.Ordinal);
        var needsPerDocument = arms.Contains(AnswerArm.Raptor, StringComparer.Ordinal);
```

- [ ] **Step 3: Add the four cases to `RetrieveContextAsync`**

```csharp
            case AnswerArm.RaptorCorpus:
                return await corpusRun!.SearchAsync(query, RaptorRetrievalMode.Blend, ContextChunks, ct);
            case AnswerArm.Raptor:
                return await perDocumentRun!.SearchAsync(query, RaptorRetrievalMode.Blend, ContextChunks, ct);
            case AnswerArm.RaptorBoost:
                return await corpusRun!.SearchAsync(query, RaptorRetrievalMode.Boost, ContextChunks, ct);
            case AnswerArm.RaptorFiltered:
                var pool = await corpusRun!.SearchAsync(
                    query, RaptorRetrievalMode.Blend, ContextChunks * 4, ct);
                return Head(
                    pool.Where(r => !r.Chunk.Metadata.ContainsKey("raptor_level")).ToList(),
                    ContextChunks);
```

`raptorfiltered` over-fetches 4× and drops summaries before taking six — the same over-fetch-and-drop shape #247's `filtered` arm used, and for the same reason: dropping from an already-truncated six would under-fill and measure the wrong thing.

- [ ] **Step 4: Verify the arms are reachable without running a model**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~ArmSelection"
```

If no such test exists, add one asserting that each new arm name is accepted by the selector and that `AnswerArm.All` contains it. It must not invoke a model.

- [ ] **Step 5: Build and commit**

```
dotnet build Rag.NET.slnx
```

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs
git commit -m "test(benchmarks): dispatch the RAPTOR arms, two ingestions not four"
```

---

### Task 4: The pilot — the gate before spending

**This task spends money.** Its purpose is to catch setup errors before the full sweep, not to produce a figure.

> **COST MODEL CORRECTED 2026-08-24, after an attempt ran 5 hours and was stopped.** This paragraph
> used to read *"roughly 50 queries × 4 new arms, most of which will hit the existing answer
> cache"*. **That counted only answer generations (~250 calls) and omitted tree construction, which
> is where essentially all of the cost is.**
>
> The `raptor` arm is the **per-document** control, so evaluating it builds **609 separate trees**,
> every level of every one an LLM summarisation. Measured: 4,739 calls in 5 hours at a steady
> 21/min, with an estimated 15-20 hours and order $10-20 still to run. Details in
> [`2026-08-21-raptor-pilot-notes.md`](./2026-08-21-raptor-pilot-notes.md).
>
> **The gate does not need that arm.** `raptorfiltered - dense` uses corpus-scope arms only, and the
> corpus tree is already built and cached -- so the gate is cheap, and it is the one thing that must
> hold before any other figure means anything. `raptor` is therefore **dropped from the arm list
> below**; schedule the per-document build as its own ~15-20 hour job once the gate has held.

**Note for whoever runs this: `Umap.Fit`'s allocation profile may fail before #345's context-length error does.** `Umap.BuildKnnGraph` allocates `new (float, int)[n]` per row — at the corpus's 17,648 leaves that is 141,184 bytes per allocation (over the 85 KB large-object-heap threshold) repeated 17,648 times, roughly 2.5 GB of LOH traffic, on top of ~311M distance evaluations over 384 dimensions and 17,648 delegate-comparison `Array.Sort`s for level 1 alone. This is more likely to surface as gen2 GC thrash or an OOM on the pilot machine than as a clean "context length exceeded" response from the model — expect it, and do not read an OOM here as an unrelated environment problem. Whoever designs #345's fix should see this too: raising `k` so clusters are smaller does not make level 1's UMAP pass any cheaper — the leaf count going into `BuildKnnGraph` is unchanged either way.

**Files:**
- No source changes. This task runs the harness and records what it observed.
- Create: `docs/plans/2026-08-21-raptor-pilot-notes.md`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: a go/no-go decision recorded in the notes file.

- [ ] **Step 1: Confirm the preconditions before spending anything**

Check, and record each in the notes file:
- The MultiHop-RAG corpus is provisioned (609 articles, 17,648 chunks).
- The embedding cache is warm — a cold embed of the corpus is over an hour and is not this task's cost.
- **`OPENROUTER_API_KEY`** is set — not `OPENAI_API_KEY`; the harness routes through OpenRouter — and the model is `openai/gpt-4o-mini`.
- Phase 6.2.4's over-fetch fix is on `main` — `raptorboost` measures a working `Boost` and is meaningless without it.

**If any is false, stop and report.** Do not substitute a different model or a smaller corpus.

- [ ] **Step 2: Run the pilot**

```
RAGNET_BEIR_LONG_RUNS=1 \
RAGNET_GRAPHRAG_ANSWERS_GENERATE=1 \
RAGNET_GRAPHRAG_ANSWERS_ARMS=dense,raptorcorpus,raptorfiltered,raptorboost \
RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES=50 \
tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe \
  -class '*BeirGraphRagAnswerTests*'
```

> **DO NOT use `dotnet test --filter` here. It is silently ignored and runs the whole project.**
> This project sets `TestingPlatformDotnetTestSupport` with `xunit.v3`, so `dotnet test` routes
> through xunit's in-process runner, which does not honour the VSTest filter. It emits
> `warning MTP0001: ... VSTestTestCaseFilter` into the build output and then executes **all 25
> test classes** -- with `RAGNET_BEIR_LONG_RUNS=1` and `RAGNET_GRAPHRAG_ANSWERS_GENERATE=1` set,
> exactly the combination that unlocks every expensive test in the project. Observed 2026-08-24: a
> run launched this way was executing library-comparison sweeps (`arguana-semantic-kernel`,
> `scifact-ragnet-control`) unrelated to RAPTOR. Nothing failed; the tell was that the process was
> nearly idle where a RAPTOR tree should saturate a core.
>
> Verify the filter narrows before running -- `<exe> -list methods -class '*BeirGraphRagAnswerTests*'`
> must list only this class's methods, not the whole project's 25 classes.
>
> **The count is 8, not the 5 this said when written** (checked 2026-08-25). #345 and #360 added
> guard tests to the class since. Only `Accuracy_AgainstTheGoldAnswers_ThreeArms` spends money;
> the rest are fast. Read the class names in the listing rather than the count -- a stale number
> here would either abort a correct run or, worse, be "fixed" by loosening the filter.
>
> **When stopping a run, kill by assembly name.** The process is
> `Rag.NET.Benchmarks.Quality.IntegrationTests.exe`, not `dotnet` or `testhost`. On 2026-08-24 two
> "stopped" runs survived that mistake and were found 90 minutes later at 5.6 CPU-hours and 6.2 GB
> each, starving their replacement.

**All four variables are load-bearing.** `RAGNET_GRAPHRAG_ANSWERS_GENERATE` is what permits the run to call the model at all — without it the harness replays from cache and generates nothing, so a new arm produces no answers and no figure. `MAX_QUERIES` bounds the run to N queries stratified by type; absent, it runs every query, which is the full sweep rather than the pilot.

- [ ] **Step 3: Check the validation gate — this decides whether the sweep runs**

**`raptorfiltered − dense` must be ≈ 0.** Both arms see the same article chunks; `raptorfiltered` simply removes the summaries. A difference means the two corpora diverged — different chunker settings, a different corpus revision, or leaves that RAPTOR ingestion altered — and **no other figure in the table would mean anything.**

Record the two numbers and their difference in the notes file.

**If the gate fails, stop and report.** Do not proceed to the full sweep, and do not adjust the gate's tolerance to make it pass. #274's equivalent check reproduced to four decimals on both scoring rules; anything materially worse than that is a setup fault, not a finding.

- [ ] **Step 4: Record the tree the run actually built**

From `RaptorRun`'s counters, record in the notes: `LeafCount`, `SummaryCount`, `CorpusRebuildCount`, and `SummariserCalls`. Derive the summarisation cost per full run from `SummariserCalls`.

**`CorpusRebuildCount` cannot be the gate here — log it, do not gate on it.** `RaptorRun` sets it to `1` beside its one `RebuildAsync` call by construction (see the property's own doc comment), and under `Corpus` scope ingestion is structurally incapable of rebuilding along the way — there is no code path left that could make it read anything but `1`. A check against it can never fire, gate or not.

**Gate on the counters that can actually move instead, and stop and report if any looks wrong:**
- **`LeafCount` must read 17,648** — the full corpus's chunk count. Anything else means some documents were skipped or double-counted.
- **`SummariserCalls`** should be small relative to the corpus — one rebuild's worth of clustering, not one per document. Compare against the fast-tier bound `RaptorRunTests.MaxPlausibleSummariserCallsForOneRebuild` documents for the shape of what "one rebuild" looks like at small scale, and flag a count that looks like ingestion summarised along the way.
- **`SummaryCount`** should be positive — the rebuild must have actually produced a tree.

- [ ] **Step 5: Commit the notes**

```bash
git add docs/plans/2026-08-21-raptor-pilot-notes.md
git commit -m "docs(plans): RAPTOR pilot — validation gate and derived sweep cost"
```

---

### Task 5: The full sweep and the pins

**This is the expensive task.** Do not start it until Task 4's gate held.

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagAnswerReproduction.cs`

**Interfaces:**
- Consumes: Task 4's go decision and derived cost.
- Produces: four pinned figures with provenance.

- [ ] **Step 1: Run the full sweep**

```
RAGNET_BEIR_LONG_RUNS=1 \
RAGNET_GRAPHRAG_ANSWERS_GENERATE=1 \
RAGNET_GRAPHRAG_ANSWERS_ARMS=raptorcorpus,raptor,raptorfiltered,raptorboost \
tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe \
  -class '*BeirGraphRagAnswerTests*'
```

> **DO NOT use `dotnet test --filter` here. It is silently ignored and runs the whole project.**
> This project sets `TestingPlatformDotnetTestSupport` with `xunit.v3`, so `dotnet test` routes
> through xunit's in-process runner, which does not honour the VSTest filter. It emits
> `warning MTP0001: ... VSTestTestCaseFilter` into the build output and then executes **all 25
> test classes** -- with `RAGNET_BEIR_LONG_RUNS=1` and `RAGNET_GRAPHRAG_ANSWERS_GENERATE=1` set,
> exactly the combination that unlocks every expensive test in the project. Observed 2026-08-24: a
> run launched this way was executing library-comparison sweeps (`arguana-semantic-kernel`,
> `scifact-ragnet-control`) unrelated to RAPTOR. Nothing failed; the tell was that the process was
> nearly idle where a RAPTOR tree should saturate a core.
>
> Verify the filter narrows before running -- `<exe> -list methods -class '*BeirGraphRagAnswerTests*'`
> must list **5 methods**, not the whole project.
>
> **When stopping a run, kill by assembly name.** The process is
> `Rag.NET.Benchmarks.Quality.IntegrationTests.exe`, not `dotnet` or `testhost`. On 2026-08-24 two
> "stopped" runs survived that mistake and were found 90 minutes later at 5.6 CPU-hours and 6.2 GB
> each, starving their replacement.

`MAX_QUERIES` is deliberately absent — its absence is what makes this the full sweep. `dense` is omitted deliberately too — it is already pinned at 0.3499 and re-running it spends money to reproduce a known number. The reproduction test in Step 3 checks it instead.

- [ ] **Step 2: Pin the four figures**

Replace each empty pin entry with the measured figure and a provenance string. Follow the existing entries' discipline exactly — they state the date, the machine, the run's shape, the per-type breakdown, and **how to read the number**, including base rates where a yes/no type would otherwise flatter the arm.

State explicitly in `RaptorCorpus`'s provenance:
- `raptorcorpus − raptor` — what the 6.2.3 breaking change bought.
- `raptorcorpus − raptorfiltered` — what the summaries do to the answer.
- `raptorboost − raptorcorpus` — what a working `Boost` buys.
- `raptorfiltered − dense` — the validation gate's value, so a later reader can see it held.

- [ ] **Step 3: Run the reproduction to verify the pins hold**

```
RAGNET_BEIR_LONG_RUNS=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~Reproduction"
```

Expected: PASS, replaying from cache and generating nothing. A generated answer here means retrieval handed the model different context than the sweep did, and the figure is not reproducible — **report it rather than re-pinning to the new value.**

- [ ] **Step 4: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagAnswerReproduction.cs
git commit -m "test(benchmarks): pin RAPTOR's four arms on MultiHop-RAG"
```

---

### Task 6: Read the result honestly, and update the ledger

> **Executed 2026-08-27.** All four steps done: `docs/guide/raptor.md` gained its `## Measured`
> section (placed after *Migration*, where a reader choosing a scope is standing), `features.md`'s
> RAPTOR row moved off *"Not yet `benchmark`"*, `Rag.NET.Raptor.csproj` went `integration` →
> `benchmark`, and `dotnet build Rag.NET.slnx` (0 warnings) plus
> `dotnet test tests/Rag.NET.RepoConventions.Tests` (94 passed, 0 failed) both ran green.
>
> **Step 3's instruction was followed exactly: the RAPTOR *thread* is marked complete in 6.2.1's
> block, the phase is not.** The sweep still owes HyDE, reranking, hybrid BM25, late chunking,
> SPLADE, three answer engines, every vector store through the SciFact parity leg, the
> pipeline-parity test, the second-corpus RAPTOR arm, and local search's unexplained abstention.
>
> **One thing this task could not do, and the guide says so instead of hiding it:** the measurement
> says corpus scope is worse, and the default was *not* reverted. That is the 2026-08-27 decision
> (option 3) — MultiHop-RAG composes its questions from identifiable source articles, so it rewards
> per-document locality by construction and is the least neutral evidence on which to reverse a
> breaking default. The guide gives the reader a corpus-shaped rule rather than a verdict.


**Files:**
- Modify: `docs/guide/raptor.md`
- Modify: `docs/reference/features.md`
- Modify: `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`
- Modify: `docs/planning/ROADMAP.md`

- [x] **Step 1: State the finding in the guide**

Add a **Measured** section to `docs/guide/raptor.md` giving the corpus, the model, the top-k, the four figures and their differences. **Say plainly whether RAPTOR helps on this corpus.**

Milestone 6's bar is *measured*, not *good*: a feature measured and found wanting is a completion, as 5.2 was. If the summaries displace rather than help, say so in the guide — a user choosing RAPTOR deserves the number.

- [x] **Step 2: Raise the verification level**

`<VerifiedBy>integration</VerifiedBy>` becomes `<VerifiedBy>benchmark</VerifiedBy>` in `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`, with a comment naming the run and the pinned figures. `benchmark` means a measured run on a real corpus with a real model, pinned in a reproduction table — which, after Task 5, is true.

Update the RAPTOR row's *Exercised by* pointer in `docs/reference/features.md`.

- [x] **Step 3: Update the roadmap**

Mark 6.2.1's RAPTOR thread complete in its phase block, with the figures. **Do not mark the whole phase complete** — RAPTOR is one thread of a sweep that still owes HyDE, reranking, hybrid BM25, late chunking, SPLADE, the three answer engines, every vector store through the SciFact parity leg, the pipeline-parity test, #176, and local search's unexplained yes/no abstention.

- [x] **Step 4: Run the conventions tests and commit**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.RepoConventions.Tests
```

```bash
git add docs/ src/Rag.NET.Raptor/Rag.NET.Raptor.csproj
git commit -m "docs(raptor): state the measured result and raise VerifiedBy to benchmark"
```

---

## Notes for the executor

**The gate in Task 4 is real.** If `raptorfiltered − dense` is not ≈ 0, stopping is the correct outcome and costs a pilot. Proceeding costs a full sweep and produces four numbers that mean nothing.

**`raptorcorpus` is RAPTOR's result.** Not `raptor`. If you find yourself writing "RAPTOR scores X" using the per-document figure, stop — that is 5.2's mistake, which cost three weeks and a revised published finding.

**Do not fix anything you find.** #336 (BM25 postings accumulate on ingest-triggered rebuilds), #337 (the variance floor) and #338 (deletion does not reach the leaf store) are all open and documented. #336 in particular means **any arm touching hybrid retrieval is contaminated** — every arm here is dense, so they are unaffected, but do not add a hybrid arm without reading it first. Report anything new; fix nothing.

**Answers only, no ranking leg — a decision, not an omission.** The spec's "Open questions for the
plan" asked whether `raptorcorpus` should also be pinned as an nDCG@10 ranking figure. It should
not, here. Phase 6.2.1's bar for a retrieval technique is *a pinned figure with a control*, and the
answer arms satisfy it; a BEIR ranking leg is a second corpus run for a second currency, and this
phase still owes that treatment to HyDE, reranking, hybrid BM25, late chunking, SPLADE, three answer
engines and every vector store. Spending a ranking leg on the one technique that already has an
answer figure would be the least valuable place to put it. **If the answer figures come out
ambiguous** — a difference inside the noise the arms cannot separate — that is the trigger to add
one, and it should be recorded as its own thread rather than smuggled into this plan.

**Expect the tree to be shallow.** `SelectK` saturates near its filter ceiling on unstructured input, so a corpus of news articles may produce a broad, shallow tree rather than a deep hierarchy. That is a finding to record, not a bug to chase — and #337 is the filed reason.
