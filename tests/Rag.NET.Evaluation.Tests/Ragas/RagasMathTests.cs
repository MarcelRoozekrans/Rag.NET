using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class RagasMathTests
{
    [Fact]
    public void AveragePrecision_GoldChunkFirst_ScoresHigherThanGoldChunkLast()
    {
        var first = RagasMath.AveragePrecision([true, false, false]);
        var last = RagasMath.AveragePrecision([false, false, true]);

        // The defect this replaces returned 1/3 for both.
        Assert.True(first > last, $"rank-blind: first={first}, last={last}");
        Assert.Equal(1.0, first, precision: 10);
        Assert.Equal(1.0 / 3.0, last, precision: 10);
    }

    [Theory]
    // relevance by rank                     expected average precision
    [InlineData(new[] { true }, 1.0)]
    [InlineData(new[] { false }, 0.0)]
    [InlineData(new[] { true, true }, 1.0)]
    [InlineData(new[] { false, false }, 0.0)]
    // P@1=1/1, P@3=2/3 -> (1 + 0.666..) / 2
    [InlineData(new[] { true, false, true }, 0.8333333333333333)]
    // P@2=1/2, P@3=2/3 -> (0.5 + 0.666..) / 2
    [InlineData(new[] { false, true, true }, 0.5833333333333333)]
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
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = RagasMath.SupportedFraction(0, 0));

        Assert.Equal("total", exception.ParamName);
    }

    [Theory]
    [InlineData(-0.4, 0.0)]   // cosine is [-1,1]; the score contract is [0,1]
    [InlineData(0.0, 0.0)]
    [InlineData(0.55, 0.55)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0000001, 1.0)]
    public void ClampScore_ConstrainsToTheDocumentedRange(double raw, double expected)
        => Assert.Equal(expected, RagasMath.ClampScore(raw), precision: 10);
}
