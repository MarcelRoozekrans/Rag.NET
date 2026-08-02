using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The captured pair: one production question, what both pipelines did with it, and when. The
/// invariants pinned here are the ones that would otherwise surface as scorer exceptions long
/// after the capture was written.
/// </summary>
public sealed class ShadowCaptureTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HoldsTheQuestionBothSidesAndTheTimestamp()
    {
        var capture = new ShadowCapture("why is the sky blue?", Answered("primary"), Answered("shadow"), Timestamp);

        Assert.Equal("why is the sky blue?", capture.Question);
        Assert.Equal("primary", capture.Primary.VariantName);
        Assert.Equal("shadow", capture.Secondary.VariantName);
        Assert.Equal(Timestamp, capture.CapturedAt);
    }

    [Fact]
    public void TheSameVariantNameOnBothSides_Throws()
    {
        // AbRunner refuses duplicated variant names at scoring time. A capture that names both
        // sides "primary" is unscoreable from the moment it is written, so it is refused where
        // it is written rather than when someone finally replays it.
        var exception = Assert.Throws<ArgumentException>(
            () => new ShadowCapture("q", Answered("primary"), Answered("primary"), Timestamp));

        Assert.Contains("primary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullQuestion_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => new ShadowCapture(null!, Answered("primary"), Answered("shadow"), Timestamp));

    [Fact]
    public void NullSides_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ShadowCapture("q", null!, Answered("shadow"), Timestamp));
        Assert.Throws<ArgumentNullException>(
            () => new ShadowCapture("q", Answered("primary"), null!, Timestamp));
    }

    [Fact]
    public void AnEmptyQuestion_IsACaptureRatherThanAnError()
    {
        // Production questions are whatever users typed. An empty one is usually a pipeline
        // rejection — which the capture records as a failure — but the capture itself must not
        // refuse to record what actually happened.
        var capture = new ShadowCapture("", Answered("primary"), Answered("shadow"), Timestamp);

        Assert.Equal("", capture.Question);
    }

    private static ShadowVariantCapture Answered(string name)
        => new(name, Answer: $"{name} answer", ContextTexts: [$"{name} context"]);
}
