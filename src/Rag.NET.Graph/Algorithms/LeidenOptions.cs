namespace Rag.NET.Graph.Algorithms;

/// <summary>
/// The former name of <see cref="LouvainWithRefinementOptions"/>, kept as a forwarder so callers on
/// 0.1.0 get a deprecation warning instead of a broken build.
/// </summary>
/// <remarks>
/// It derives from the type that replaced it rather than duplicating its properties, so an existing
/// <c>new LeidenOptions { Resolution = 1.2 }</c> still reaches every method that now takes a
/// <see cref="LouvainWithRefinementOptions"/> — carrying the same values, and one deprecation
/// warning naming what the algorithm actually is. See <see cref="LouvainWithRefinement"/>'s remarks
/// for why the old name was wrong.
/// </remarks>
[Obsolete(
    "Renamed to LouvainWithRefinementOptions, after what it configures: Louvain's local moving " +
    "and aggregation with the Leiden paper's refinement phase between them. The old name was a " +
    "claim about a guarantee the code did not then have; it has it now, but the descriptive name " +
    "stays.")]
public sealed record class LeidenOptions : LouvainWithRefinementOptions;
