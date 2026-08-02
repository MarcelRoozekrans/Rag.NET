using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// What <see cref="ShadowReplay.From"/> builds from stored captures: the sample list the offline
/// scorer walks, the reference answers that unlock the ground-truth metrics, and the captured
/// production latency and spend — the figures a replayed comparison must be read against, because
/// the report's own timing section times an in-memory lookup.
/// </summary>
public sealed class ShadowReplayTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_BuildsOneSampleAndBothVariantsFromTheCapturesAlone()
    {
        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")]);

        Assert.Equal("primary", replay.VariantA.Name);
        Assert.Equal("shadow", replay.VariantB.Name);
        Assert.Equal(2, replay.Samples.Count);
        Assert.Equal("q1", replay.Samples[0].Question);
        Assert.Equal("q2", replay.Samples[1].Question);

        // Production traffic carries no reference answer, so the default is empty — which is why
        // only the reference-free metrics can score an unannotated replay.
        Assert.All(replay.Samples, sample => Assert.Equal("", sample.ReferenceAnswer));
    }

    [Fact]
    public void From_WithAReferenceAnswerForAQuestion_PutsItOnThatQuestionsSample()
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["q2"] = "The reference for q2.",
        };

        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")], references);

        Assert.Equal("", replay.Samples[0].ReferenceAnswer);
        Assert.Equal("The reference for q2.", replay.Samples[1].ReferenceAnswer);
    }

    [Fact]
    public void From_AnEmptyCaptureSet_IsRefusedWithTheReasonRatherThanCrashingLater()
    {
        var exception = Assert.Throws<ArgumentException>(() => ShadowReplay.From([]));

        Assert.Equal("captures", exception.ParamName);
        Assert.Contains("no captures", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_CapturesFromDifferentVariantPairs_AreRefusedAsOneReplay()
    {
        var mixed = new[]
        {
            Capture("q1"),
            new ShadowCapture("q2", Answered("primary", "q2"), Answered("shadow-v2", "q2"), Timestamp),
        };

        var exception = Assert.Throws<ArgumentException>(() => ShadowReplay.From(mixed));

        Assert.Equal("captures", exception.ParamName);
        Assert.Contains("shadow-v2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_LatencyIsTheCapturedProductionWallClockNotAReplayMeasurement()
    {
        // 900/1100, 800/1000, 700/1300 milliseconds captured in production. A replayed pipeline
        // answers from memory in microseconds, so these figures can only come from the captures.
        var replay = ShadowReplay.From(
        [
            CaptureWithLatency("q1", primaryMs: 900, secondaryMs: 1100),
            CaptureWithLatency("q2", primaryMs: 800, secondaryMs: 1000),
            CaptureWithLatency("q3", primaryMs: 700, secondaryMs: 1300),
        ]);

        Assert.Equal(3, replay.Latency.ComparedPairs);
        Assert.Equal(TimeSpan.FromMilliseconds(800), replay.Latency.MedianA);
        Assert.Equal(TimeSpan.FromMilliseconds(900), replay.Latency.Percentile95A);
        Assert.Equal(TimeSpan.FromMilliseconds(1100), replay.Latency.MedianB);
        Assert.Equal(TimeSpan.FromMilliseconds(1300), replay.Latency.Percentile95B);

        // Deltas are 200, 200 and 600 — mean 1000/3. Constant-free of replay noise: every figure
        // is a wall-clock that really happened in production.
        Assert.Equal(1000.0 / 3, replay.Latency.MeanDeltaMilliseconds!.Value, precision: 10);
        Assert.NotNull(replay.Latency.ConfidenceIntervalMilliseconds);
    }

    [Fact]
    public void From_LatencyPairsOnlyCapturesWhereBothSidesWereMeasured()
    {
        var replay = ShadowReplay.From(
        [
            CaptureWithLatency("q1", primaryMs: 900, secondaryMs: 1100),
            CaptureWithFailedSecondary("q2"),
        ]);

        // The failed side has no latency — timing a call that threw measures how fast it failed —
        // so the pair drops out of the latency comparison entirely rather than pairing 900 ms
        // against a fabricated zero.
        Assert.Equal(1, replay.Latency.ComparedPairs);
        Assert.Equal(TimeSpan.FromMilliseconds(900), replay.Latency.MedianA);
        Assert.Equal(TimeSpan.FromMilliseconds(1100), replay.Latency.MedianB);
    }

    [Fact]
    public void From_NoCaptureCarriesLatency_ReportsAbsenceNotZeros()
    {
        var replay = ShadowReplay.From([Capture("q1")]);

        Assert.Equal(0, replay.Latency.ComparedPairs);
        Assert.Null(replay.Latency.MedianA);
        Assert.Null(replay.Latency.MeanDeltaMilliseconds);
        Assert.Null(replay.Latency.ConfidenceIntervalMilliseconds);
    }

    [Fact]
    public void From_SpendIsTheCapturedTotalPerFullyMeasuredSide_AndTheUnmeasuredPrimaryIsAbsent()
    {
        // Task 5: primary spend is deliberately null — it serves concurrent traffic on a shared
        // ledger, so no honest per-request figure exists. A failed secondary can still carry
        // spend; it cost money before it threw, and that money is part of the total.
        var replay = ShadowReplay.From(
        [
            CaptureWithSecondarySpend("q1", 0.002m),
            CaptureWithSecondarySpend("q2", 0.003m),
            CaptureWithFailedSecondary("q3", spend: 0.001m),
        ]);

        Assert.Equal(0.006m, replay.Spend["shadow"]);
        Assert.False(replay.Spend.ContainsKey("primary"));
    }

    [Fact]
    public void From_ASideWithAnyUnmeasuredCapture_HasNoSpendFigureRatherThanAnUndercount()
    {
        var replay = ShadowReplay.From(
        [
            CaptureWithSecondarySpend("q1", 0.002m),
            Capture("q2"), // secondary spend null: nothing measured it
        ]);

        // Summing the captures that happened to be measured and presenting it as the side's spend
        // would undercount; absence carries the honest meaning, exactly as in AbReport.Cost.
        Assert.Empty(replay.Spend);
    }

    private static ShadowCapture Capture(string question) => new(
        question,
        Answered("primary", question),
        Answered("shadow", question),
        Timestamp);

    private static ShadowCapture CaptureWithLatency(string question, double primaryMs, double secondaryMs) => new(
        question,
        Answered("primary", question) with { Latency = TimeSpan.FromMilliseconds(primaryMs) },
        Answered("shadow", question) with { Latency = TimeSpan.FromMilliseconds(secondaryMs) },
        Timestamp);

    private static ShadowCapture CaptureWithSecondarySpend(string question, decimal spend) => new(
        question,
        Answered("primary", question),
        Answered("shadow", question) with { Spend = spend },
        Timestamp);

    private static ShadowCapture CaptureWithFailedSecondary(string question) => new(
        question,
        Answered("primary", question) with { Latency = TimeSpan.FromMilliseconds(900) },
        FailedSecondary(),
        Timestamp);

    private static ShadowCapture CaptureWithFailedSecondary(string question, decimal spend) => new(
        question,
        Answered("primary", question) with { Latency = TimeSpan.FromMilliseconds(900) },
        FailedSecondary() with { Spend = spend },
        Timestamp);

    private static ShadowVariantCapture FailedSecondary() => new(
        "shadow",
        Answer: null,
        ContextTexts: [],
        Failure: new ShadowVariantFailure("shadow", "InvalidOperationException: the vector store went away"));

    private static ShadowVariantCapture Answered(string name, string question) => new(
        name,
        Answer: $"{name} answer to {question}",
        ContextTexts: [$"{name} context for {question}"]);
}
