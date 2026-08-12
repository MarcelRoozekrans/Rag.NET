namespace Rag.NET.Graph.Algorithms;

/// <summary>Options for the <see cref="LouvainWithRefinement"/> community detection algorithm.</summary>
/// <remarks>
/// Named <c>LeidenOptions</c> until the algorithm it configures was measured against the Leiden
/// paper — see <see cref="LouvainWithRefinement"/>'s remarks. The old name survives as an obsolete
/// record deriving from this one, so a caller who still passes a <see cref="LeidenOptions"/> gets a
/// deprecation warning rather than a broken build. <b>That derivation is why this type is not
/// sealed, and it must stay unsealed for as long as <see cref="LeidenOptions"/> exists.</b>
/// </remarks>
public record class LouvainWithRefinementOptions
{
    /// <summary>The value <see cref="Randomness"/> takes when nothing sets it.</summary>
    /// <remarks>
    /// The value Traag, Waltman and van Eck use throughout their own experiments — "we used a value
    /// of 0.01 for the parameter θ" — rather than a number picked here.
    /// </remarks>
    private const double DefaultRandomness = 0.01;

    private double _randomness = DefaultRandomness;

    /// <summary>Resolution parameter — higher values produce more, smaller communities. Default: 1.0.</summary>
    public double Resolution { get; set; } = 1.0;

    /// <summary>Maximum iterations per level. Default: 10.</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>Maximum hierarchy levels. Null = until no improvement. Default: null.</summary>
    public int? MaxLevels { get; set; }

    /// <summary>Random seed for deterministic results. Default: 42.</summary>
    public int RandomSeed { get; set; } = 42;

    /// <summary>
    /// θ — how much randomness the refinement phase uses when it picks which sub-community a node
    /// joins. Must be finite and greater than zero. Default: 0.01.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refinement deliberately does not take the best merge, and that is one of the three
    /// things the well-connectedness guarantee is made of.</b> Among the sub-communities a node may
    /// legally join, one is drawn at random with probability proportional to
    /// <c>exp(ΔQ / θ)</c> over those whose modularity increase <c>ΔQ</c> is non-negative — staying
    /// put being one of them, at <c>ΔQ = 0</c>. Always taking the largest gain would make the
    /// refinement a second greedy pass that reproduces the local-moving phase's blind spots instead
    /// of breaking them up.
    /// </para>
    /// <para>
    /// <b>What a value means.</b> θ is measured in the same units as the modularity increase, so it
    /// is the size of a quality difference the draw is allowed to ignore. Small values concentrate
    /// the draw on the best candidate and approach the greedy behaviour; large values flatten it
    /// toward uniform over everything non-negative, which explores more sub-partitions at the cost
    /// of quality. 0.01 is what the paper used. Below roughly 1e-4 the draw is deterministic in all
    /// but name; much above 1 the refinement stops preferring good merges at all.
    /// </para>
    /// <para>
    /// <b>Randomised does not mean unreproducible.</b> Every draw comes from a single
    /// <see cref="Random"/> seeded with <see cref="RandomSeed"/>, so a fixed seed gives a fixed
    /// partition, and <c>LouvainWithRefinementTests.Detect_IsDeterministicWithSameSeed</c> asserts
    /// it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not finite, or is zero or negative. Validated here rather than where the draw
    /// happens, so the exception names the property that was set instead of surfacing as a division
    /// by zero — or as a silent <c>NaN</c> weight that makes every candidate unreachable — from
    /// inside a refinement pass the caller never wrote.
    /// </exception>
    public double Randomness
    {
        get => _randomness;
        set
        {
            if (!double.IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"{nameof(Randomness)} must be a finite number greater than 0. It divides the " +
                    "quality increase inside exp(ΔQ / θ), so zero is a division by zero and a " +
                    "negative value inverts the draw onto the worst candidate available.");
            }

            _randomness = value;
        }
    }
}
