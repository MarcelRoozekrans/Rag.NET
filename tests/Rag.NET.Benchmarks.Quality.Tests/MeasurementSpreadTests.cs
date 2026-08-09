using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="MeasurementSpread"/> — the min/max/ratio triple every published cost figure
/// carries so its run-to-run variation travels with it instead of hiding behind one number.
/// </summary>
public sealed class MeasurementSpreadTests
{
    [Fact]
    public void Ratio_IsTheMaximumOverTheMinimum()
    {
        Assert.Equal(2.5, new MeasurementSpread(2, 5).Ratio);
    }

    [Theory]
    [InlineData(2.4)]
    [InlineData(0)]
    public void Ratio_OfEqualValues_IsExactlyOne(double value)
    {
        // Including zero: a pair of 0-second measurements is perfectly reproduced, not 0/0.
        Assert.Equal(1, new MeasurementSpread(value, value).Ratio);
    }

    [Fact]
    public void Ratio_OfAZeroMinimumAgainstAPositiveMaximum_IsInfinite()
    {
        // A 0 measured beside a 2.4 is not noise around a common value — it is a span that
        // did not measure the same work, and an infinite ratio fails any finite bar.
        Assert.Equal(double.PositiveInfinity, new MeasurementSpread(0, 2.4).Ratio);
    }

    [Fact]
    public void AMinimumAboveTheMaximum_IsRefused()
    {
        // A swapped pair would report a ratio below 1, which reads as better-than-perfect
        // reproducibility and slides under every bar.
        _ = Assert.Throws<ArgumentException>(() => new MeasurementSpread(5, 2));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(double.NaN, 2)]
    [InlineData(1, double.PositiveInfinity)]
    public void ANegativeOrNonFiniteValue_IsRefused(double minimum, double maximum)
    {
        // No clock runs backwards, and a NaN endpoint would make the ratio NaN — which compares
        // false against every bar and so would pass the gate by not comparing.
        _ = Assert.Throws<ArgumentException>(() => new MeasurementSpread(minimum, maximum));
    }
}
