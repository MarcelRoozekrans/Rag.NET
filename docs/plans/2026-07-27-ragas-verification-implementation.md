# RAGAS Verification Implementation Plan (Phase 3.1)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Turn `Rag.NET.Evaluation.Ragas` from shipped-but-unverified code into four metrics that match the published RAGAS definitions, are pinned by tests, and are documented.

**Architecture:** Split the LLM plumbing from the arithmetic. An internal `RagasJudge` owns prompting, parsing, throttling and cost; the evaluators become arithmetic over judgement arrays. The formulas then become testable with no LLM at all, which is where metric fidelity is actually pinned.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`), xUnit v3, `System.Numerics.Tensors`.

**Design:** `docs/plans/2026-07-27-ragas-verification-design.md`. Read it before starting.

---

## Conventions that will fail the build if ignored

- **Warnings are errors.** MA0051 (methods ≤ 60 lines), MA0015 (`paramName` on argument exceptions), ZA0601/ZA0501 (no LINQ or boxing in hot loops), EPS05/EPS06 (ValueTask hidden copies).
- **EPS06 forbids NSubstitute on `ValueTask` members.** `IChatClient.GetResponseAsync` returns `Task`, so NSubstitute is legal for it — the existing `LlmJudgeEvaluatorTests` uses it. `IEmbeddingGenerator.GenerateAsync` also returns `Task`. Hand-written fakes are still required wherever a fake must *route on prompt content*, which NSubstitute does poorly.
- **No new `#pragma` or `SuppressMessage`.** The repo has exactly two justified pragmas and the standing rule is not to add a third.
- **xUnit v3:** always `TestContext.Current.CancellationToken`, never `CancellationToken.None`, in tests.
- **No sleeps in tests.** Use `TaskCompletionSource` and bounded `WaitAsync`.
- **Commits:** conventional, ending with the trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — stage explicit paths. `.lucent/*` and `.claude/worktrees/*` are expected to be dirty; leave them alone.

Verify after every task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

---

## Part A: the arithmetic, with no LLM in sight

This is the part that is definitionally wrong today, and it is pure functions, so it is tested exhaustively without a single mock.

### Task A1: `RagasMath.AveragePrecision`

**Files:**
- Create: `src/Rag.NET.Evaluation.Ragas/RagasMath.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/Ragas/RagasMathTests.cs`

**Step 1: Write the failing tests.**

The whole point of this task is that rank matters. These two cases have identical relevant/total ratios and must produce different scores — that assertion alone is the fix.

```csharp
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class RagasMathTests
{
    [Fact]
    public void AveragePrecision_GoldChunkFirst_ScoresHigherThanGoldChunkLast()
    {
        var first = RagasMath.AveragePrecision([true, false, false]);
        var last  = RagasMath.AveragePrecision([false, false, true]);

        // The defect this replaces returned 1/3 for both.
        Assert.True(first > last, $"rank-blind: first={first}, last={last}");
        Assert.Equal(1.0, first, precision: 10);
        Assert.Equal(1.0 / 3.0, last, precision: 10);
    }

    [Theory]
    // relevance by rank                     expected average precision
    [InlineData(new[] { true },                            1.0)]
    [InlineData(new[] { false },                           0.0)]
    [InlineData(new[] { true, true },                      1.0)]
    [InlineData(new[] { false, false },                    0.0)]
    // P@1=1/1, P@3=2/3 -> (1 + 0.666..) / 2
    [InlineData(new[] { true, false, true },               0.8333333333333333)]
    // P@2=1/2, P@3=2/3 -> (0.5 + 0.666..) / 2
    [InlineData(new[] { false, true, true },               0.5833333333333333)]
    public void AveragePrecision_MatchesTheRagasDefinition(bool[] relevance, double expected)
        => Assert.Equal(expected, RagasMath.AveragePrecision(relevance), precision: 10);

    [Fact]
    public void AveragePrecision_NoRelevantChunks_IsZeroNotDivideByZero()
        => Assert.Equal(0.0, RagasMath.AveragePrecision([false, false]), precision: 10);

    [Fact]
    public void AveragePrecision_EmptyInput_IsZero()
        => Assert.Equal(0.0, RagasMath.AveragePrecision([]), precision: 10);

    [Theory]
    [InlineData(3, 4, 0.75)]
    [InlineData(0, 4, 0.0)]
    [InlineData(4, 4, 1.0)]
    public void SupportedFraction_IsSupportedOverTotal(int supported, int total, double expected)
        => Assert.Equal(expected, RagasMath.SupportedFraction(supported, total), precision: 10);

    [Fact]
    public void SupportedFraction_ZeroTotal_ThrowsRatherThanReturningOne()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RagasMath.SupportedFraction(0, 0));

    [Theory]
    [InlineData(-0.4, 0.0)]   // cosine is [-1,1]; the score contract is [0,1]
    [InlineData(0.0, 0.0)]
    [InlineData(0.55, 0.55)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0000001, 1.0)]
    public void ClampScore_ConstrainsToTheDocumentedRange(double raw, double expected)
        => Assert.Equal(expected, RagasMath.ClampScore(raw), precision: 10);
}
```

