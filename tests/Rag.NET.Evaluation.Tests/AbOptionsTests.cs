using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Internal;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

/// <summary>
/// The options validate themselves, so a bad value fails where it was written.
/// </summary>
/// <remarks>
/// <see cref="AbStatistics.BootstrapMeanDeltaCi"/> already refuses a too-small resample count, but
/// that throw arrives during the comparison and names <c>resamples</c> — a parameter the caller
/// never wrote. Validating in the initialiser points at the property they did.
/// </remarks>
public sealed class AbOptionsTests
{
    [Fact]
    public void Defaults_AreTheOnesTheDesignSettledOn()
    {
        var options = new AbOptions();

        Assert.Null(options.Seed);
        Assert.Equal(2000, options.BootstrapResamples);
        Assert.Equal(1e-9, options.TieEpsilon, precision: 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(999)]
    public void BootstrapResamples_BelowTheMinimum_ThrowsNamingTheProperty(int resamples)
    {
        // Below 40 the 2.5% trim floors to zero and the interval becomes the min-max of the
        // draws while still being labelled 95% — it under-covers, so it excludes zero, so it
        // names winners that are not there. Refused rather than silently allowed.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AbOptions { BootstrapResamples = resamples });

        // MA0015 requires paramName to be a real parameter, and an init accessor's is "value" —
        // so the property the caller actually wrote is named in the message instead.
        Assert.Contains(nameof(AbOptions.BootstrapResamples), exception.Message, StringComparison.Ordinal);
        Assert.Equal(resamples, exception.ActualValue);
    }

    [Fact]
    public void BootstrapResamples_AtTheMinimum_IsAccepted()
        => Assert.Equal(
            AbStatistics.MinimumResamples,
            new AbOptions { BootstrapResamples = AbStatistics.MinimumResamples }.BootstrapResamples);

    [Fact]
    public void TieEpsilon_Negative_ThrowsNamingTheProperty()
    {
        // A negative band is not a narrower tie rule; it inverts one, putting exactly-equal
        // samples into a win column.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AbOptions { TieEpsilon = -1e-9 });

        Assert.Contains(nameof(AbOptions.TieEpsilon), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TieEpsilon_Zero_IsAcceptedAsExactEqualityOnly()
        => Assert.Equal(0.0, new AbOptions { TieEpsilon = 0.0 }.TieEpsilon, precision: 12);
}
