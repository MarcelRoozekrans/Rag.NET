# A/B Testing Framework Implementation Plan (Phase 3.3)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `RagAbTester` that runs an evaluation dataset through two pipeline configurations and reports which one is better — with enough statistical honesty that "better" survives scrutiny.

**Architecture:** Three layers, split so the subtlest part is testable without a pipeline. `AbStatistics` is pure arithmetic over `double[]`. `AbRunner` executes variants in alternating order and times them. `RagAbTester` composes those with the existing RAGAS suite.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI`, xUnit v3, NSubstitute (legal on `IRagPipeline` — its members return `Task`, so EPS06 does not bite).

**Design:** `docs/plans/2026-07-28-ab-testing-design.md`. Read it first, especially §4, §5 and §6.

---

## Conventions that will fail the build if ignored

- **Warnings are errors.** MA0051 (methods ≤ 60 lines), MA0015 (`paramName`), MA0048 (file name matches type name), ZA0601/ZA0501 (no LINQ/boxing in hot loops), EPS05/EPS06, **HLQ012 (no `foreach` over `List<T>`)**, HLQ013 (`foreach` not `for` over arrays).
- **No new `#pragma` or `SuppressMessage`.** Neither evaluation project has any; keep it that way.
- xUnit v3: always `TestContext.Current.CancellationToken`.
- **No sleeps in tests.** Signal with `TaskCompletionSource` and bounded `WaitAsync`.
- **Commits:** conventional, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — explicit paths. `.lucent/*` and `.claude/worktrees/*` are expected dirty; leave them.

Verify after every task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

Baselines: `Rag.NET.Evaluation.Tests` **180**, `Rag.NET.Tests` **1308**, `Rag.NET.Api.Tests` **63**, `Rag.NET.DataProviders.Tests` **69**.

Everything new goes in `src/Rag.NET.Evaluation/` (the package `features.md` names). `Rag.NET.Evaluation` already grants `InternalsVisibleTo` to `Rag.NET.Evaluation.Tests`, so internal types are directly testable — check the csproj rather than assuming.

---

## Part A: the statistics, with no pipeline in sight

This is where a wrong answer would be least visible, so it goes first and is pinned by table tests.

### Task A1: paired deltas, mean, and win/loss/tie

**Files:**
- Create: `src/Rag.NET.Evaluation/Internal/AbStatistics.cs`
- Create: `src/Rag.NET.Evaluation/AbComparison.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/AbStatisticsTests.cs`

**Step 1: write the failing tests.**

```csharp
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Internal;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public sealed class AbStatisticsTests
{
    [Fact]
    public void PairedDeltas_SkipsAPairWhereEitherSideIsNull()
    {
        double?[] a = [0.5, null, 0.7, 0.2];
        double?[] b = [0.6, 0.9, null, 0.4];

        var deltas = AbStatistics.PairedDeltas(a, b);

        // Only indices 0 and 3 have both sides. A pair is all-or-nothing: keeping the readable
        // half would average the two variants over different sample sets while still calling
        // the result paired.
        Assert.Equal([0.1, 0.2], deltas, precision: 10);
    }

    [Fact]
    public void PairedDeltas_MismatchedLengths_Throws()
        => Assert.Throws<ArgumentException>(() => AbStatistics.PairedDeltas([0.1], [0.1, 0.2]));

    [Theory]
    [InlineData(new[] { 0.1, 0.2, 0.3 }, 0.2)]
    [InlineData(new[] { -0.1, 0.1 }, 0.0)]
    [InlineData(new[] { 0.5 }, 0.5)]
    public void MeanDelta_IsTheArithmeticMean(double[] deltas, double expected)
        => Assert.Equal(expected, AbStatistics.MeanDelta(deltas), precision: 10);

    [Fact]
    public void MeanDelta_NoPairs_IsNull()
        => Assert.Null(AbStatistics.MeanDelta([]));

    [Theory]
    // deltas                              B wins, A wins, ties
    [InlineData(new[] { 0.1, 0.2, -0.3 },       2,      1,     0)]
    [InlineData(new[] { 0.0, 0.0 },             0,      0,     2)]
    [InlineData(new[] { 1e-12, -1e-12 },        0,      0,     2)]  // inside epsilon: a tie
    public void Tally_CountsWinsLossesAndTies(double[] deltas, int bWins, int aWins, int ties)
    {
        var tally = AbStatistics.Tally(deltas, epsilon: 1e-9);

        Assert.Equal(bWins, tally.BWins);
        Assert.Equal(aWins, tally.AWins);
        Assert.Equal(ties, tally.Ties);
    }
}
```