**Step 2: Run to verify they fail.**

`dotnet test tests/Rag.NET.Evaluation.Tests --filter "FullyQualifiedName~RagasMathTests"`
Expected: compile failure — `RagasMath` does not exist.

**Step 3: Implement.**

`SupportedFraction` throws on zero rather than returning 1.0. That is deliberate: "no items" must be decided by the *caller*, who knows whether it means "nothing was asserted" or "parsing failed". Burying it here is how the original defect happened.

```csharp
namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// The RAGAS score formulas as pure functions over judgement arrays.
/// </summary>
/// <remarks>
/// Separated from the evaluators so metric fidelity can be pinned by table tests with no LLM
/// involved. The arithmetic is the part that was definitionally wrong before Phase 3.1; keeping
/// it callable without mock choreography is what makes that verifiable.
/// </remarks>
internal static class RagasMath
{
    /// <summary>
    /// Rank-aware average precision: <c>Σ(P@k × rel_k) / total_relevant</c>.
    /// </summary>
    /// <remarks>
    /// Rank-aware on purpose. Plain <c>relevant / total</c> scores a retriever that returns the
    /// gold chunk first identically to one that returns it last, which is exactly the
    /// discrimination this metric exists to provide.
    /// </remarks>
    /// <param name="relevanceByRank">Relevance judgements in retrieved order.</param>
    public static double AveragePrecision(ReadOnlySpan<bool> relevanceByRank)
    {
        var relevantSoFar = 0;
        var precisionSum = 0.0;

        for (var i = 0; i < relevanceByRank.Length; i++)
        {
            if (!relevanceByRank[i])
                continue;

            relevantSoFar++;
            precisionSum += relevantSoFar / (double)(i + 1);
        }

        return relevantSoFar == 0 ? 0.0 : precisionSum / relevantSoFar;
    }

    /// <summary>Fraction of items judged supported.</summary>
    /// <param name="supported">Items judged supported.</param>
    /// <param name="total">Items judged at all.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="total"/> is zero. The caller decides what "nothing to judge" means —
    /// returning 1.0 here is the defect this phase removes.
    /// </exception>
    public static double SupportedFraction(int supported, int total)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(total, 0, nameof(total));
        return supported / (double)total;
    }

    /// <summary>Clamps a raw similarity to the documented [0, 1] score range.</summary>
    public static double ClampScore(double raw) => Math.Clamp(raw, 0.0, 1.0);
}
```

**Step 4: Run tests.** Expected: all pass.

**Step 5: Commit.**

```bash
git add src/Rag.NET.Evaluation.Ragas/RagasMath.cs tests/Rag.NET.Evaluation.Tests/Ragas/RagasMathTests.cs
git commit -m "feat(evaluation): rank-aware RAGAS arithmetic as pure functions"
```

---

## Part B: the judge

### Task B1: tri-state verdict and the judge contract

**Files:**
- Create: `src/Rag.NET.Evaluation.Ragas/Judging/Verdict.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/Judging/ExtractionResult.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/RagasOptions.cs`

**Step 1: Write the types.**

```csharp
namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>The outcome of a single yes/no judgement.</summary>
/// <remarks>
/// Tri-state rather than <c>bool</c> because a verdict the model did not give is not a verdict.
/// Collapsing "unparseable" into "no" biases every score downward silently; collapsing it into
/// "yes" biases upward. Both fabricate. This makes the third case the caller's problem, which is
/// the only place it can be handled honestly.
/// </remarks>
internal enum Verdict
{
    /// <summary>The model affirmed.</summary>
    Yes,

    /// <summary>The model denied.</summary>
    No,

    /// <summary>The model's reply could not be read as either.</summary>
    Unparseable,
}
```

