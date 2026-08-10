using ZeroAlloc.Validation;

namespace Rag.NET.Raptor;

/// <summary>Configuration for the RAPTOR retrieval behavior.</summary>
[Validate]
public sealed class RaptorRetrievalOptions
{
    /// <summary>Retrieval mode. Default: Blend.</summary>
    public RaptorRetrievalMode Mode { get; set; } = RaptorRetrievalMode.Blend;

    /// <summary>
    /// Score multiplier for summary chunks in Boost mode. Default: 1.2.
    /// <para>
    /// Must be greater than 0, and finite — enforced by the validation attributes, which
    /// <c>UseRaptor</c> runs through the generated <c>RaptorRetrievalOptionsValidator</c> at
    /// registration. <c>RaptorRetrievalBehavior.ApplyBoost</c> multiplies every summary
    /// chunk's score by this factor, so zero buries all summaries at the bottom of the
    /// ranking and a negative factor inverts their order — the opposite of what Boost mode
    /// exists to do, silently.
    /// </para>
    /// </summary>
    [GreaterThan(0.0)]
    [Must(nameof(SummaryBoostFactorIsFinite), Message = "SummaryBoostFactor must be a finite number (not NaN or infinity).")]
    public double SummaryBoostFactor { get; set; } = 1.2;

    /// <summary>Reports whether <see cref="SummaryBoostFactor"/> is a finite number.</summary>
    /// <param name="value">The <see cref="SummaryBoostFactor"/> value under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool SummaryBoostFactorIsFinite(double value) => double.IsFinite(value);

    /// <summary>
    /// Minimum RAPTOR level to include in Filter mode. Null = no lower bound.
    /// <para>
    /// When both bounds are set, this must not exceed <see cref="MaxRaptorLevel"/> — enforced
    /// by <see cref="ValidateLevelWindow"/> through the generated
    /// <c>RaptorRetrievalOptionsValidator</c>, which <c>UseRaptor</c> runs at registration.
    /// An empty window (<c>Min &gt; Max</c>) fails every result against one bound or the
    /// other, so Filter mode would remove every result on every retrieval.
    /// </para>
    /// </summary>
    public int? MinRaptorLevel { get; set; }

    /// <summary>
    /// Maximum RAPTOR level to include in Filter mode. Null = no upper bound.
    /// <para>
    /// When set, must be zero or positive — enforced by the validation attribute
    /// (<see langword="null"/> passes). RAPTOR levels are never negative — leaf chunks are
    /// level 0 and summaries count up from 1 — so a negative upper bound excludes every
    /// result in Filter mode.
    /// </para>
    /// </summary>
    [GreaterThanOrEqualTo(0, When = nameof(MaxRaptorLevelIsSet))]
    public int? MaxRaptorLevel { get; set; }

    /// <summary>Reports whether <see cref="MaxRaptorLevel"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="MaxRaptorLevel"/> has a value.</returns>
    internal bool MaxRaptorLevelIsSet() => MaxRaptorLevel is not null;

    /// <summary>
    /// Reports the two Filter-mode bounds being individually valid but describing an empty
    /// window. A <c>[CustomValidation]</c> method rather than a check the caller has to
    /// remember: it runs inside the generated <c>RaptorRetrievalOptionsValidator</c>, so every
    /// caller of that validator enforces it — the same shape as
    /// <c>ChunkingOptions.ValidateOverlapFitsChunk</c>, and for the same reason.
    /// </summary>
    /// <returns>One failure when both bounds are set and <see cref="MinRaptorLevel"/> exceeds <see cref="MaxRaptorLevel"/>.</returns>
    // Fully qualified: Rag.NET.Models.ValidationFailure is a different type that can be in
    // scope here, and the generator's ZV0013 rejects the method outright if the return type
    // resolves to it.
    [CustomValidation]
    public IEnumerable<ZeroAlloc.Validation.ValidationFailure> ValidateLevelWindow()
    {
        if (MinRaptorLevel is { } min && MaxRaptorLevel is { } max && min > max)
        {
            yield return new ZeroAlloc.Validation.ValidationFailure
            {
                PropertyName = nameof(MinRaptorLevel),
                ErrorMessage =
                    $"MinRaptorLevel ({min}) must not exceed MaxRaptorLevel ({max}). " +
                    "Filter mode drops results below MinRaptorLevel and above MaxRaptorLevel, " +
                    "so an empty window removes every result on every retrieval.",
            };
        }
    }
}
