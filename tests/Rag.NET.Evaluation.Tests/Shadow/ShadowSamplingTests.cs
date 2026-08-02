using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// Sampling: off by default, deterministic under test, and never clamped. The sampler is
/// injected, so the rate-0 and rate-1 cases are exact assertions rather than probabilistic
/// ones — and at both extremes the sampler is provably never consulted at all, which is what
/// lets those two tests run against the default <c>Random.Shared</c> source in registration
/// tests without a flake.
/// </summary>
[Collection("ShadowTelemetry")] // emits shadow counters; see ShadowTelemetryCollection
public sealed class ShadowSamplingTests
{
    [Fact]
    public void SampleRate_DefaultsToZero()
    {
        // Registering shadow mode must do nothing until someone deliberately sets a number:
        // the secondary costs real money on someone else's bill.
        Assert.Equal(0.0, new ShadowPipelineOptions().SampleRate);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SampleRate_OutOfRange_IsRejectedNotClamped(double rate)
    {
        // A clamped rate is a rate nobody chose; refused where the option is set.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShadowPipelineOptions { SampleRate = rate });
    }

    /// <summary>
    /// Rate 0 shadows nothing: nothing is enqueued, the secondary is never invoked at all, and
    /// the sampler itself is never consulted — a throwing sampler proves the last part, because
    /// a decorator that rolled the dice before comparing to zero would fail this test loudly.
    /// </summary>
    [Fact]
    public async Task AskAsync_RateZero_ShadowsNothing_AndNeverConsultsTheSampler()
    {
        var secondary = new ScriptedRagPipeline();
        var queue = NewQueue();
        var shadowed = NewShadowed(secondary, queue, rate: 0.0, MustNotBeConsulted);

        var response = await shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken);

        Assert.Equal("primary-answer", response.Answer);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.DroppedCount);
        Assert.Equal(0, secondary.AskCalls);
    }

    /// <summary>
    /// Rate 1 shadows everything, and — mirroring rate 0 — without consulting the sampler: a
    /// custom source returning exactly 1.0 must not be able to make a "shadow everything" rate
    /// skip requests.
    /// </summary>
    [Fact]
    public async Task AskAsync_RateOne_ShadowsEverything_AndNeverConsultsTheSampler()
    {
        var queue = NewQueue();
        var shadowed = NewShadowed(new ScriptedRagPipeline(), queue, rate: 1.0, MustNotBeConsulted);

        await shadowed.AskAsync("q1", options: null, TestContext.Current.CancellationToken);
        await shadowed.AskAsync("q2", options: null, TestContext.Current.CancellationToken);
        await shadowed.AskAsync("q3", options: null, TestContext.Current.CancellationToken);

        Assert.Equal(3, queue.PendingCount);
    }

    /// <summary>
    /// Between the extremes the injected source decides, deterministically: a draw strictly
    /// below the rate is sampled, a draw at or above it is not. The boundary draw equal to the
    /// rate is excluded so that the probability of sampling is exactly the rate over the
    /// source's half-open [0, 1) range.
    /// </summary>
    [Fact]
    public async Task AskAsync_MidRate_TakesExactlyTheDrawsBelowTheRate()
    {
        var queue = NewQueue();
        var draws = new Queue<double>([0.49, 0.5, 0.51, 0.0]);
        var shadowed = NewShadowed(new ScriptedRagPipeline(), queue, rate: 0.5, () => draws.Dequeue());

        await shadowed.AskAsync("sampled", options: null, TestContext.Current.CancellationToken);
        await shadowed.AskAsync("at-the-rate", options: null, TestContext.Current.CancellationToken);
        await shadowed.AskAsync("above", options: null, TestContext.Current.CancellationToken);
        await shadowed.AskAsync("sampled-too", options: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, queue.PendingCount);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("sampled", first.Question);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("sampled-too", second.Question);
    }

    /// <summary>An unsampled request is still served normally — sampling skips the shadow, never the caller.</summary>
    [Fact]
    public async Task AskAsync_AnUnsampledRequest_IsStillServedThePrimaryAnswer()
    {
        var queue = NewQueue();
        var shadowed = NewShadowed(new ScriptedRagPipeline(), queue, rate: 0.5, () => 0.9);

        var response = await shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken);

        Assert.Equal("primary-answer", response.Answer);
        Assert.Equal(0, queue.PendingCount);
    }

    private static double MustNotBeConsulted() =>
        throw new InvalidOperationException("the sampler must not be consulted at rate 0 or rate 1");

    private static ShadowCaptureQueue NewQueue() =>
        new(new ShadowCaptureQueueOptions { Capacity = 8 });

    private static ShadowRagPipeline NewShadowed(
        ScriptedRagPipeline secondary,
        ShadowCaptureQueue queue,
        double rate,
        Func<double> sampleSource) =>
        new(
            new ScriptedRagPipeline((_, _, _) =>
                Task.FromResult(ScriptedRagPipeline.Response("primary-answer", "primary-context"))),
            secondary,
            queue,
            new ShadowPipelineOptions { SampleRate = rate },
            sampleSource: sampleSource);
}