```csharp
namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>The outcome of asking the model for a JSON array of strings.</summary>
/// <param name="Items">The parsed items. Empty is legitimate — an answer can assert nothing.</param>
/// <param name="Parsed">
/// Whether the reply parsed at all. This is the distinction the pre-3.1 code lacked: it caught
/// <c>JsonException</c>, returned an empty list, and the caller scored the empty list as 1.0. A
/// malformed reply therefore produced the best possible score.
/// </param>
internal readonly record struct ExtractionResult(IReadOnlyList<string> Items, bool Parsed)
{
    public static ExtractionResult Failed() => new([], Parsed: false);

    public static ExtractionResult Success(IReadOnlyList<string> items) => new(items, Parsed: true);
}
```

```csharp
namespace Rag.NET.Evaluation.Ragas;

/// <summary>Tuning for a RAGAS evaluation run.</summary>
public sealed class RagasOptions
{
    /// <summary>
    /// Maximum LLM calls in flight across the whole run. Defaults to <c>4</c>.
    /// </summary>
    /// <remarks>
    /// Shared across every metric in a suite, not per metric. Per-metric ceilings multiply: four
    /// registered metrics each fanning out over a 50-chunk sample is 200 concurrent requests,
    /// which is how the pre-3.1 code behaved with no ceiling at all.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 4;

    /// <summary>
    /// Number of synthetic questions Answer Relevance generates. Defaults to <c>3</c>.
    /// </summary>
    public int SyntheticQuestionCount { get; set; } = 3;

    /// <summary>
    /// Price of one input token, in whatever currency the ledger is denominated in.
    /// Defaults to <c>0</c> — set it from your own price sheet, or cost entries record zero.
    /// </summary>
    /// <remarks>The ledger never prices anything itself; the caller computes the cost.</remarks>
    public decimal PricePerInputToken { get; set; }

    /// <summary>Price of one output token. Defaults to <c>0</c>. See <see cref="PricePerInputToken"/>.</summary>
    public decimal PricePerOutputToken { get; set; }
}
```

**Step 2: Build.** `dotnet build Rag.NET.slnx` → 0/0.

**Step 3: Commit.**

```bash
git add src/Rag.NET.Evaluation.Ragas/Judging src/Rag.NET.Evaluation.Ragas/RagasOptions.cs
git commit -m "feat(evaluation): tri-state verdict and RAGAS run options"
```

### Task B2: the prompt-routing fake

Build the test double before the thing it doubles — the judge's whole job is sequenced, prompt-dependent calls, and NSubstitute with one canned reply cannot express that.

**Files:**
- Create: `tests/Rag.NET.Evaluation.Tests/Ragas/RoutingChatClient.cs`

**Step 1: Implement the fake.**

It must record **peak observed concurrency**, not just a call count — a total proves nothing about a ceiling.

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>
/// An <see cref="IChatClient"/> that answers based on what the prompt contains, and records how
/// the calls actually interleaved.
/// </summary>
/// <remarks>
/// Hand-written rather than NSubstitute because RAGAS evaluators make sequenced,
/// prompt-dependent calls — extract a list, then judge each item — and a single canned reply
/// cannot express that. Peak concurrency is tracked because asserting a total call count proves
/// nothing about whether a ceiling held.
/// </remarks>
internal sealed class RoutingChatClient : IChatClient
{
    private readonly IReadOnlyList<(string Contains, string Reply)> _routes;
    private readonly string _fallback;
    private readonly Lock _gate = new();
    private readonly List<string> _prompts = [];
    private TaskCompletionSource? _release;
    private int _inFlight;
    private int _peakInFlight;

    public RoutingChatClient(
        IReadOnlyList<(string Contains, string Reply)> routes,
        string fallback = "no")
    {
        _routes = routes;
        _fallback = fallback;
    }

    /// <summary>Every prompt seen, in the order the calls started.</summary>
    public IReadOnlyList<string> Prompts { get { lock (_gate) { return [.. _prompts]; } } }

    /// <summary>The largest number of calls that were ever simultaneously in flight.</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    public int CallCount { get { lock (_gate) { return _prompts.Count; } } }

    /// <summary>Usage reported on each response. Null means the model reported none.</summary>
    public UsageDetails? Usage { get; set; }

    /// <summary>
    /// Blocks every call until <see cref="ReleaseAll"/> is called, so a test can observe how many
    /// the judge let start at once.
    /// </summary>
    public void GateCalls() => _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseAll() => _release?.TrySetResult();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = string.Join("\n", messages.Select(m => m.Text));