**Step 3: implement.**

`MeanDelta` returns `double?` — `null` for "no comparable pairs", never `0.0`. Phase 3.1 established that a fabricated zero is indistinguishable from a real one, and this is the same trap: a mean delta of `0.0` means *the variants tied*, which is a finding, while `null` means *nothing could be compared*, which is not.

```csharp
namespace Rag.NET.Evaluation.Internal;

/// <summary>
/// The paired-comparison arithmetic behind <see cref="RagAbTester"/>, as pure functions.
/// </summary>
/// <remarks>
/// Separated from execution so the statistics can be pinned by table tests without a pipeline or a
/// model. This is the part of the phase where a wrong answer would be least visible: an A/B run
/// always produces a winner, and only the interval says whether the winner is real.
/// </remarks>
internal static class AbStatistics
{
    /// <summary>
    /// Per-sample <c>b - a</c> for every index where **both** sides scored.
    /// </summary>
    /// <remarks>
    /// A pair is all-or-nothing. Keeping the readable half of a pair would compute the two means
    /// over different sample sets while still describing the result as paired.
    /// </remarks>
    public static double[] PairedDeltas(IReadOnlyList<double?> a, IReadOnlyList<double?> b) { … }

    /// <summary>Mean of the paired deltas; <c>null</c> when there are none.</summary>
    /// <remarks>
    /// Nullable rather than <c>0.0</c>: a mean delta of zero says the variants tied, which is a
    /// result. <c>null</c> says nothing was comparable, which is not.
    /// </remarks>
    public static double? MeanDelta(ReadOnlySpan<double> deltas) { … }

    /// <summary>How often each variant won, within <paramref name="epsilon"/>.</summary>
    public static AbTally Tally(ReadOnlySpan<double> deltas, double epsilon) { … }
}
```

`AbTally` is a small public record in `AbComparison.cs` (`BWins`, `AWins`, `Ties`).

**Commit:** `feat(evaluation): paired-delta arithmetic for A/B comparison`

### Task A2: the bootstrap confidence interval

**Files:**
- Modify: `src/Rag.NET.Evaluation/Internal/AbStatistics.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/AbStatisticsTests.cs`

Percentile bootstrap: resample the deltas with replacement `resamples` times, take each resample's mean, sort, and read the 2.5th and 97.5th percentiles.

**The test that carries the phase** — the interval must actually discriminate:

