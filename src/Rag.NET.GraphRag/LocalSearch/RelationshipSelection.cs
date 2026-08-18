using Rag.NET.Graph;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// Orders and caps the relationships a set of selected entities reaches, by upstream's
/// <c>_filter_relationships</c> rule.
/// </summary>
/// <remarks>
/// <para>
/// Worth its own type because the rule is three rules, and the plausible summary of it — "the top
/// ten relationships" — is wrong in all three parts. In-network relationships are never truncated;
/// the cap is a multiple of the selection size rather than a constant; and the out-of-network sort
/// leads on a quantity (<c>links</c>) that is not a property of the relationship at all.
/// </para>
/// <para>
/// Source:
/// <c>packages/graphrag/graphrag/query/context_builder/local_context.py::_filter_relationships</c>
/// and <c>query/input/retrieval/relationships.py</c>.
/// </para>
/// </remarks>
internal static class RelationshipSelection
{
    /// <summary>Selects and orders relationships for the local context section.</summary>
    /// <param name="selected">Selected entities, in selection order.</param>
    /// <param name="relationships">Every relationship touching at least one selected entity.</param>
    /// <param name="degrees">Whole-graph degree by entity name.</param>
    /// <param name="topKRelationships">Multiplier for the out-of-network cap.</param>
    /// <returns>In-network relationships first, then capped out-of-network ones.</returns>
    internal static List<GraphRelationship> Select(
        IReadOnlyList<GraphEntity> selected,
        IReadOnlyList<GraphRelationship> relationships,
        IReadOnlyDictionary<string, int> degrees,
        int topKRelationships)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < selected.Count; i++)
        {
            _ = names.Add(selected[i].Name);
        }

        var (inNetworkRaw, outNetwork) = Partition(names, relationships);

        // Stable ordering throughout, because upstream's is. Python's list.sort keeps equal
        // elements in insertion order even under reverse=True, and equal ranks are common — every
        // relationship between two degree-1 entities ties with every other. List<T>.Sort is an
        // unstable introsort, so using it here would make the table's order depend on the input
        // size rather than on the graph, and no test could pin the result.
        var inNetwork = inNetworkRaw.OrderByDescending(r => Rank(r, degrees)).ToList();

        // Upstream returns early here without sorting or capping. Kept: a single out-of-network
        // relationship has nothing to be ranked against, and the cap cannot bind on one row.
        if (outNetwork.Count <= 1)
        {
            inNetwork.AddRange(outNetwork);
            return inNetwork;
        }

        var links = CountLinks(names, outNetwork);
        outNetwork = outNetwork
            .OrderByDescending(r => Links(r, names, links))
            .ThenByDescending(r => Rank(r, degrees))
            .ToList();

        // The cap scales with the selection, and applies only here — 200 rows at the defaults, not
        // ten. In-network relationships were already added above and are not subject to it.
        var budget = topKRelationships * selected.Count;
        if (outNetwork.Count > budget)
        {
            outNetwork.RemoveRange(budget, outNetwork.Count - budget);
        }

        inNetwork.AddRange(outNetwork);
        return inNetwork;
    }

    /// <summary>Splits relationships by how many of their endpoints are selected.</summary>
    /// <param name="names">Selected entity names.</param>
    /// <param name="relationships">Candidates.</param>
    /// <returns>Both endpoints selected, and exactly one endpoint selected.</returns>
    private static (List<GraphRelationship> InNetwork, List<GraphRelationship> OutNetwork) Partition(
        HashSet<string> names, IReadOnlyList<GraphRelationship> relationships)
    {
        var inNetwork = new List<GraphRelationship>();
        var outNetwork = new List<GraphRelationship>();

        for (var i = 0; i < relationships.Count; i++)
        {
            var rel = relationships[i];
            var sourceSelected = names.Contains(rel.SourceEntity);
            var targetSelected = names.Contains(rel.TargetEntity);

            if (sourceSelected && targetSelected)
            {
                inNetwork.Add(rel);
            }
            else if (sourceSelected || targetSelected)
            {
                outNetwork.Add(rel);
            }

            // Neither endpoint selected: not a candidate. Upstream never sees these because its
            // caller filters first; this tolerates them rather than trusting the caller.
        }

        return (inNetwork, outNetwork);
    }

    /// <summary>
    /// Counts, for each entity outside the selection, how many distinct out-of-network partners it
    /// has.
    /// </summary>
    /// <remarks>
    /// This is upstream's <c>links</c>, and it is the leading sort key. An outside entity that
    /// several selected entities all point at outranks one reached from a single seed, however
    /// well-connected that one is in the graph at large — which is the whole idea: a shared
    /// neighbour is evidence about the query, and a high-degree one is only evidence about the
    /// corpus.
    /// </remarks>
    /// <param name="names">Selected entity names.</param>
    /// <param name="outNetwork">Out-of-network relationships.</param>
    /// <returns>Partner count by outside entity name.</returns>
    private static Dictionary<string, int> CountLinks(
        HashSet<string> names, List<GraphRelationship> outNetwork)
    {
        var partners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < outNetwork.Count; i++)
        {
            var rel = outNetwork[i];
            var outside = names.Contains(rel.SourceEntity) ? rel.TargetEntity : rel.SourceEntity;
            var other = names.Contains(rel.SourceEntity) ? rel.SourceEntity : rel.TargetEntity;

            if (!partners.TryGetValue(outside, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                partners[outside] = set;
            }

            _ = set.Add(other);
        }

        var links = new Dictionary<string, int>(partners.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, set) in partners)
        {
            links[name] = set.Count;
        }

        return links;
    }

    /// <summary>Reads the <c>links</c> count of a relationship's outside endpoint.</summary>
    /// <param name="rel">The relationship.</param>
    /// <param name="names">Selected entity names.</param>
    /// <param name="links">Partner counts by outside entity name.</param>
    /// <returns>The count, or 0 when the endpoint has none recorded.</returns>
    private static int Links(
        GraphRelationship rel, HashSet<string> names, Dictionary<string, int> links)
    {
        var outside = names.Contains(rel.SourceEntity) ? rel.TargetEntity : rel.SourceEntity;
        return links.TryGetValue(outside, out var count) ? count : 0;
    }

    /// <summary>
    /// A relationship's rank: the sum of its two endpoints' whole-graph degrees.
    /// </summary>
    /// <remarks>
    /// Upstream stores this on the relationship as <c>rank</c>, written during indexing from the
    /// <c>combined_degree</c> column — confirmed at
    /// <c>query/indexer_adapters.py::read_indexer_relationships</c>, which maps
    /// <c>rank_col="combined_degree"</c>. Computed here instead of stored, so it cannot go stale
    /// against a graph that has grown since.
    /// </remarks>
    /// <param name="rel">The relationship.</param>
    /// <param name="degrees">Whole-graph degree by entity name.</param>
    /// <returns>Combined degree; endpoints with no recorded degree count 0.</returns>
    private static int Rank(GraphRelationship rel, IReadOnlyDictionary<string, int> degrees)
    {
        var source = degrees.TryGetValue(rel.SourceEntity, out var s) ? s : 0;
        var target = degrees.TryGetValue(rel.TargetEntity, out var t) ? t : 0;
        return source + target;
    }
}
