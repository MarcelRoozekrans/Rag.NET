using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// One side of a captured pair is either an answer or a failure — never both, never neither. A
/// side that is both is ambiguous, and a side that is neither replays as nothing at all; both
/// would sit quietly in storage until an offline comparison tripped over them much later.
/// </summary>
public sealed class ShadowVariantCaptureTests
{
    [Fact]
    public void AnAnsweredSide_CarriesEverythingTheOfflineComparisonReads()
    {
        var capture = new ShadowVariantCapture(
            VariantName: "primary",
            Answer: "42",
            ContextTexts: ["first chunk", "second chunk"],
            Latency: TimeSpan.FromMilliseconds(120),
            Spend: 0.003m);

        Assert.Equal("primary", capture.VariantName);
        Assert.Equal("42", capture.Answer);
        Assert.Equal(["first chunk", "second chunk"], capture.ContextTexts);
        Assert.Equal(TimeSpan.FromMilliseconds(120), capture.Latency);
        Assert.Equal(0.003m, capture.Spend);
        Assert.Null(capture.Failure);
    }

    [Fact]
    public void AFailedSide_CarriesTheFailureAndMayStillCarrySpend()
    {
        // A call that threw can still have cost money before it threw — embeddings, retrieval —
        // so spend is not tied to success.
        var capture = new ShadowVariantCapture(
            VariantName: "shadow",
            Answer: null,
            ContextTexts: [],
            Spend: 0.001m,
            Failure: new ShadowVariantFailure("shadow", "InvalidOperationException: boom"));

        Assert.Null(capture.Answer);
        Assert.NotNull(capture.Failure);
        Assert.Equal(0.001m, capture.Spend);
    }

    [Fact]
    public void NeitherAnswerNorFailure_Throws()
        => Assert.Throws<ArgumentException>(() => new ShadowVariantCapture(
            VariantName: "shadow",
            Answer: null,
            ContextTexts: []));

    [Fact]
    public void BothAnswerAndFailure_Throws()
        => Assert.Throws<ArgumentException>(() => new ShadowVariantCapture(
            VariantName: "shadow",
            Answer: "42",
            ContextTexts: [],
            Failure: new ShadowVariantFailure("shadow", "InvalidOperationException: boom")));

    [Fact]
    public void NullContextTexts_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ShadowVariantCapture(
            VariantName: "primary",
            Answer: "42",
            ContextTexts: null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankVariantName_Throws(string? name)
    {
        // The offline scorer keys everything by variant name and refuses blanks at scoring time;
        // refusing here fails where the capture was written instead of months later.
        Assert.ThrowsAny<ArgumentException>(() => new ShadowVariantCapture(
            VariantName: name!,
            Answer: "42",
            ContextTexts: []));
    }
}
