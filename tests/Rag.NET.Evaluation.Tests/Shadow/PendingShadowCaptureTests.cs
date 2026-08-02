using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The queued work item. The property worth pinning is its shape: everything the background
/// consumer needs to run the secondary and assemble the pair — and nothing that could only be
/// known by running the secondary on the request path, which is exactly what it exists to avoid.
/// </summary>
public sealed class PendingShadowCaptureTests
{
    [Fact]
    public void Constructor_NullQuestion_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PendingShadowCapture(
            null!, Options: null, Primary(), new ScriptedRagPipeline(), "shadow", CapturedAt));
    }

    [Fact]
    public void Constructor_NullPrimary_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PendingShadowCapture(
            "q", Options: null, null!, new ScriptedRagPipeline(), "shadow", CapturedAt));
    }

    [Fact]
    public void Constructor_NullSecondaryPipeline_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PendingShadowCapture(
            "q", Options: null, Primary(), null!, "shadow", CapturedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankSecondaryVariantName_Throws(string? name)
    {
        Assert.Throws<ArgumentException>(() => new PendingShadowCapture(
            "q", Options: null, Primary(), new ScriptedRagPipeline(), name!, CapturedAt));
    }

    [Fact]
    public void Constructor_SecondaryNameMatchingThePrimarys_Throws()
    {
        // ShadowCapture refuses same-named sides at build time — on the consumer, long after the
        // request. Refusing here surfaces the misconfiguration where it was made instead.
        Assert.Throws<ArgumentException>(() => new PendingShadowCapture(
            "q", Options: null, Primary(), new ScriptedRagPipeline(), "primary", CapturedAt));
    }

    [Fact]
    public void ToCapture_BuildsThePairAroundTheCapturedPrimary()
    {
        var primary = Primary();
        var pending = new PendingShadowCapture(
            "q", Options: null, primary, new ScriptedRagPipeline(), "shadow", CapturedAt);
        var secondary = new ShadowVariantCapture("shadow", Answer: "b", ContextTexts: ["d"]);

        var capture = pending.ToCapture(secondary);

        Assert.Equal("q", capture.Question);
        Assert.Same(primary, capture.Primary);
        Assert.Same(secondary, capture.Secondary);
        Assert.Equal(CapturedAt, capture.CapturedAt);
    }

    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static ShadowVariantCapture Primary() =>
        new("primary", Answer: "a", ContextTexts: ["c"], Latency: TimeSpan.FromMilliseconds(1));
}
