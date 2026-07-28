# Dataset Builder Verification Implementation Plan (Phase 3.2)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `EvaluationDatasetBuilder` produce datasets that are reproducible, free of corrupt samples, bounded in cost and memory — and pinned by tests that assert the right behaviour rather than the current one.

**Architecture:** The throttle-and-cost plumbing moves down from `RagasJudge` into a shared internal `EvaluationChatCaller` in `Rag.NET.Evaluation`; `RagasJudge` and the builder both compose it. Sampling becomes seeded reservoir sampling. A failed generation is excluded and counted rather than emitted.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI`, xUnit v3, NSubstitute (legal on `IChatClient` — `GetResponseAsync` returns `Task`, so EPS06 does not bite).

**Design:** `docs/plans/2026-07-28-dataset-builder-verification-design.md`. Read it first.

---

## Conventions that will fail the build if ignored

- **Warnings are errors.** MA0051 (methods ≤ 60 lines), MA0015 (`paramName` on argument exceptions), MA0048 (file name matches type name), ZA0601/ZA0501 (no LINQ/boxing in hot loops), EPS05/EPS06, HLQ013 (`foreach` not `for` over arrays).
- **No new `#pragma` or `SuppressMessage`.** Neither `Rag.NET.Evaluation` nor `Rag.NET.Evaluation.Ragas` has any; keep it that way.
- xUnit v3: always `TestContext.Current.CancellationToken`.
- **No sleeps in tests.** Signal with `TaskCompletionSource` and bounded `WaitAsync`.
- **Commits:** conventional, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — explicit paths. `.lucent/*` and `.claude/worktrees/*` are expected dirty; leave them.

Verify after every task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

Baselines before starting: `Rag.NET.Evaluation.Tests` **151**, `Rag.NET.Tests` **1313**.

---

## Part A: move the shared core down

The riskiest part, because it touches code merged hours ago. It goes first so everything after builds on one implementation rather than two.

### Task A1: `EvaluationCallOptions`

**Files:**
- Create: `src/Rag.NET.Evaluation/EvaluationCallOptions.cs`
- Modify: `src/Rag.NET.Evaluation.Ragas/RagasOptions.cs`

Extract the shared tuning into a base class and have `RagasOptions` extend it. Move `MaxConcurrentCalls`, `PricePerInputToken`, `PricePerOutputToken`, `PricePerEmbeddingToken` down; leave `SyntheticQuestionCount` on `RagasOptions`.

Property names must not change — RAGAS callers and the guide's snippets depend on them, and this must stay source-compatible. Carry the XML docs down verbatim, including the "set it from your own price sheet; the ledger never prices anything itself" framing.

Verify: build 0/0, and `Rag.NET.Evaluation.Tests` still 151 with no test edits. If any RAGAS test needed changing, the extraction was not source-compatible — stop and reconsider.

**Commit:** `refactor(evaluation): share the LLM call options between RAGAS and the builder`

### Task A2: `EvaluationChatCaller`

**Files:**
- Create: `src/Rag.NET.Evaluation/Internal/EvaluationChatCaller.cs`
- Modify: `src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj` (add `InternalsVisibleTo` for `Rag.NET.Evaluation.Ragas` and `Rag.NET.Evaluation.Tests`)
- Modify: `src/Rag.NET.Evaluation.Ragas/Judging/RagasJudge.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/EvaluationChatCallerTests.cs`

Move out of `RagasJudge`, unchanged in behaviour:
- the `SemaphoreSlim` ceiling and its `MaxConcurrentCalls <= 0` guard,
- `CompleteAsync` (gate → chat call → record cost → return trimmed text),
- `RecordCostAsync`, **including the guard that requires both token counters to be provider-reported**. That guard was the whole-phase review finding in 3.1; do not simplify it back to a null check on the `Usage` wrapper.

`RagasJudge` keeps `ClassifyAsync`, `ClassifyManyAsync`, `ExtractListAsync`, `ParseVerdict`, `StripCodeFence` and composes the caller.

**This is a pure refactor. The existing `RagasJudgeTests` must pass unchanged.** If one needs editing, behaviour moved when it should not have — stop and find out why. Port the cost tests to `EvaluationChatCallerTests` (they now belong there) and leave the judge's parsing tests where they are.

`InternalsVisibleTo` uses the `<AssemblyAttribute Include="...InternalsVisibleToAttribute"><_Parameter1>` form — that is what all ~35 other projects in this repo use. Copy the shape from `src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj`.

