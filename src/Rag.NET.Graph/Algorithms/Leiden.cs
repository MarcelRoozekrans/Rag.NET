namespace Rag.NET.Graph.Algorithms;

/// <summary>
/// The former name of <see cref="LouvainWithRefinement"/>, kept as a forwarder so callers on 0.1.0
/// get a deprecation warning instead of a broken build.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name was a claim, and the claim was false.</b> It asserted Traag, Waltman and van Eck's
/// algorithm (<i>From Louvain to Leiden: guaranteeing well-connected communities</i>, Scientific
/// Reports 9:5233, 2019), whose whole reason for existing is a guarantee this implementation does
/// not provide — see <see cref="LouvainWithRefinement"/>'s remarks for the three places it departs
/// from the paper, and <c>CommunityConnectivityTests</c> for the ten-node graph on which it returns
/// an internally disconnected community at the default resolution. The absence is demonstrated, not
/// deduced.
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
    "Renamed to LouvainWithRefinement: this is Louvain's local moving and aggregation with a " +
    "refinement pass, not Traag/Waltman/van Eck's Leiden algorithm, and it does not provide that " +
    "paper's guarantee that every returned community is internally connected.")]
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