```csharp
[Fact]
public void BootstrapCi_DistinguishesARealShiftFromNoise()
{
    // Noise: deltas centred on zero. The interval must span zero.
    var noise = new double[40];
    for (var i = 0; i < noise.Length; i++)
        noise[i] = (i % 2 == 0) ? 0.05 : -0.05;

    // A real shift: every sample moved the same way.
    var shift = new double[40];
    for (var i = 0; i < shift.Length; i++)
        shift[i] = (i % 2 == 0) ? 0.10 : 0.04;

    var noiseCi = AbStatistics.BootstrapMeanDeltaCi(noise, resamples: 2000, new Random(7))!.Value;
    var shiftCi = AbStatistics.BootstrapMeanDeltaCi(shift, resamples: 2000, new Random(7))!.Value;

    // This is the whole point of the interval: without it, both of these report a winner.
    Assert.True(noiseCi.Lower < 0 && noiseCi.Upper > 0, $"noise CI [{noiseCi.Lower}, {noiseCi.Upper}] should span zero");
    Assert.True(shiftCi.Lower > 0, $"shift CI [{shiftCi.Lower}, {shiftCi.Upper}] should exclude zero");
}

[Fact]
public void BootstrapCi_SameSeed_IsReproducible()
{
    double[] deltas = [0.1, -0.05, 0.2, 0.0, 0.15];

    var first  = AbStatistics.BootstrapMeanDeltaCi(deltas, 500, new Random(99));
    var second = AbStatistics.BootstrapMeanDeltaCi(deltas, 500, new Random(99));

    // An unreproducible confidence interval is not evidence. Same rule as the dataset seed.
    Assert.Equal(first!.Value.Lower, second!.Value.Lower, precision: 12);
    Assert.Equal(first.Value.Upper, second.Value.Upper, precision: 12);
}

[Fact]
public void BootstrapCi_MoreSamplesNarrowTheInterval()
{
    var few  = new double[10];
    var many = new double[200];
    for (var i = 0; i < few.Length; i++)  few[i]  = (i % 2 == 0) ? 0.10 : 0.02;
    for (var i = 0; i < many.Length; i++) many[i] = (i % 2 == 0) ? 0.10 : 0.02;

    var fewCi  = AbStatistics.BootstrapMeanDeltaCi(few, 2000, new Random(3))!.Value;
    var manyCi = AbStatistics.BootstrapMeanDeltaCi(many, 2000, new Random(3))!.Value;

    Assert.True(manyCi.Upper - manyCi.Lower < fewCi.Upper - fewCi.Lower);
}

[Fact]
public void BootstrapCi_NoPairs_IsNull()
    => Assert.Null(AbStatistics.BootstrapMeanDeltaCi([], 100, new Random(1)));

[Fact]
public void BootstrapCi_OnePair_IsDegenerateNotAnError()
{
    // Every resample of a single value is that value. The interval collapses to a point, which is
    // honest — one sample supports no interval — rather than an exception or a fabricated width.
    var ci = AbStatistics.BootstrapMeanDeltaCi([0.3], 100, new Random(1))!.Value;

    Assert.Equal(0.3, ci.Lower, precision: 10);
    Assert.Equal(0.3, ci.Upper, precision: 10);
}
```

**Verify the discrimination test is not vacuous:** make `BootstrapMeanDeltaCi` return a fixed wide interval such as `(-1, 1)` and confirm `BootstrapCi_DistinguishesARealShiftFromNoise` goes red on the shift case. Report what you saw. A CI test that passes whatever the implementation does is worse than none, because it looks like rigour.

`AbConfidenceInterval(double Lower, double Upper)` is a public record in `AbComparison.cs`. No LINQ in the resampling loop (ZA0601); allocate the resample-means buffer once.

**Commit:** `feat(evaluation): seeded bootstrap confidence interval on the mean delta`

---

## Part B: execution

### Task B1: `AbVariant` and `AbRunner`

**Files:**
- Create: `src/Rag.NET.Evaluation/AbVariant.cs`
- Create: `src/Rag.NET.Evaluation/Internal/AbRunner.cs`
- Create: `src/Rag.NET.Evaluation/Internal/VariantRun.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/AbRunnerTests.cs`

```csharp
/// <summary>One side of an A/B comparison.</summary>
/// <param name="Name">Label used in the report. Must be unique within a comparison.</param>
/// <param name="Pipeline">The configured pipeline. Differences here — chunking, vector store,
/// embedding model, reranker — are what this framework exists to compare.</param>
/// <param name="Options">Per-call settings for this variant (TopK, prompt, temperature).</param>
/// <param name="CostLedger">
/// Optional ledger **dedicated to this variant's pipeline**. See the remarks: the tester reads
/// spend before and after the run, so any other traffic writing to the same ledger pollutes the
/// figure. Omit it and cost is reported as absent rather than as zero.
/// </param>
public sealed record AbVariant(
    string Name,
    IRagPipeline Pipeline,
    RagOptions? Options = null,
    ICostLedger? CostLedger = null);
```