        lock (_gate)
        {
            _prompts.Add(text);
        }

        var now = Interlocked.Increment(ref _inFlight);
        UpdatePeak(now);

        try
        {
            if (_release is not null)
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var reply = _fallback;
            foreach (var (contains, candidate) in _routes)
            {
                if (text.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    reply = candidate;
                    break;
                }
            }

            return new ChatResponse([new ChatMessage(ChatRole.Assistant, reply)])
            {
                Usage = Usage,
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdatePeak(int observed)
    {
        var peak = Volatile.Read(ref _peakInFlight);
        while (observed > peak)
        {
            var prior = Interlocked.CompareExchange(ref _peakInFlight, observed, peak);
            if (prior == peak)
                return;

            peak = prior;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("RAGAS does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
```

**Step 2: Build.** 0/0.

**Step 3: Commit.**

```bash
git add tests/Rag.NET.Evaluation.Tests/Ragas/RoutingChatClient.cs
git commit -m "test(evaluation): prompt-routing chat client that records peak concurrency"
```

### Task B3: `RagasJudge`

**Files:**
- Create: `src/Rag.NET.Evaluation.Ragas/Judging/RagasJudge.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/Ragas/RagasJudgeTests.cs`

**Step 1: Write the failing tests.**

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class RagasJudgeTests
{
    private static RagasJudge Judge(IChatClient client, RagasOptions? options = null, ICostLedger? ledger = null)
        => new(client, options ?? new RagasOptions(), ledger);

    [Theory]
    [InlineData("yes", Verdict.Yes)]
    [InlineData("Yes.", Verdict.Yes)]
    [InlineData("YES", Verdict.Yes)]
    [InlineData("no", Verdict.No)]
    [InlineData("No.", Verdict.No)]
    public async Task ClassifyAsync_ReadsAPlainVerdict(string reply, Verdict expected)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var verdict = await judge.ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal(expected, verdict);
    }

    [Theory]
    [InlineData("Yes, but only partially.")]
    [InlineData("The claim is supported by the context.")]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task ClassifyAsync_AmbiguousReply_IsUnparseableNotAGuess(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var verdict = await judge.ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        // "Yes, but only partially" counted as full support before 3.1, and "The claim is
        // supported" counted as unsupported. Both were StartsWith("yes") artefacts.
        Assert.Equal(Verdict.Unparseable, verdict);
    }

    [Fact]
    public async Task ExtractListAsync_ValidJson_ParsesItems()
    {
        var judge = Judge(new RoutingChatClient([], fallback: """["one","two"]"""));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.True(result.Parsed);
        Assert.Equal(["one", "two"], result.Items);
    }

    [Fact]
    public async Task ExtractListAsync_EmptyArray_ParsesAsGenuinelyEmpty()
    {
        var judge = Judge(new RoutingChatClient([], fallback: "[]"));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.True(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ExtractListAsync_MalformedJson_ReportsFailureInsteadOfEmpty()
    {
        var judge = Judge(new RoutingChatClient([], fallback: "I'm sorry, I can't do that."));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        // This is the defect that made a broken reply score 1.0: it was indistinguishable from
        // an answer that genuinely asserted nothing.
        Assert.False(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ClassifyManyAsync_RespectsTheConcurrencyCeiling()
    {
        var client = new RoutingChatClient([], fallback: "yes");
        client.GateCalls();
        var judge = Judge(client, new RagasOptions { MaxConcurrentCalls = 2 });
        var items = Enumerable.Range(0, 10).Select(i => $"item {i}").ToList();

        var pending = judge.ClassifyManyAsync("sys", items, _ => "u", TestContext.Current.CancellationToken);

        // Wait until the judge has started as many as it is going to, then release.
        await WaitForAsync(() => client.CallCount >= 2, TestContext.Current.CancellationToken);
        client.ReleaseAll();
        await pending;

        Assert.Equal(10, client.CallCount);
        Assert.True(client.PeakInFlight <= 2, $"peak was {client.PeakInFlight}, ceiling was 2");
    }

    [Fact]
    public async Task ClassifyManyAsync_WithoutACeiling_StillCompletesEveryItem()
    {
        var client = new RoutingChatClient([], fallback: "yes");
        var judge = Judge(client, new RagasOptions { MaxConcurrentCalls = 100 });
        var items = Enumerable.Range(0, 5).Select(i => $"item {i}").ToList();

        var verdicts = await judge.ClassifyManyAsync("sys", items, _ => "u", TestContext.Current.CancellationToken);

        Assert.Equal(5, verdicts.Count);
        Assert.All(verdicts, v => Assert.Equal(Verdict.Yes, v));
    }

    [Fact]
    public async Task ClassifyAsync_WithUsageAndPrices_RecordsCost()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 },
        };
        var options = new RagasOptions { PricePerInputToken = 0.001m, PricePerOutputToken = 0.002m };

        await Judge(client, options, ledger).ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(CostKind.Chat, entry.Kind);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(10, entry.OutputTokens);
        Assert.Equal((100 * 0.001m) + (10 * 0.002m), entry.Cost);
    }

    [Fact]
    public async Task ClassifyAsync_WhenTheModelReportsNoUsage_RecordsNothing()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes") { Usage = null };

        await Judge(client, new RagasOptions { PricePerInputToken = 1m }, ledger)
            .ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        // Recording a zero-token entry would state as fact that the call was free.
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task ClassifyAsync_WhenTheLedgerThrows_DoesNotFailTheEvaluation()
    {
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
        };

        var verdict = await Judge(client, new RagasOptions(), new ThrowingCostLedger())
            .ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.Yes, verdict);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Yield();
    }
}
```

Add the two ledger fakes in the same folder:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Tests.Ragas;

internal sealed class RecordingCostLedger : ICostLedger
{
    private readonly Lock _gate = new();
    private readonly List<CostEntry> _entries = [];

    public IReadOnlyList<CostEntry> Entries { get { lock (_gate) { return [.. _entries]; } } }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
        => Task.FromResult(0m);
}

internal sealed class ThrowingCostLedger : ICostLedger
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("ledger is down");

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
        => Task.FromResult(0m);
}
```

> Confirm `CostWindow`'s namespace before writing these — check `src/Rag.NET.Abstractions/Abstractions/ICostLedger.cs` and its `using` block, and match it.

**Step 2: Run to verify failure.** Expected: `RagasJudge` does not exist.

**Step 3: Implement `RagasJudge`.**

Keep every method under 60 lines (MA0051). Verdict parsing is exact-match after trimming punctuation — anything else is `Unparseable`, deliberately.

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>
/// Owns every LLM interaction the RAGAS metrics need: prompting, parsing, throttling and cost.
/// </summary>
/// <remarks>
/// Exists so the metrics themselves are arithmetic over judgement arrays. Before Phase 3.1 each
/// evaluator carried its own copy of this plumbing, which is how the same JSON-parse defect came
/// to exist twice and the same brittle verdict parsing three times.
/// </remarks>
internal sealed class RagasJudge(
    IChatClient chatClient,
    RagasOptions options,
    ICostLedger? costLedger = null)
{
    private readonly SemaphoreSlim _gate = new(
        options.MaxConcurrentCalls > 0
            ? options.MaxConcurrentCalls
            : throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentCalls must be at least 1."));

    /// <summary>Asks for a yes/no judgement.</summary>
    public async Task<Verdict> ClassifyAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        return ParseVerdict(reply);
    }

    /// <summary>Judges many items under the shared concurrency ceiling, preserving input order.</summary>
    public async Task<IReadOnlyList<Verdict>> ClassifyManyAsync(
        string systemPrompt,
        IReadOnlyList<string> items,
        Func<string, string> toUserPrompt,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<Verdict>[items.Count];
        for (var i = 0; i < items.Count; i++)
            tasks[i] = ClassifyAsync(systemPrompt, toUserPrompt(items[i]), cancellationToken);

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Asks for a JSON array of strings, distinguishing "empty" from "unreadable".</summary>
    public async Task<ExtractionResult> ExtractListAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply))
            return ExtractionResult.Failed();

        try
        {
            var items = JsonSerializer.Deserialize(reply, RagJsonSerializerContext.Default.ListString);
            return items is null ? ExtractionResult.Failed() : ExtractionResult.Success(items);
        }
        catch (JsonException)
        {
            return ExtractionResult.Failed();
        }
    }

    /// <summary>
    /// Exact match after trimming whitespace and trailing punctuation. Anything else is
    /// <see cref="Verdict.Unparseable"/> rather than a guess.
    /// </summary>
    private static Verdict ParseVerdict(string reply)
    {
        var trimmed = reply.Trim().TrimEnd('.', '!', ' ');
        if (string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
            return Verdict.Yes;

        return string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            ? Verdict.No
            : Verdict.Unparseable;
    }

    private async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt),
            };

            var response = await chatClient
                .GetResponseAsync(messages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await RecordCostAsync(response, cancellationToken).ConfigureAwait(false);
            return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordCostAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        // No usage reported means no honest entry to write. Recording zero tokens would state as
        // fact that the call was free.
        if (costLedger is null || response.Usage is not { } usage)
            return;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;

        var entry = new CostEntry
        {
            Kind = CostKind.Chat,
            InputTokens = input,
            OutputTokens = output,
            Cost = (input * options.PricePerInputToken) + (output * options.PricePerOutputToken),
        };

        try
        {
            await costLedger.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Billing visibility must never break an evaluation run.
        }
    }
}
```

> `RagJsonSerializerContext.Default.ListString` is already used by the pre-3.1 evaluators, so the source-generated context exists. Confirm its namespace resolves from this file; the old code has `using Rag.NET.Evaluation;` at the top and finds it by enclosing-namespace lookup from bare `Rag.NET`.

**Step 4: Run tests.** All pass, including the ceiling test.

**Step 5: Commit.**

```bash
git add src/Rag.NET.Evaluation.Ragas/Judging/RagasJudge.cs tests/Rag.NET.Evaluation.Tests/Ragas/RagasJudgeTests.cs tests/Rag.NET.Evaluation.Tests/Ragas/RecordingCostLedger.cs
git commit -m "feat(evaluation): RagasJudge owns prompting, parsing, throttling and cost"
```

---

## Part C: rewrite the four metrics

Each metric now returns `double?` — `null` means *not scoreable*, which is different from zero.

### Task C1: widen `IRagasMetric` to a nullable score

**Files:**
- Modify: `src/Rag.NET.Evaluation.Ragas/IRagasMetric.cs`

```csharp
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

internal interface IRagasMetric
{
    /// <summary>True if this metric requires a non-empty ReferenceAnswer on every sample.</summary>
    bool RequiresGroundTruth { get; }

    /// <summary>
    /// Scores one sample, or returns <c>null</c> when the sample cannot be scored — no retrieved
    /// chunks, or a model reply that could not be read.
    /// </summary>
    /// <remarks>
    /// Nullable rather than a sentinel double. Returning 0.0 for "unscoreable" claims the
    /// retrieval was maximally bad, and returning 1.0 claims it was perfect; the pre-3.1 code did
    /// both in different places. A null is excluded from the aggregate and counted, so a degraded
    /// run is visible instead of averaged in.
    /// </remarks>
    Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken);
}
```

Commit: `refactor(evaluation): let a RAGAS metric report "not scoreable"`

### Task C2: Faithfulness

**Files:**
- Rewrite: `src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/Ragas/FaithfulnessEvaluatorTests.cs`

**Tests to write first** — the first one is the headline regression:

```csharp
[Fact]
public async Task ScoreAsync_WhenClaimExtractionFails_IsNotScoreable()
{
    // Pre-3.1 this returned 1.0 — a malformed reply scored better than a real answer.
    var client = new RoutingChatClient([("Extract", "sorry, I cannot")], fallback: "yes");
    var sut = new FaithfulnessEvaluator(Judge(client));

    var score = await sut.ScoreAsync(Sample(), TestContext.Current.CancellationToken);

    Assert.Null(score);
}

[Fact]
public async Task ScoreAsync_AnswerAssertsNothing_IsTriviallyFaithful()
{
    var client = new RoutingChatClient([("Extract", "[]")], fallback: "yes");
    var sut = new FaithfulnessEvaluator(Judge(client));

    Assert.Equal(1.0, await sut.ScoreAsync(Sample(), TestContext.Current.CancellationToken));
}

[Fact]
public async Task ScoreAsync_HalfTheClaimsSupported_ScoresAHalf()
{
    var client = new RoutingChatClient(
    [
        ("Extract", """["alpha","beta"]"""),
        ("alpha", "yes"),
        ("beta", "no"),
    ]);
    var sut = new FaithfulnessEvaluator(Judge(client));

    Assert.Equal(0.5, await sut.ScoreAsync(Sample(), TestContext.Current.CancellationToken));
}

[Fact]
public async Task ScoreAsync_UnparseableVerdictsAreExcludedNotCountedAgainst()
{
    var client = new RoutingChatClient(
    [
        ("Extract", """["alpha","beta"]"""),
        ("alpha", "yes"),
        ("beta", "I think so?"),
    ]);
    var sut = new FaithfulnessEvaluator(Judge(client));

    // One readable verdict, and it was "yes" -> 1.0 over the judgements actually obtained.
    Assert.Equal(1.0, await sut.ScoreAsync(Sample(), TestContext.Current.CancellationToken));
}

[Fact]
public async Task ScoreAsync_NoSourceChunks_IsNotScoreable()
{
    var sut = new FaithfulnessEvaluator(Judge(new RoutingChatClient([])));

    var score = await sut.ScoreAsync(
        new EvaluationSample("q", "a", "r", SourceChunks: null),
        TestContext.Current.CancellationToken);

    // Nothing retrieved is an absence of evidence, not evidence of an ungrounded answer.
    Assert.Null(score);
}
```

**Implementation shape:**

```csharp
public sealed class FaithfulnessEvaluator : IRagasMetric
{
    // Public constructor keeps the standalone usage that already worked.
    public FaithfulnessEvaluator(IChatClient chatClient, RagasOptions? options = null, ICostLedger? costLedger = null)
        : this(new RagasJudge(chatClient, options ?? new RagasOptions(), costLedger)) { }

    internal FaithfulnessEvaluator(RagasJudge judge) => _judge = judge;

    public bool RequiresGroundTruth => false;

    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        if (sample.SourceChunks is not { Count: > 0 })
            return null;

        var claims = await _judge.ExtractListAsync(ExtractPrompt, sample.PredictedAnswer, cancellationToken)
            .ConfigureAwait(false);
        if (!claims.Parsed)
            return null;
        if (claims.Items.Count == 0)
            return 1.0;

        var context = string.Join("\n", sample.SourceChunks);
        var verdicts = await _judge.ClassifyManyAsync(
            VerifyPrompt, claims.Items, claim => $"Context: {context}\nClaim: {claim}", cancellationToken)
            .ConfigureAwait(false);

        return ScoreFromVerdicts(verdicts);
    }

    /// <summary>Scores over readable verdicts only; null when none were readable.</summary>
    internal static double? ScoreFromVerdicts(IReadOnlyList<Verdict> verdicts)
    {
        var yes = 0;
        var readable = 0;
        foreach (var verdict in verdicts)
        {
            if (verdict == Verdict.Unparseable)
                continue;

            readable++;
            if (verdict == Verdict.Yes)
                yes++;
        }

        return readable == 0 ? null : RagasMath.SupportedFraction(yes, readable);
    }
}
```

Keep the existing prompt text, but add `Respond with exactly "yes" or "no" and nothing else.` to the verify prompt — the parser is now strict, so the prompt must ask for what it accepts.

Commit: `fix(evaluation): Faithfulness no longer scores 1.0 on a parse failure`

### Task C3: Context Recall

Same shape as C2 over `ReferenceAnswer` statements. `ScoreFromVerdicts` is shared — put it on `RagasMath` or a small internal helper rather than copying it, since duplicating exactly this is what produced the original twin defects.

Keep the existing `InvalidOperationException` when `ReferenceAnswer` is empty.

Commit: `fix(evaluation): Context Recall no longer scores 1.0 on a parse failure`

### Task C4: Context Precision — rank-aware

**Files:**
- Rewrite: `src/Rag.NET.Evaluation.Ragas/ContextPrecisionEvaluator.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/Ragas/ContextPrecisionEvaluatorTests.cs`

**The headline test:**

```csharp
[Fact]
public async Task ScoreAsync_IsRankAware()
{
    // Same chunks, same relevance, different order. Pre-3.1 both returned 1/3.
    var goldFirst = await Score(["GOLD", "junk", "junk"]);
    var goldLast  = await Score(["junk", "junk", "GOLD"]);

    Assert.Equal(1.0, goldFirst!.Value, precision: 10);
    Assert.Equal(1.0 / 3.0, goldLast!.Value, precision: 10);
}
```

Verdicts must be collected **in retrieved order** — `ClassifyManyAsync` preserves input order, which is why it exists rather than an unordered `Task.WhenAll` over a `Select`.

Commit: `fix(evaluation): Context Precision is rank-aware average precision`

### Task C5: Answer Relevance

**Files:**
- Rewrite: `src/Rag.NET.Evaluation.Ragas/AnswerRelevanceEvaluator.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/Ragas/AnswerRelevanceEvaluatorTests.cs`

Three fixes:

1. **One call returning `n` distinct questions** as a JSON array via `ExtractListAsync`, not `n` identical prompts. Prompt: `Generate {n} different questions that the following answer responds to. Output a JSON array of strings.`
2. **Noncommittal detection** — a `ClassifyAsync` call asking `Is the answer evasive or a refusal to answer (for example "I don't know")?`. `Verdict.Yes` → score `0.0` immediately, no embedding calls.
3. **Clamp** the mean cosine through `RagasMath.ClampScore`.

Tests must cover: evasive answer scores 0 without embedding; extraction failure → null; identical question and answer embeddings → 1.0; an opposed embedding → clamped to 0.0, not negative.

Needs a hand-written `IEmbeddingGenerator<string, Embedding<float>>` fake returning scripted vectors — add `StubEmbeddingGenerator` alongside `RoutingChatClient`.

Commit: `fix(evaluation): Answer Relevance penalises evasion and clamps its score`

---

## Part D: suite, report, docs

### Task D1: per-sample results and null-aware aggregation

**Files:**
- Modify: `src/Rag.NET.Evaluation.Ragas/RagasReport.cs`
- Modify: `src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuite.cs`
- Modify: `src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuiteBuilder.cs` (accept `RagasOptions` and an optional `ICostLedger`, and construct **one** `RagasJudge` shared by every metric)
- Test: `tests/Rag.NET.Evaluation.Tests/Ragas/RagasEvaluationSuiteTests.cs`

`RagasReport` gains:

```csharp
/// <summary>Per-sample scores, so a poor aggregate can be traced to the samples that caused it.</summary>
public IReadOnlyList<RagasSampleScore> Samples { get; init; } = [];

/// <summary>How many samples could not be scored, per metric name.</summary>
public IReadOnlyDictionary<string, int> UnscoreableSamples { get; init; } =
    new Dictionary<string, int>(StringComparer.Ordinal);
```

Aggregation changes from `total / samples.Count` to a mean over **non-null** scores. A metric with no scoreable sample reports `null`, not `0.0`.

**The shared-ceiling test matters most here:**

```csharp
[Fact]
public async Task EvaluateAsync_ConcurrencyCeilingIsSharedAcrossMetricsNotPerMetric()
{
    var client = new RoutingChatClient([...]);
    client.GateCalls();
    var suite = new RagasEvaluationSuiteBuilder(client, embeddings, new RagasOptions { MaxConcurrentCalls = 2 })
        .AddFaithfulness().AddContextRecall().AddContextPrecision().Build();
    // ... release, await ...
    Assert.True(client.PeakInFlight <= 2, $"peak {client.PeakInFlight} — the ceiling is per metric, not per run");
}
```

Commit: `feat(evaluation): per-sample RAGAS results and a shared concurrency ceiling`

### Task D2: documentation

**Files:**
- Modify: `docs/guide/evaluation.md` — new `## RAGAS metrics` section after `LlmJudgeEvaluator`
- Modify: `docs/reference/features.md` — matrix row `:1054`, and the detail section

The guide section must cover: what each metric measures and its **formula**; which need a `ReferenceAnswer`; how to read a `null` score and the `UnscoreableSamples` count; the noncommittal rule; `MaxConcurrentCalls` and why it is shared per run; cost recording, that prices default to zero, and that evaluation spend now counts toward `UseCostBudgeting`'s window; per-sample output.

State plainly that **scores changed** in this phase and why — rank-aware precision, evasion penalty, no fabricated 1.0 — so anyone with a baseline re-baselines rather than reporting a regression.

In `features.md`, correct the false claim. It currently reads *"Each metric is a standalone `IRagEvaluator<T>` so they can be composed into a `RagasEvaluationSuite`"*. Both halves are wrong: they implement the internal `IRagasMetric`, and the suite cannot be composed by a caller. What is true is that each evaluator class is public and directly usable on its own.

Commit: `docs(evaluation): document the RAGAS metrics and the score changes`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. `dotnet test tests/Rag.NET.Evaluation.Tests` → all green; report the count (28 before this phase).
3. `dotnet test tests/Rag.NET.Tests` → 1311.
4. Confirm no `#pragma` or `SuppressMessage` was added: `git diff main --stat` and grep the diff.
5. Confirm `.lucent/*` and `.claude/worktrees/*` are unstaged in every commit.
6. Update `docs/planning/ROADMAP.md` Phase 3.1 to `[status: complete]` with a `**Completed:**` line — **after** the whole-phase review, not before.
