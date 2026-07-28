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

    [Fact]
    public void BootstrapCi_DistinguishesARealShiftFromNoise()
    {
        // Noise: deltas centred on zero. The interval must span zero.
        var noise = new double[40];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (i % 2 == 0) ? 0.05 : -0.05;

        // A real shift: every sample moved the same way.
        var shift = new double[40];
        for (var i = 0; i < shift.Length; i++)
            shift[i] = (i % 2 == 0) ? 0.10 : 0.04;

        var noiseCi = AbStatistics.BootstrapMeanDeltaCi(noise, resamples: 2000, new Random(7))!.Value;
        var shiftCi = AbStatistics.BootstrapMeanDeltaCi(shift, resamples: 2000, new Random(7))!.Value;

        // This is the whole point of the interval: without it, both of these report a winner.
        Assert.True(noiseCi.Lower < 0 && noiseCi.Upper > 0, $"noise CI [{noiseCi.Lower}, {noiseCi.Upper}] should span zero");
        Assert.True(shiftCi.Lower > 0, $"shift CI [{shiftCi.Lower}, {shiftCi.Upper}] should exclude zero");
    }

    [Fact]
    public void BootstrapCi_SameSeed_IsReproducible()
    {
        double[] deltas = [0.1, -0.05, 0.2, 0.0, 0.15];

        var first = AbStatistics.BootstrapMeanDeltaCi(deltas, 500, new Random(99));
        var second = AbStatistics.BootstrapMeanDeltaCi(deltas, 500, new Random(99));

        // An unreproducible confidence interval is not evidence. Same rule as the dataset seed.
        Assert.Equal(first!.Value.Lower, second!.Value.Lower, precision: 12);
        Assert.Equal(first.Value.Upper, second.Value.Upper, precision: 12);
    }

    [Fact]
    public void BootstrapCi_MoreSamplesNarrowTheInterval()
    {
        var few = new double[10];
        var many = new double[200];
        for (var i = 0; i < few.Length; i++)
            few[i] = (i % 2 == 0) ? 0.10 : 0.02;
        for (var i = 0; i < many.Length; i++)
            many[i] = (i % 2 == 0) ? 0.10 : 0.02;

        var fewCi = AbStatistics.BootstrapMeanDeltaCi(few, 2000, new Random(3))!.Value;
        var manyCi = AbStatistics.BootstrapMeanDeltaCi(many, 2000, new Random(3))!.Value;

        Assert.True(manyCi.Upper - manyCi.Lower < fewCi.Upper - fewCi.Lower);
    }

    [Fact]
    public void BootstrapCi_ContainsTheObservedMean()
    {
        double[] deltas = [0.10, 0.02, 0.08, 0.04, 0.06, 0.12, 0.01, 0.09];

        var mean = AbStatistics.MeanDelta(deltas)!.Value;
        var ci = AbStatistics.BootstrapMeanDeltaCi(deltas, 2000, new Random(11))!.Value;

        // A percentile bootstrap brackets the statistic it resamples. An interval that did not
        // would mean the resampling loop is not computing the mean it claims to.
        Assert.InRange(mean, ci.Lower, ci.Upper);
    }

    [Fact]
    public void BootstrapCi_NoPairs_IsNull()
        => Assert.Null(AbStatistics.BootstrapMeanDeltaCi([], 100, new Random(1)));

    [Fact]
    public void BootstrapCi_OnePair_IsDegenerateNotAnError()
    {
        // Every resample of a single value is that value. The interval collapses to a point, which
        // is honest — one sample supports no interval — rather than an exception or a fabricated
        // width.
        var ci = AbStatistics.BootstrapMeanDeltaCi([0.3], 100, new Random(1))!.Value;

        Assert.Equal(0.3, ci.Lower, precision: 10);
        Assert.Equal(0.3, ci.Upper, precision: 10);
    }

    [Fact]
    public void BootstrapCi_IdenticalDeltas_CollapseToAPoint()
    {
        double[] deltas = [0.25, 0.25, 0.25, 0.25, 0.25];

        var ci = AbStatistics.BootstrapMeanDeltaCi(deltas, 500, new Random(5))!.Value;

        Assert.Equal(0.25, ci.Lower, precision: 10);
        Assert.Equal(0.25, ci.Upper, precision: 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BootstrapCi_NonPositiveResamples_Throws(int resamples)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = AbStatistics.BootstrapMeanDeltaCi([0.1, 0.2], resamples, new Random(1)); });
}
