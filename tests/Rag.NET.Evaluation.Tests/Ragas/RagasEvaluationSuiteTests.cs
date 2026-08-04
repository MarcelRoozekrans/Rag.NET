using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class RagasEvaluationSuiteTests
{
    private const string ClaimsRoute = "atomic factual claims";
    private const string StatementsRoute = "atomic statements";

    private static StubEmbeddingGenerator Embedder() => new([1f, 0f]);

    private static EvaluationSample Sample(
        string question = "Q?",
        string predictedAnswer = "The predicted answer.",
        IReadOnlyList<string>? chunks = null)
        => new(question, predictedAnswer, "The reference answer.", chunks ?? ["chunk one"]);

    [Fact]
    public async Task EvaluateAsync_ConcurrencyCeilingIsSharedAcrossMetricsNotPerMetric()
    {
        // Three chat-only metrics at a ceiling of 2. The suite starts every metric for a sample
        // before awaiting any of them, so by the time the first await returns, each metric has
        // already issued as many calls as its judge would let it: two in total if the judge is
        // shared, four if each metric owns one (Faithfulness 1 + Context Precision 2 + Recall 1).
        var client = new RoutingChatClient(
        [
            (ClaimsRoute, """["alpha"]"""),
            (StatementsRoute, """["beta"]"""),
        ],
            fallback: "yes");
        client.GateCalls();

        var suite = new RagasEvaluationSuiteBuilder(
                client, Embedder(), new RagasOptions { MaxConcurrentCalls = 2 })
            .AddFaithfulness()
            .AddContextPrecision()
            .AddContextRecall()
            .Build();

        var chunks = new List<string>();
        for (var i = 0; i < 6; i++)
            chunks.Add($"chunk {i}");

        var pending = suite.EvaluateAsync([Sample(chunks: chunks)], TestContext.Current.CancellationToken);

        await WaitForAsync(() => client.CallCount >= 2, TestContext.Current.CancellationToken);
        var peakWhileGated = client.PeakInFlight;
        client.ReleaseAll();
        var report = await pending;

        Assert.True(
            client.PeakInFlight <= 2,
            $"peak {client.PeakInFlight} (peak {peakWhileGated} before release) — the ceiling is per metric, not per run");

        // The run must actually have done the work; a ceiling that held because nothing ran
        // would satisfy the assertion above and mean nothing.
        Assert.Equal(1.0, report.Faithfulness!.Value, precision: 10);
        Assert.Equal(1.0, report.ContextPrecision!.Value, precision: 10);
        Assert.Equal(1.0, report.ContextRecall!.Value, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsEverySampleInInputOrder()
    {
        var client = new RoutingChatClient([(ClaimsRoute, """["alpha"]""")], fallback: "yes");
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).AddFaithfulness().Build();

        var report = await suite.EvaluateAsync(
            [Sample(question: "first?"), Sample(question: "second?")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Samples.Count);
        Assert.Equal("first?", report.Samples[0].Question);
        Assert.Equal("second?", report.Samples[1].Question);
        Assert.Equal(1.0, report.Samples[0].Scores[RagasMetricNames.Faithfulness]!.Value, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_UnscoreableSampleIsExcludedFromTheMeanAndCounted()
    {
        // One scoreable sample scoring 1.0, one that cannot be scored at all. Folding the second
        // in as a zero would report 0.5 — a number no sample produced, and the fabrication this
        // phase exists to remove.
        var client = new RoutingChatClient([(ClaimsRoute, """["alpha"]""")], fallback: "yes");
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).AddFaithfulness().Build();

        var report = await suite.EvaluateAsync(
            [Sample(question: "scoreable?"), Sample(question: "nothing retrieved?", chunks: [])],
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, report.Faithfulness!.Value, precision: 10);
        Assert.Equal(1, report.UnscoreableSamples[RagasMetricNames.Faithfulness]);
        Assert.NotNull(report.Samples[0].Scores[RagasMetricNames.Faithfulness]);
        Assert.Null(report.Samples[1].Scores[RagasMetricNames.Faithfulness]);
    }

    [Fact]
    public async Task EvaluateAsync_MetricThatScoredEverySample_ReportsAZeroUnscoreableCount()
    {
        // An absent key would force the caller to read "no key" as either "none" or "not
        // registered", which are opposite facts.
        var client = new RoutingChatClient([(ClaimsRoute, """["alpha"]""")], fallback: "yes");
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).AddFaithfulness().Build();

        var report = await suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken);

        Assert.Equal(0, report.UnscoreableSamples[RagasMetricNames.Faithfulness]);
        Assert.DoesNotContain(RagasMetricNames.ContextRecall, report.UnscoreableSamples);
    }

    [Fact]
    public async Task EvaluateAsync_NoSampleScoreable_ReportsNullNotZero()
    {
        // 0.0 would state that the answer was entirely ungrounded. Nothing was retrieved, so
        // nothing about grounding was established either way.
        var client = new RoutingChatClient([], fallback: "yes");
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).AddFaithfulness().Build();

        var report = await suite.EvaluateAsync(
            [Sample(chunks: []), Sample(chunks: [])], TestContext.Current.CancellationToken);

        Assert.Null(report.Faithfulness);
        Assert.Equal(2, report.UnscoreableSamples[RagasMetricNames.Faithfulness]);
        Assert.Equal(0, client.CallCount);

        // And not one level up either: an overall of 0.0 would report the worst possible quality
        // for a run that established nothing about quality at all.
        Assert.Null(report.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_SomeMetricsScoreable_OverallIsTheMeanOfThoseOnly()
    {
        // Faithfulness scores 1.0; Context Recall cannot read its statement list, so it is null.
        // Averaging the null in as a zero would report 0.5 for a run whose only readable metric
        // was perfect.
        var client = new RoutingChatClient(
        [
            (ClaimsRoute, "[]"),
            (StatementsRoute, "I'm sorry, I can't do that."),
        ],
            fallback: "yes");

        var suite = new RagasEvaluationSuiteBuilder(client, Embedder())
            .AddFaithfulness()
            .AddContextRecall()
            .Build();

        var report = await suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken);

        Assert.Equal(1.0, report.Faithfulness!.Value, precision: 10);
        Assert.Null(report.ContextRecall);
        Assert.Equal(1.0, report.OverallScore!.Value, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_NoSamples_Throws()
    {
        // Migrated from tests/Rag.NET.Tests/Evaluation when that duplicate suite was deleted
        // (Phase 4.1), like the three tests below: they covered suite contracts this suite did
        // not. An empty run reporting an empty report would read as a measurement of nothing.
        var client = new RoutingChatClient([]);
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).AddFaithfulness().Build();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            suite.EvaluateAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvaluateAsync_NoMetricsRegistered_Throws()
    {
        var client = new RoutingChatClient([]);
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken));

        Assert.Equal("No metrics are registered.", exception.Message);
    }

    [Fact]
    public async Task EvaluateAsync_MetricPreconditionFailure_ThrowsBeforeSpendingAnything()
    {
        // Build() succeeds — validation belongs to the run — and the run fails fast: Context
        // Precision requires a reference answer, and the throw must land before any model call
        // is paid for, at suite level exactly as it does on the evaluator alone.
        var client = new RoutingChatClient([]);
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder()).AddContextPrecision().Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            suite.EvaluateAsync(
                [new EvaluationSample("Q?", "A.", string.Empty, ["chunk one"])],
                TestContext.Current.CancellationToken));

        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_OverallIsTheMeanOfTheRegisteredMetrics_AndUnregisteredStayNull()
    {
        // Faithfulness asserts nothing -> trivially 1.0; every chunk judged irrelevant ->
        // Context Precision 0.0. The overall must be their mean, and the two metrics nobody
        // registered must stay null rather than joining it as zeros.
        var client = new RoutingChatClient([(ClaimsRoute, "[]")], fallback: "no");
        var suite = new RagasEvaluationSuiteBuilder(client, Embedder())
            .AddFaithfulness()
            .AddContextPrecision()
            .Build();

        var report = await suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken);

        Assert.Equal(1.0, report.Faithfulness!.Value, precision: 10);
        Assert.Equal(0.0, report.ContextPrecision!.Value, precision: 10);
        Assert.Null(report.AnswerRelevance);
        Assert.Null(report.ContextRecall);
        Assert.Equal(0.5, report.OverallScore!.Value, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_WithACostLedger_RecordsTheWholeRunsSpend()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([(ClaimsRoute, """["alpha"]""")], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 },
        };
        var options = new RagasOptions { PricePerInputToken = 0.5m, PricePerOutputToken = 0.25m };

        var suite = new RagasEvaluationSuiteBuilder(client, Embedder(), options, ledger)
            .AddFaithfulness()
            .Build();

        await suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken);

        // One extraction plus one verification, both billed through the single shared judge.
        Assert.Equal(2, ledger.Entries.Count);
        foreach (var entry in ledger.Entries)
        {
            Assert.Equal(CostKind.Chat, entry.Kind);
            Assert.Equal((10 * 0.5m) + (2 * 0.25m), entry.Cost);
        }
    }

    [Fact]
    public async Task EvaluateAsync_WithACostLedger_RecordsAnswerRelevancesEmbeddingBatchToo()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([("different questions", """["a?"]""")], fallback: "no");
        var embedder = new StubEmbeddingGenerator([1f, 0f])
        {
            Usage = new UsageDetails { InputTokenCount = 8 },
        };
        var options = new RagasOptions { PricePerEmbeddingToken = 0.5m };

        var suite = new RagasEvaluationSuiteBuilder(client, embedder, options, ledger)
            .AddAnswerRelevance()
            .Build();

        await suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken);

        // The builder has to hand the ledger to the evaluator as well as to the judge: the
        // embedding batch is the one call in a run that does not go through the judge.
        var embedding = Assert.Single(ledger.Entries, entry => entry.Kind == CostKind.Embedding);
        Assert.Equal(8, embedding.InputTokens);
        Assert.Equal(8 * 0.5m, embedding.Cost);
    }

    [Fact]
    public async Task EvaluateAsync_SyntheticQuestionCountFromOptions_ReachesAnswerRelevance()
    {
        var client = new RoutingChatClient([("different questions", """["a?","b?"]""")], fallback: "no");
        var suite = new RagasEvaluationSuiteBuilder(
                client, Embedder(), new RagasOptions { SyntheticQuestionCount = 5 })
            .AddAnswerRelevance()
            .Build();

        await suite.EvaluateAsync([Sample()], TestContext.Current.CancellationToken);

        Assert.Contains(
            client.Prompts,
            prompt => prompt.Contains("Generate 5 different questions", StringComparison.Ordinal));
    }

    /// <summary>
    /// Spins until <paramref name="condition"/> holds, or five seconds pass.
    /// </summary>
    /// <remarks>
    /// Bounded so a regression that stops the suite starting its calls fails the run rather than
    /// hanging it.
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