**Verification that matters:** after this task, temporarily set `MaxConcurrentCalls = 1` and confirm the RAGAS ceiling test still passes, then restore. The shared caller must still be *one instance per suite run* — if the refactor accidentally gives each metric its own, the ceiling multiplies again and 3.1's `EvaluateAsync_ConcurrencyCeilingIsSharedAcrossMetricsNotPerMetric` should catch it. Confirm that test still passes and say so.

**Commit:** `refactor(evaluation): move throttling and cost recording into a shared caller`

---

## Part B: reproducible, bounded sampling

### Task B1: seeded reservoir sampling

**Files:**
- Create: `src/Rag.NET.Evaluation/Internal/ReservoirSampler.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/ReservoirSamplerTests.cs`

A pure function first, for the same reason Part A of 3.1 came first: it is testable exhaustively with no LLM and no data manager.

Algorithm R. Keep the first `k` items; for item `i >= k`, pick `j = rng.Next(i + 1)` and replace slot `j` if `j < k`.

**Step 1: write the failing tests.**

```csharp
[Fact]
public void Sample_SameSeed_SelectsTheSameItems()
{
    var source = Enumerable.Range(0, 100).Select(i => $"item {i}").ToList();

    var first  = ReservoirSampler.Sample(source, 10, new Random(1234));
    var second = ReservoirSampler.Sample(source, 10, new Random(1234));

    // The guarantee the phase exists to add: a dataset can be regenerated.
    Assert.Equal(first, second);
}

[Fact]
public void Sample_DifferentSeeds_GenerallySelectDifferentItems()
{
    var source = Enumerable.Range(0, 100).Select(i => $"item {i}").ToList();

    var a = ReservoirSampler.Sample(source, 10, new Random(1));
    var b = ReservoirSampler.Sample(source, 10, new Random(2));

    Assert.NotEqual(a, b);
}

[Theory]
[InlineData(0, 0)]
[InlineData(5, 5)]
[InlineData(100, 10)]     // k larger than the source clamps to what exists
public void Sample_ReturnsAtMostWhatExists(int k, int expected)
    => Assert.Equal(expected, ReservoirSampler.Sample(Enumerable.Range(0, 10).Select(i => $"{i}").ToList(), k, new Random(1)).Count);

[Fact]
public void Sample_SelectsEveryItemWithRoughlyEqualFrequency()
{
    // Uniformity is the property that makes the dataset representative. Loose bounds:
    // this is a sanity check against a badly skewed reservoir, not a statistical proof.
    var source = Enumerable.Range(0, 10).Select(i => i).ToList();
    var counts = new int[10];
    for (var seed = 0; seed < 2000; seed++)
        foreach (var picked in ReservoirSampler.Sample(source, 3, new Random(seed)))
            counts[picked]++;

    Assert.All(counts, c => Assert.InRange(c, 400, 800));   // expected 600 each
}
```

**Step 3: implement.** Signature takes `IReadOnlyList<T>` for now; Task B2 introduces the streaming overload it actually needs. Keep it allocation-lean (no LINQ in the loop — ZA0601).

**Commit:** `feat(evaluation): seeded reservoir sampling`

### Task B2: stream the corpus instead of materialising it

**Files:**
- Modify: `src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs`
- Modify: `src/Rag.NET.Evaluation/EvaluationDatasetBuilderOptions.cs` (add `Seed`, extend `EvaluationCallOptions`)
- Test: `tests/Rag.NET.Tests/Evaluation/EvaluationDatasetBuilderTests.cs`

Replace `:36-46`. Today it accumulates every chunk of every document into one list to pick `SampleCount`. Feed each document's chunks through the reservoir as they arrive and keep only the reservoir.

Add an `async` overload of `Sample` taking the per-document enumeration, or drive the reservoir from the builder's loop — your call, but the corpus must not be accumulated.

**The test that pins it:** count `GetChunksAsync` calls and assert the builder holds no more than `SampleCount` chunks. Assert against the reservoir's size, not against a memory measurement.

Document `Seed`'s XML with the two honest limits from design §4: it fixes which chunks are *sampled*, not the corpus (ingestion changes what is there) and not the generated text (the model is not seeded).

**Commit:** `perf(evaluation): sample the corpus by streaming rather than materialising it`

---

## Part C: a failed generation is not a sample

### Task C1: `EvaluationDataset` result

**Files:**
- Create: `src/Rag.NET.Evaluation/EvaluationDataset.cs`
- Modify: `src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs`

`BuildAsync` returns `EvaluationDataset` instead of `IReadOnlyList<EvaluationSample>`:

