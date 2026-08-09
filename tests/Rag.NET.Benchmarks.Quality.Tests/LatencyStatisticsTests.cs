using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="LatencyStatistics"/> against arithmetic a reader can redo by counting on the
/// sorted samples, per <see cref="IrMetricsTests"/>' convention.
/// <para>
/// The definition is what these tests exist to hold still. "p50" and "p99" each name a family of
/// estimators that disagree by a whole sample on small inputs, and the disagreements are exactly
/// the size of the differences a cost table would report between entrants. So the arrays below are
/// short enough that the answer can be read off by eye, and each case names the alternative
/// definition it would fail under: the interpolated median, and the p99-as-maximum reading.
/// </para>
/// </summary>
public sealed class LatencyStatisticsTests
{
    /// <summary>Ten through forty, already sorted, so the nearest rank can be counted off.</summary>
    private static readonly double[] Four = [10, 20, 30, 40];

    [Fact]
    public void Percentile_P50OfFourSamplesIsTheSecond_NotTheMidpointOfTheMiddleTwo()
    {
        // ceil(0.50 x 4) = 2, so the second smallest: 20. The interpolated median of
        // [10, 20, 30, 40] is 25 — a value no query ever took. Nearest rank only ever reports a
        // latency that was actually observed, which is the property that makes a published cell
        // checkable against the raw samples beside it.
        Assert.Equal(20, LatencyStatistics.Percentile(Four, 50));
    }

    [Fact]
    public void Percentile_P25OfFourSamplesLandsExactlyOnTheFirstIndex()
    {
        // ceil(0.25 x 4) = 1 exactly, no rounding up: the smallest sample.
        Assert.Equal(10, LatencyStatistics.Percentile(Four, 25));
    }

    [Fact]
    public void Percentile_ARankBetweenTwoIndicesRoundsUp()
    {
        // Three samples, p50: ceil(0.50 x 3) = ceil(1.5) = 2, so the middle sample. Rounding down
        // would report the smallest of three as the median.
        Assert.Equal(2, LatencyStatistics.Percentile([1.0, 2.0, 3.0], 50));
    }

    [Fact]
    public void Percentile_OneSampleIsThatSampleAtEveryPercentile()
    {
        Assert.Equal(7, LatencyStatistics.Percentile([7.0], 50));
        Assert.Equal(7, LatencyStatistics.Percentile([7.0], 99));
        Assert.Equal(7, LatencyStatistics.Percentile([7.0], 100));
    }

    [Fact]
    public void Percentile_TwoSamplesPutP50OnTheLowerAndP99OnTheHigher()
    {
        // ceil(0.50 x 2) = 1 and ceil(0.99 x 2) = 2. The p50 of two samples is the faster of them,
        // not their average — the same no-interpolation rule, at the smallest size where it bites.
        Assert.Equal(3, LatencyStatistics.Percentile([9.0, 3.0], 50));
        Assert.Equal(9, LatencyStatistics.Percentile([9.0, 3.0], 99));
    }

    [Fact]
    public void Percentile_ExactlyOneHundredSamplesPutP99OnTheNinetyNinth_NotTheMaximum()
    {
        // ceil(0.99 x 100) = 99, so the 99th of 100 — the first sample count at which p99 stops
        // being the maximum. This is the boundary the doc comment names, pinned so a future change
        // to the definition cannot pass unnoticed.
        var samples = Ascending(100);

        Assert.Equal(99, LatencyStatistics.Percentile(samples, 99));
        Assert.Equal(100, LatencyStatistics.Percentile(samples, 100));
    }

    [Fact]
    public void Percentile_FewerThanOneHundredSamplesPutP99OnTheMaximum()
    {
        // ceil(0.99 x 99) = 99, the last of 99. Below one hundred samples p99 IS the single worst
        // observation rather than an estimate of a tail, and a table quoting it as a tail figure
        // would be quoting one query.
        var samples = Ascending(99);

        Assert.Equal(99, LatencyStatistics.Percentile(samples, 99));
        Assert.Equal(LatencyStatistics.Percentile(samples, 100), LatencyStatistics.Percentile(samples, 99));
    }

