using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
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

    // Verdict is internal, so these cannot be [InlineData] parameters without making the public
    // test method expose an internal type (CS0051). Facts it is.
    [Fact]
    public void ScoreFromVerdicts_AllSupported_IsOne()
        => Assert.Equal(1.0, RagasMath.ScoreFromVerdicts([Verdict.Yes, Verdict.Yes])!.Value, precision: 10);

    [Fact]
    public void ScoreFromVerdicts_NoneSupported_IsZero()
        => Assert.Equal(0.0, RagasMath.ScoreFromVerdicts([Verdict.No, Verdict.No])!.Value, precision: 10);

    [Fact]
    public void ScoreFromVerdicts_HalfSupported_IsAHalf()
        => Assert.Equal(0.5, RagasMath.ScoreFromVerdicts([Verdict.Yes, Verdict.No])!.Value, precision: 10);

    [Fact]
    public void ScoreFromVerdicts_UnparseableIsExcludedFromTheDenominator()
    {
        // Counted as "no" this would be 0.5; the model never denied anything, so it is 1.0
        // over the one judgement actually obtained.
        var score = RagasMath.ScoreFromVerdicts([Verdict.Yes, Verdict.Unparseable]);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public void ScoreFromVerdicts_NothingReadable_IsNotScoreable()
        => Assert.Null(RagasMath.ScoreFromVerdicts([Verdict.Unparseable, Verdict.Unparseable]));

    [Fact]
    public void ScoreFromVerdicts_NoVerdictsAtAll_IsNotScoreable()
        => Assert.Null(RagasMath.ScoreFromVerdicts([]));
}