```csharp
/// <summary>Samples that generated successfully.</summary>
public IReadOnlyList<EvaluationSample> Samples { get; init; } = [];

/// <summary>How many chunks were sampled and sent for generation.</summary>
public int Requested { get; init; }

/// <summary>
/// How many sampled chunks produced no usable sample, by reason.
/// </summary>
/// <remarks>
/// Surfaced rather than folded into a shorter list, for the same reason
/// <c>RagasReport.UnscoreableSamples</c> exists: a caller who asks for 50 and receives 47
/// should be able to see why, instead of quietly evaluating against a smaller set.
/// </remarks>
public IReadOnlyDictionary<string, int> Skipped { get; init; } =
    new Dictionary<string, int>(StringComparer.Ordinal);
```

This is a breaking API change. Nothing is published (NuGet packaging is Phase 4.1), so per the posture already set in this repo — source-breaking is acceptable while not public — take it cleanly and document it in the guide. Do **not** create a breaking-changes log; that was deliberately removed.

**Commit:** `feat(evaluation): report which sampled chunks produced no usable sample`

### Task C2: exclude empty generations

**Files:**
- Modify: `src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs`
- Modify: `tests/Rag.NET.Tests/Evaluation/EvaluationDatasetBuilderTests.cs`

An empty or whitespace-only question drops the sample. In `QuestionAndAnswer` mode an empty reference answer drops it too — emitting it produces a sample that Context Precision and Context Recall reject at evaluation time, which moves the error somewhere it cannot be explained.

**Re-point the existing test.** `BuildAsync_WhenLlmReturnsEmptyText_HandlesGracefully` currently asserts the opposite of the new behaviour:

```csharp
Assert.Single(samples);
Assert.Equal(string.Empty, samples[0].Question);
```

Rename it to `BuildAsync_WhenTheModelReturnsNothing_DropsTheSampleAndCountsIt`, assert `Assert.Empty(dataset.Samples)` and that `Skipped` records one, and add a comment recording what it used to claim — the same treatment 3.1 gave its two siblings. Do not delete it.

Add: a partial failure (one chunk generates, one comes back empty) yields one sample and one skip.

**Commit:** `fix(evaluation): drop generations the model returned nothing for`

### Task C3: bounded concurrency and cost

**Files:**
- Modify: `src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs`
- Test: `tests/Rag.NET.Tests/Evaluation/EvaluationDatasetBuilderTests.cs`

Route both generation calls through `EvaluationChatCaller`, which supplies the ceiling and the cost recording for free.

**The ceiling test must assert peak observed concurrency**, not a total call count — a total proves nothing about whether a ceiling held. `tests/Rag.NET.Evaluation.Tests/Ragas/RoutingChatClient.cs` already does exactly this, with per-call latches and `ReleaseInReverseAsync()`; reuse it rather than writing a second one. Note it currently lives in the `Ragas` test folder of a different test project — moving or linking it is part of this task.

**Verify by mutation:** remove the ceiling, confirm the test goes red, restore. Remember that a timestamp-preserving `Copy-Item` restore makes MSBuild skip recompiling, so touch the file or rebuild fully.

**Commit:** `fix(evaluation): bound dataset generation concurrency and record its cost`

---

## Part D: documentation

### Task D1

**Files:**
- Modify: `docs/guide/evaluation.md`
- Modify: `docs/reference/features.md`

The builder's guide section must cover: `Seed` and **precisely** what it does and does not guarantee (design §4 — same seed and same corpus select the same chunks; the corpus and the model both still vary); the `EvaluationDataset` return type and how to read `Skipped`; `MaxConcurrentCalls`; cost recording and that prices default to zero.

Carry over the `UseCostBudgeting` double-counting warning from the RAGAS section — the builder has the same exposure, since a DI-resolved `IChatClient` is decorated and records to the ledger itself.

State plainly that **datasets built before this phase are not reproducible and may contain empty-question samples**, so anyone holding one should rebuild rather than trust it.

`features.md`: tick the summary-matrix row (search for `Evaluation Dataset Builder`; **verify the line number yourself**) and correct the Status prose.

Every code sample must compile against the real API — check member names and signatures against source, and say how you verified.

**Commit:** `docs(evaluation): document dataset reproducibility and its limits`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. `dotnet test tests/Rag.NET.Evaluation.Tests` — report the count (151 at phase start).
3. `dotnet test tests/Rag.NET.Tests` — report the count (1313 at phase start).
4. `dotnet test tests/Rag.NET.Api.Tests` (63) and `tests/Rag.NET.DataProviders.Tests` (69) as regression checks.
5. Confirm no `#pragma`/`SuppressMessage` added anywhere in the diff.
6. Confirm the RAGAS shared-ceiling test still passes after Part A's refactor.
7. `docs/planning/ROADMAP.md` and `docs/planning/MILESTONE.md` flip to complete **after** the whole-phase review, not before — and both files, per the `73472b4` precedent.
