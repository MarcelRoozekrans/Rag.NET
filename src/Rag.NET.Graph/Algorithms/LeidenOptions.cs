namespace Rag.NET.Graph.Algorithms;

/// <summary>Options for the Leiden community detection algorithm.</summary>
public sealed record class LeidenOptions
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