**The cost mechanism needs stating plainly, because the design left it implicit.** The tester receives pipelines that are already built, so it cannot instrument their spend. It can only read a ledger the caller wired into that pipeline itself, by snapshotting `GetSpendAsync(CostWindow.Day)` before and after the run and reporting the difference. That requires a ledger dedicated to the variant; a shared one measures everything else on that day too. Document this on `CostLedger` — an unexplained cost column that silently includes unrelated traffic is exactly the sort of plausible number this milestone exists to remove.

**Step 1: the tests that matter.**

```csharp
[Fact]
public async Task RunAsync_AlternatesWhichVariantLeads()
{
    var order = new List<string>();
    var a = RecordingPipeline("A", order);
    var b = RecordingPipeline("B", order);

    await new AbRunner().RunAsync(
        [Variant("A", a), Variant("B", b)],
        Samples("q1", "q2", "q3", "q4"),
        TestContext.Current.CancellationToken);

    // Whichever runs second benefits from provider prompt caching and a warm store. A fixed
    // order hands one variant that advantage on every sample and reports it as a result.
    Assert.Equal(["A", "B", "B", "A", "A", "B", "B", "A"], order);
}

[Fact]
public async Task RunAsync_WhenOneVariantThrows_ExcludesThePairAndContinues()
{
    var a = SucceedingPipeline();
    var b = ThrowingOnSecondQuestionPipeline();

    var runs = await new AbRunner().RunAsync(
        [Variant("A", a), Variant("B", b)],
        Samples("q1", "q2", "q3"),
        TestContext.Current.CancellationToken);

    // The run continues — one bad sample must not lose the other 99.
    Assert.Equal(3, runs.Count);
    // But the failed sample is not comparable, on either side.
    Assert.False(runs[1].IsComparable);
    Assert.NotNull(runs[1].Failure);
    Assert.True(runs[0].IsComparable);
    Assert.True(runs[2].IsComparable);
}

[Fact]
public async Task RunAsync_RecordsPerVariantLatency()
{
    var runs = await new AbRunner().RunAsync(
        [Variant("A", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
        Samples("q1"),
        TestContext.Current.CancellationToken);

    Assert.All(runs[0].Elapsed.Values, e => Assert.True(e >= TimeSpan.Zero));
    Assert.Equal(2, runs[0].Elapsed.Count);
}

[Fact]
public async Task RunAsync_PropagatesCancellation()
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    await cts.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AbRunner().RunAsync(
        [Variant("A", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
        Samples("q1"),
        cts.Token));
}
```

`Assert.Equal(["A","B","B","A",…])` is the alternation pin. **Verify it bites:** change the runner to always lead with the first variant and confirm this test fails. A runner that ignores alternation while the test still passes is the failure mode here.

Use NSubstitute for `IRagPipeline` where a canned response suffices; hand-write the recording fake, since it must capture call *order* across two instances.

`VariantRun` carries: the sample, `IReadOnlyDictionary<string, RagResponse>` by variant name, `IReadOnlyDictionary<string, TimeSpan>` elapsed, `IsComparable`, and an optional `Failure` (variant name + exception message). A run is comparable only when **every** variant answered.

**Commit:** `feat(evaluation): alternating-order A/B runner with per-variant timing`

---

## Part C: the tester and its report

### Task C1: `RagAbTester` and `AbReport`

**Files:**
- Create: `src/Rag.NET.Evaluation/RagAbTester.cs`
- Create: `src/Rag.NET.Evaluation/AbReport.cs`
- Create: `src/Rag.NET.Evaluation/AbOptions.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/RagAbTesterTests.cs`

