namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// What <see cref="ShadowRagPipeline"/> samples and how the two sides of every captured pair are
/// named.
/// </summary>
/// <remarks>
/// The offline scorer keys everything by variant name, so both labels must be non-blank and the
/// two must differ. Blankness is refused here, where the option is set; distinctness is refused
/// by the decorator's constructor, which is the first place that sees both. The properties are
/// settable rather than init-only so a <c>UseShadow(o =&gt; ...)</c> configure delegate can
/// assign them.
/// </remarks>
public sealed class ShadowPipelineOptions
{
    /// <summary>
    /// The share of <c>AskAsync</c> calls that are shadowed, in [0, 1]. Default <c>0.0</c>:
    /// registering shadow mode does nothing until someone deliberately sets a rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every sampled request roughly doubles that request's pipeline spend.</b> The secondary
    /// runs the same question through a second, complete pipeline — its own retrieval and its own
    /// LLM call — so a rate of 0.05 adds about 5% × 2× to the bill, out-of-band but very much
    /// real. That is why the default is zero and why an out-of-range value is refused rather than
    /// clamped: a clamped rate is a rate nobody chose.
    /// </para>
    /// <para>
    /// Sampling does not break the comparison: the unit is a request, and a sampled request runs
    /// both pipelines over the same question, so the captured pairs stay paired.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a number in [0, 1].</exception>
    public double SampleRate
    {
        get => _sampleRate;
        set
        {
            if (double.IsNaN(value) || value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"{nameof(SampleRate)} must be between 0 and 1 inclusive. It is refused rather " +
                    "than clamped: every sampled request roughly doubles that request's spend, so " +
                    "the rate must be a number somebody actually chose.");
            }

            _sampleRate = value;
        }
    }

    /// <summary>The label for the side the caller is served. Default <c>"primary"</c>.</summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    public string PrimaryVariantName
    {
        get => _primaryVariantName;
        set => _primaryVariantName = RequireName(value, nameof(PrimaryVariantName));
    }

    /// <summary>The label for the shadowed side. Default <c>"shadow"</c>.</summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    public string SecondaryVariantName
    {
        get => _secondaryVariantName;
        set => _secondaryVariantName = RequireName(value, nameof(SecondaryVariantName));
    }

    private double _sampleRate;
    private string _primaryVariantName = "primary";
    private string _secondaryVariantName = "shadow";

    private static string RequireName(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{propertyName} must be non-blank; the offline scorer keys everything by variant name.",
                nameof(value));
        }

        return value;
    }
}