    [Fact]
    public void Percentile_ManySamplesPutP99BelowTheMaximum()
    {
        // ceil(0.99 x 300) = 297 of 300 — SciFact's judged query count, the size these actually
        // run at, where p99 is a tail figure rather than a single worst case.
        var samples = Ascending(300);

        Assert.Equal(297, LatencyStatistics.Percentile(samples, 99));
    }

    [Fact]
    public void Percentile_IsIndifferentToInputOrder()
    {
        Assert.Equal(
            LatencyStatistics.Percentile([40.0, 10.0, 30.0, 20.0], 50),
            LatencyStatistics.Percentile(Four, 50));
    }

    [Fact]
    public void Percentile_DoesNotReorderTheCallersSamples()
    {
        // The samples come out of a sidecar the caller may still be reading; sorting in place
        // would leave whatever it does next reading a permuted map.
        var samples = new[] { 40.0, 10.0, 30.0, 20.0 };

        _ = LatencyStatistics.Percentile(samples, 50);

        Assert.Equal([40.0, 10.0, 30.0, 20.0], samples);
    }

    [Fact]
    public void Percentile_RejectsAnEmptySampleSet()
    {
        // A percentile over nothing has no rank to name. Returning 0 ms would be the best cell in
        // the table, produced by an entrant that measured nothing.
        _ = Assert.Throws<ArgumentException>(() => LatencyStatistics.Percentile([], 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.5)]
    [InlineData(double.NaN)]
    public void Percentile_RejectsAPercentileOutsideItsRange(double percentile)
    {
        // Nearest rank is ceil(p/100 x n), which is 0 — no sample — at p = 0, so the range is
        // (0, 100]. A 0 asked for by mistake would otherwise index off the front of the array.
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            LatencyStatistics.Percentile(Four, percentile));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void Percentile_RejectsANonFiniteOrNegativeSample(double sample)
    {
        // NaN is the dangerous one: it does not compare, so a sort can leave it anywhere and it
        // would silently become whichever percentile it landed on rather than failing.
        _ = Assert.Throws<ArgumentException>(() =>
            LatencyStatistics.Percentile([1.0, sample, 3.0], 50));
    }

    [Fact]
    public void From_ReportsTheSampleCountWithP50AndP99()
    {
        var statistics = LatencyStatistics.From(Four);

        Assert.Equal(4, statistics.SampleCount);
        Assert.Equal(20, statistics.P50Milliseconds);
        Assert.Equal(40, statistics.P99Milliseconds);
    }

    [Fact]
    public void From_AgreesWithPercentileSoOneDefinitionIsInPlay()
    {
        // From sorts once and reads both ranks off the same array; Percentile sorts per call. They
        // must not be allowed to drift into two definitions of the same word.
        var samples = Ascending(300);

        var statistics = LatencyStatistics.From(samples);

        Assert.Equal(LatencyStatistics.Percentile(samples, 50), statistics.P50Milliseconds);
        Assert.Equal(LatencyStatistics.Percentile(samples, 99), statistics.P99Milliseconds);
    }

    [Fact]
    public void From_ReportsTheSampleCountSoASmallRunIsVisible()
    {
        // The count is on the result because p99 means something different below one hundred
        // samples, and the reader of a row needs to be able to tell which it is holding.
        Assert.Equal(1, LatencyStatistics.From([7.0]).SampleCount);
    }

    [Fact]
    public void From_RejectsAnEmptySampleSet()
    {
        _ = Assert.Throws<ArgumentException>(() => LatencyStatistics.From([]));
    }

    /// <summary>The samples 1..<paramref name="count"/>, so a rank equals the value at it.</summary>
    private static double[] Ascending(int count)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = i + 1;
        }

        return samples;
    }
}
