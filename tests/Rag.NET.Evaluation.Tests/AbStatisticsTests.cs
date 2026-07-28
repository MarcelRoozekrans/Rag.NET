using Rag.NET.Evaluation.Internal;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

/// <summary>
/// The paired arithmetic is pure, so it is pinned here with no pipeline and no model — the same
/// split that let <c>RagasMath</c> and <c>ReservoirSampler</c> be tested exhaustively.
/// </summary>
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
        Assert.Equal(2, deltas.Length);
        Assert.Equal(0.1, deltas[0], precision: 10);
        Assert.Equal(0.2, deltas[1], precision: 10);
    }

    [Fact]
    public void PairedDeltas_KeepsInputOrder()
    {
        double?[] a = [0.1, 0.9, 0.3];
        double?[] b = [0.4, 0.2, 0.8];

        var deltas = AbStatistics.PairedDeltas(a, b);

        Assert.Equal(3, deltas.Length);
        Assert.Equal(0.3, deltas[0], precision: 10);
        Assert.Equal(-0.7, deltas[1], precision: 10);
        Assert.Equal(0.5, deltas[2], precision: 10);
    }

    [Fact]
    public void PairedDeltas_NothingComparable_IsEmptyNotZero()
    {
        double?[] a = [null, 0.4];
        double?[] b = [0.5, null];

        Assert.Empty(AbStatistics.PairedDeltas(a, b));
    }

    [Fact]
    public void PairedDeltas_MismatchedLengths_Throws()
        => Assert.Throws<ArgumentException>(() => AbStatistics.PairedDeltas([0.1], [0.1, 0.2]));

    [Theory]
    [InlineData(new[] { 0.1, 0.2, 0.3 }, 0.2)]
    [InlineData(new[] { -0.1, 0.1 }, 0.0)]
    [InlineData(new[] { 0.5 }, 0.5)]
    public void MeanDelta_IsTheArithmeticMean(double[] deltas, double expected)
        => Assert.Equal(expected, AbStatistics.MeanDelta(deltas)!.Value, precision: 10);

    [Fact]
    public void MeanDelta_NoPairs_IsNull()
    {
        // Not 0.0: zero means the variants tied, which is a finding. Null means nothing could be
        // compared, which is not.
        Assert.Null(AbStatistics.MeanDelta([]));
    }

    [Theory]
    // deltas                              B wins, A wins, ties
    [InlineData(new[] { 0.1, 0.2, -0.3 }, 2, 1, 0)]
    [InlineData(new[] { 0.0, 0.0 }, 0, 0, 2)]
    [InlineData(new[] { 1e-12, -1e-12 }, 0, 0, 2)]  // inside epsilon: a tie
    public void Tally_CountsWinsLossesAndTies(double[] deltas, int bWins, int aWins, int ties)
    {
        var tally = AbStatistics.Tally(deltas, epsilon: 1e-9);

        Assert.Equal(bWins, tally.BWins);
        Assert.Equal(aWins, tally.AWins);
        Assert.Equal(ties, tally.Ties);
    }

    [Fact]
    public void Tally_CountsSumToThePairCount()
    {
        double[] deltas = [0.4, -0.2, 0.0, 0.9, -1e-11];

        var tally = AbStatistics.Tally(deltas, epsilon: 1e-9);

        Assert.Equal(deltas.Length, tally.BWins + tally.AWins + tally.Ties);
    }

    [Fact]
    public void Tally_NoPairs_IsAllZero()
    {
        var tally = AbStatistics.Tally([], epsilon: 1e-9);

        Assert.Equal(0, tally.BWins);
        Assert.Equal(0, tally.AWins);
        Assert.Equal(0, tally.Ties);
    }

    [Fact]
    public void Tally_NegativeEpsilon_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AbStatistics.Tally([0.1], epsilon: -1e-9));
}
