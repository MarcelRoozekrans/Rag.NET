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

        var first = AbStatistics.BootstrapMeanDeltaCi(deltas, 1000, new Random(99));
        var second = AbStatistics.BootstrapMeanDeltaCi(deltas, 1000, new Random(99));

        // An unreproducible confidence interval is not evidence. Same rule as the dataset seed.
        //
        // Note what this does *not* say: it compares the function to itself, which holds for any
        // deterministic function of the seed — including one trimmed to the wrong percentile. The
        // confidence level is pinned by BootstrapCi_HalfWidthMatchesTheAnalytic95PercentInterval
        // below, not here.
        Assert.Equal(first!.Value.Lower, second!.Value.Lower, precision: 12);
        Assert.Equal(first.Value.Upper, second.Value.Upper, precision: 12);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(2024)]
    public void BootstrapCi_HalfWidthMatchesTheAnalytic95PercentInterval(int seed)
    {
        // Evenly spaced over [-0.5, +0.5]: a known, non-degenerate spread, and enough pairs that the
        // bootstrap distribution of the mean is near-normal by the central limit theorem — which is
        // what makes the analytic half-width below the right thing to compare against.
        var deltas = new double[400];
        for (var i = 0; i < deltas.Length; i++)
            deltas[i] = -0.5 + (i / (double)(deltas.Length - 1));

        var ci = AbStatistics.BootstrapMeanDeltaCi(deltas, resamples: 4000, new Random(seed))!.Value;

        // This is the test that pins the *confidence level*, and the only one that does. Every other
        // assertion in this file — reproducibility, spanning zero on noise, narrowing with n,
        // bracketing the mean — passes just as happily against an interval trimmed to 70%, which
        // yields z = 1.04 instead of 1.96 and declares a finding on pure noise about five times too
        // often. A CI that is wrong only in its width is invisible to every property except its
        // width, so the width is asserted against the textbook value rather than against itself.
        var expected = 1.96 * PopulationStandardDeviation(deltas) / Math.Sqrt(deltas.Length);
        var actual = (ci.Upper - ci.Lower) / 2;

        // ±25% is loose enough to absorb the Monte-Carlo jitter of two order statistics and any
        // harmless change to how the resamples are drawn, and far tighter than the 47% shortfall a
        // 70% interval produces.
        Assert.InRange(actual, expected * 0.75, expected * 1.25);
    }

    private static double PopulationStandardDeviation(double[] values)
    {
        var mean = AbStatistics.MeanDelta(values)!.Value;
        var sumOfSquares = 0.0;

        foreach (var value in values)
        {
            var deviation = value - mean;
            sumOfSquares += deviation * deviation;
        }

        // Divisor n, not n - 1: the bootstrap resamples the observed deltas as if they were the
        // population, so the spread it reproduces is the population spread of that array.
        return Math.Sqrt(sumOfSquares / values.Length);
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
        => Assert.Null(AbStatistics.BootstrapMeanDeltaCi([], 1000, new Random(1)));

    [Fact]
    public void BootstrapCi_OnePair_IsDegenerateNotAnError()
    {
        // Every resample of a single value is that value. The interval collapses to a point, which
        // is honest — one sample supports no interval — rather than an exception or a fabricated
        // width.
        var ci = AbStatistics.BootstrapMeanDeltaCi([0.3], 1000, new Random(1))!.Value;

        Assert.Equal(0.3, ci.Lower, precision: 10);
        Assert.Equal(0.3, ci.Upper, precision: 10);
    }

    [Fact]
    public void BootstrapCi_IdenticalDeltas_CollapseToAPoint()
    {
        double[] deltas = [0.25, 0.25, 0.25, 0.25, 0.25];

        var ci = AbStatistics.BootstrapMeanDeltaCi(deltas, 1000, new Random(5))!.Value;

        Assert.Equal(0.25, ci.Lower, precision: 10);
        Assert.Equal(0.25, ci.Upper, precision: 10);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]   // the cliff: floor(0.025 * 39) is 0, so nothing would be trimmed at all
    [InlineData(40)]
    [InlineData(999)]
    public void BootstrapCi_ResamplesBelowTheFloor_Throws(int resamples)
    {
        // Below 40, trim floors to zero and the "95%" interval is really the min and max of the
        // draws — expected coverage (B-1)/(B+1), so 0.818 at B=10. It under-covers, which excludes
        // zero more often than it should, which manufactures winners. Refused rather than allowed.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = AbStatistics.BootstrapMeanDeltaCi([0.1, 0.2], resamples, new Random(1)); });
    }

    [Fact]
    public void BootstrapCi_ResamplesAtTheFloor_IsAccepted()
        => Assert.NotNull(AbStatistics.BootstrapMeanDeltaCi(
            [0.1, 0.2],
            AbStatistics.MinimumResamples,
            new Random(1)));

    [Theory]
    // sorted values (ms)                            p     expected (ms)
    [InlineData(new long[] { 10, 20, 30, 40 }, 0.50, 20)]  // even length: rank ceil(0.5*4) = 2
    [InlineData(new long[] { 10, 20 }, 0.50, 10)]          // ... so the lower middle, not 15
    [InlineData(new long[] { 10, 20, 30 }, 0.50, 20)]      // odd length: the true middle
    [InlineData(new long[] { 10, 20, 30, 40 }, 0.95, 40)]  // rank ceil(0.95*4) = 4
    [InlineData(new long[] { 10, 20, 30 }, 0.95, 30)]
    [InlineData(new long[] { 7 }, 0.50, 7)]                // one sample is its own every percentile
    [InlineData(new long[] { 7 }, 0.95, 7)]
    [InlineData(new long[] { 5, 5, 5, 900 }, 0.50, 5)]     // an outlier does not move p50
    [InlineData(new long[] { 5, 5, 5, 900 }, 0.95, 900)]
    public void Percentile_TakesTheValueAtTheNearestRank(
        long[] sortedMilliseconds,
        double percentile,
        long expectedMilliseconds)
    {
        // The convention is load-bearing and invisible: p50 and p95 of two variants both look
        // plausible whatever rank they came from, so nothing but a table pins which one it is.
        var actual = AbStatistics.Percentile(Latencies(sortedMilliseconds), percentile);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), actual!.Value);
    }

    [Fact]
    public void Percentile_OnTwentySamples_P95IsTheNineteenthNotTheLargest()
    {
        // ceil(0.95 * 20) = 19, so p95 is the 19th of 20 and the slowest call is *not* it. On the
        // small arrays above p95 and the maximum coincide, which would let a rank that is merely
        // "near the top" pass; here they are two different values.
        var sorted = new TimeSpan[20];
        for (var i = 0; i < sorted.Length; i++)
            sorted[i] = TimeSpan.FromMilliseconds(i + 1);

        Assert.Equal(TimeSpan.FromMilliseconds(19), AbStatistics.Percentile(sorted, 0.95)!.Value);
        Assert.Equal(TimeSpan.FromMilliseconds(10), AbStatistics.Percentile(sorted, 0.50)!.Value);
    }

    [Fact]
    public void Percentile_NeverInterpolatesBetweenTwoSamples()
    {
        // Every latency in the report has to be a wall-clock some call really took. The midpoint of
        // 20 ms and 40 ms is 30 ms, and no call took 30 ms — an averaging or interpolating
        // implementation would print it anyway, and it would look entirely reasonable.
        TimeSpan[] sorted = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)];

        for (var p = 0; p <= 100; p++)
        {
            var value = AbStatistics.Percentile(sorted, p / 100.0)!.Value;

            Assert.Contains(value, sorted);
        }
    }

    [Fact]
    public void Percentile_NoSamples_IsNull()
    {
        // Not TimeSpan.Zero: an instantaneous variant and a variant nothing was measured on are
        // very different reports.
        Assert.Null(AbStatistics.Percentile([], 0.50));
    }

    private static TimeSpan[] Latencies(long[] milliseconds)
    {
        var latencies = new TimeSpan[milliseconds.Length];
        for (var i = 0; i < milliseconds.Length; i++)
            latencies[i] = TimeSpan.FromMilliseconds(milliseconds[i]);

        return latencies;
    }
}
