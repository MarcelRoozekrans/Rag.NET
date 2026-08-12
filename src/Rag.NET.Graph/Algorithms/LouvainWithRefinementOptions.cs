namespace Rag.NET.Graph.Algorithms;

/// <summary>Options for the <see cref="LouvainWithRefinement"/> community detection algorithm.</summary>
/// <remarks>
/// Named <c>LeidenOptions</c> until the algorithm it configures was measured against the Leiden
/// paper and found not to be it — see <see cref="LouvainWithRefinement"/>'s remarks. The old name
/// survives as an obsolete record deriving from this one, so a caller who still passes a
/// <see cref="LeidenOptions"/> gets a deprecation warning rather than a broken build. That
/// derivation is why this type is not sealed.
/// </remarks>
public record class LouvainWithRefinementOptions
{
    /// <summary>Resolution parameter — higher values produce more, smaller communities. Default: 1.0.</summary>
    public double Resolution { get; set; } = 1.0;

    /// <summary>Maximum iterations per level. Default: 10.</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>Maximum hierarchy levels. Null = until no improvement. Default: null.</summary>
    public int? MaxLevels { get; set; }

    /// <summary>Random seed for deterministic results. Default: 42.</summary>
    public int RandomSeed { get; set; } = 42;
}