Flow:
1. `AbRunner` executes the dataset, alternating, timing each variant.
2. For each variant, project the runs into `EvaluationSample`s:
   `new EvaluationSample(sample.Question, response.Answer, sample.ReferenceAnswer, response.Sources.Select(s => s.Chunk.Text).ToList())`.
   Note `RagResponse` exposes **`Sources`** (`IReadOnlyList<SearchResult>`), not `SourceChunks` — a stale guide snippet used the wrong name for months, so check the member rather than trusting prose.
3. Score each variant with the caller-supplied `RagasEvaluationSuite` (or `IRagEvaluator`). `RagasReport.Samples` is documented as being in input order, so index *i* in one variant's report pairs with index *i* in the other.
4. For each metric, pair the per-sample scores, drop pairs where either side is `null` **or** the run was not comparable, and feed the deltas to `AbStatistics`.
5. Build `AbReport`.

`AbOptions` carries `Seed` (for the bootstrap), `BootstrapResamples` (default 2000), and `TieEpsilon` (default 1e-9). Document `Seed` the way `EvaluationDatasetBuilderOptions.Seed` is documented: what it fixes and what it does not — it makes the *interval* reproducible given the same deltas; it does not make the pipelines or the judge deterministic.

`AbReport` per metric: each variant's mean, the mean delta, the tally, the CI, and **how many pairs were dropped and why** (run failure vs unscoreable). Plus per-variant latency p50/p95 and the latency delta with its own CI, and cost when ledgers were supplied.

**Tests:**
- Two variants where B is uniformly better → positive mean delta, CI excludes zero, B wins the tally.
- Identical variants → mean delta ~0, CI spans zero. **This is the test that stops the framework manufacturing winners.**
- A metric returning `null` for one variant on one sample → that pair dropped, counted, and the others unaffected.
- A variant throwing on one sample → that sample dropped from *every* metric, counted separately from unscoreable.
- No comparable pairs at all → means and CI are `null`, not `0.0`, and the report says how many were dropped.

**Commit:** `feat(evaluation): RagAbTester with paired reporting`

---

## Part D: documentation

### Task D1

**Files:**
- Modify: `docs/guide/evaluation.md`
- Modify: `docs/reference/features.md`

New A/B section covering: what a variant is and why it is a whole pipeline; why execution alternates (whichever runs second gets a warm cache, so a fixed order is a thumb on the scale); how to read the tally and the CI; the drop rules from §5.

**State plainly that a CI spanning zero is not a win.** That is the sentence the section exists for. An A/B run always produces a higher number on one side; the interval is the only thing separating a result from noise.

Also state that **shadow mode is not in this phase**, and name Phase 3.8 — `features.md` promises it, and a row ticked without that note would leave the promise half-met with no explanation.

Cost: explain the dedicated-ledger requirement and that omitting a ledger reports cost as absent, not zero.

`features.md`: tick the `A/B Testing Framework` matrix row (**verify the line number yourself**) and rewrite the Status prose to say what shipped — offline harness with paired statistics — rather than what was originally specified.

Every code sample must compile against the real API. Verify by pasting the snippets into a throwaway project with `ProjectReference`s and building, with a negative control (rename a member, confirm the compiler objects) to prove the file was really compiled. Say how you verified.

**Commit:** `docs(evaluation): document the A/B harness and how to read its interval`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. `dotnet test tests/Rag.NET.Evaluation.Tests` — report the count (180 at phase start).
3. `dotnet test tests/Rag.NET.Tests` (1308), `tests/Rag.NET.Api.Tests` (63), `tests/Rag.NET.DataProviders.Tests` (69).
4. The two mutation checks: the alternation pin (Task B1) and the CI discrimination pin (Task A2).
5. No `#pragma`/`SuppressMessage` added anywhere in the diff.
6. `docs/planning/ROADMAP.md` and `docs/planning/MILESTONE.md` flip to complete **after** the whole-phase review, not before — and both files, per the `73472b4` precedent.
