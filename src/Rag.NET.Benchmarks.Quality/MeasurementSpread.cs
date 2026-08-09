using System.Globalization;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The run-to-run spread of one measured cost figure: the smallest and largest value any repeat
/// run measured, and their ratio. Every figure <see cref="CostReproducibility"/> reports travels
/// as one of these rather than as a single number, because the honest published figure is the
/// range — a lone number chosen from repeats that disagreed is a claim the data does not make.
/// </summary>
public sealed record MeasurementSpread
{
    /// <summary>Creates a spread from the extremes the repeat runs measured.</summary>
    /// <param name="minimum">The smallest value any repeat measured.</param>
    /// <param name="maximum">The largest value any repeat measured.</param>
    /// <exception cref="ArgumentException">
    /// Either value is negative or non-finite — no clock runs backwards, and a NaN endpoint would
    /// make <see cref="Ratio"/> NaN, which compares false against every bar and so would pass the
    /// reproducibility gate by not comparing — or the minimum exceeds the maximum, which would
    /// report a ratio below 1: better-than-perfect reproducibility, sliding under any bar.
    /// </exception>
    public MeasurementSpread(double minimum, double maximum)
    {
        RequireMeasured(minimum, "minimum");
        RequireMeasured(maximum, "maximum");
        if (minimum > maximum)
        {
            throw new ArgumentException(
                $"The minimum {minimum.ToString(CultureInfo.InvariantCulture)} exceeds the " +
                $"maximum {maximum.ToString(CultureInfo.InvariantCulture)}. A swapped pair would " +
                "report a ratio below 1 — better-than-perfect reproducibility — and slide under " +
                "every bar.",
                nameof(minimum));
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the smallest value any repeat run measured.</summary>
    public double Minimum { get; }

    /// <summary>Gets the largest value any repeat run measured.</summary>
    public double Maximum { get; }

    /// <summary>
    /// Gets how far apart the repeats were: <see cref="Maximum"/> over <see cref="Minimum"/>, so 1
    /// is perfect reproduction and the 23x embedding-cache defect reads as 23. Equal endpoints are
    /// exactly 1 — including a pair of zeros, which is a perfectly reproduced measurement and not
    /// 0/0 — and a zero minimum under a positive maximum is positive infinity, because a span that
    /// measured nothing beside one that measured something fails any finite bar.
    /// </summary>
    public double Ratio => Maximum.Equals(Minimum) ? 1 : Maximum / Minimum;

    private static void RequireMeasured(double value, string what)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException(
                $"The {what} is {value.ToString(CultureInfo.InvariantCulture)}, which is not a " +
                "non-negative finite measurement. No clock runs backwards, and a non-finite " +
                "endpoint would make the ratio NaN — which compares false against every bar, so " +
                "the gate would pass by not comparing.",
                what);
        }
    }
}
