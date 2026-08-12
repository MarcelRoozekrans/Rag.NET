namespace Rag.NET.Graph.Algorithms;

/// <summary>
/// The former name of <see cref="LouvainWithRefinement"/>, kept as a forwarder so callers on 0.1.0
/// get a deprecation warning instead of a broken build.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name was a claim, and when it was made the claim was false.</b> It asserted Traag, Waltman
/// and van Eck's algorithm (<i>From Louvain to Leiden: guaranteeing well-connected communities</i>,
/// Scientific Reports 9:5233, 2019), whose whole reason for existing is a guarantee the
/// implementation behind this name did not provide — <c>CommunityConnectivityTests</c> pinned a
/// ten-node graph on which it returned an internally disconnected community at the default
/// resolution. The absence was demonstrated, not deduced. <b>It has since been implemented (#180)</b>
/// and the replacement type now supplies that guarantee; the rename is not being undone, because
/// <see cref="LouvainWithRefinement"/> describes the construction rather than pointing at a paper,
/// and because reversing a rename costs every caller a second migration to say nothing new.
/// </para>
/// <para>
/// <b>This type adds nothing and changes nothing.</b> <see cref="Detect"/> hands its arguments
/// straight to <see cref="LouvainWithRefinement.Detect"/>, so the partition is identical, seed for
/// seed — <c>ObsoleteLeidenForwarderTests</c> calls through here by reflection (the only way to
/// exercise it in a tree that builds warnings-as-errors) and asserts exactly that, because a
/// forwarder nobody has run is indistinguishable from a broken one.
/// </para>
/// </remarks>
[Obsolete(
    "Renamed to LouvainWithRefinement, which is what it is: Louvain's local moving and " +
    "aggregation with the Leiden paper's refinement phase between them. The old name was a claim " +
    "about a guarantee the code did not then have; it has it now, but the descriptive name stays.")]
public static class Leiden
{
    /// <summary>Detect communities in the given graph by modularity optimisation.</summary>
    /// <param name="graph">The graph to cluster.</param>
    /// <param name="options">The clustering settings, or null for the defaults.</param>
    /// <returns>Whatever <see cref="LouvainWithRefinement.Detect"/> returns for the same arguments.</returns>
    public static IReadOnlyList<Community> Detect(
        GraphSnapshot graph, LouvainWithRefinementOptions? options = null) =>
        LouvainWithRefinement.Detect(graph, options);
}
