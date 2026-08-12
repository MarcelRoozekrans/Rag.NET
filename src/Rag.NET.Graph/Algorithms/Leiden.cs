using System.Runtime.InteropServices;
using Rag.NET.Telemetry;

namespace Rag.NET.Graph.Algorithms;

/// <summary>
/// Modularity community detection: Louvain's local moving and aggregation, with a refinement pass
/// between them that constrains what may be aggregated together.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the Leiden algorithm, and this paragraph is the only thing that says so.</b> The
/// type name asserts Traag, Waltman and van Eck's algorithm (<i>From Louvain to Leiden: guaranteeing
/// well-connected communities</i>, Scientific Reports 9:5233, 2019), whose entire reason for
/// existing is a guarantee — after each iteration "all communities are γ-connected", and in
/// particular no returned community is internally disconnected, which Louvain's are known to be.
/// <b>This implementation does not provide that guarantee</b>, and someone choosing it because the
/// name promises one would be choosing on a false premise.
/// </para>
/// <para>
/// <b>What it actually does.</b> Local moving to a modularity local optimum at
/// <see cref="LeidenOptions.Resolution"/>; then a refinement pass that rebuilds sub-communities
/// inside each community from singletons, merging a node only into a sub-community of its own
/// community and only by the largest modularity gain; then aggregation of that refined partition
/// into super-nodes, and the same again one level up. On the graphs where the answer is not in
/// doubt it finds it: ten disjoint cliques ring-bridged come back as ten communities, two joined by
/// a bridge as two, three as three. <b>It clusters, and it clusters correctly on everything
/// measured.</b>
/// </para>
/// <para>
/// <b>Where it departs from the paper, in the three places the guarantee comes from.</b> The paper
/// aggregates the refined partition but starts the next level from the unrefined one — "the
/// aggregate network is created based on the partition P<sub>refined</sub>. However, the initial
/// partition for the aggregate network is based on P" — where <see cref="RunLeiden"/> overwrites P
/// with the refined partition and restarts the next level from singletons. The paper moves only
/// nodes that are alone in their refined community, and merges one "only if both are sufficiently
/// well connected to their community in P"; <see cref="RefineSingleNode"/> moves every node and
/// tests no such condition. The paper picks the merge target at random, weighted by the size of the
/// quality increase and a randomness parameter θ; this picks the largest gain. <b>The guarantee is a
/// property of those constraints, not of having a refinement pass</b>, so adding the seeding step
/// alone would not earn the name either.
/// </para>
/// <para>
/// <b>Concretely, what is not guaranteed, and it has been measured rather than argued.</b> A refined
/// sub-community is built by attaching nodes to sub-communities they have an edge to, but a node may
/// later leave one it was the sole link through, and nothing puts the remainder back together — so a
/// returned community can be internally disconnected. It does happen. A sweep of some 30,000
/// detections found none on anything dense (cliques, planted partitions, Erdős–Rényi, barbells) and
/// found them on sparse weighted graphs: 48 of 2,220 random weighted trees held a disconnected
/// community for some resolution and seed, while 2,220 <i>unweighted</i> trees held none. A ten-node
/// example is pinned in <c>LeidenCommunityConnectivityTests</c>, at the default resolution, where a
/// returned community contains a node with no edge to any other member.
/// </para>
/// <para>
/// <b>Why the name was kept anyway.</b> <c>Leiden</c> is public in a package shipped at 0.1.0, and
/// the name reaches further than this type — <see cref="LeidenOptions"/>, the
/// <c>options.Leiden</c> property on the GraphRAG ingestion options, and this repository's own
/// guide. Renaming is a breaking change across all of them and was not taken unilaterally. The
/// honest description therefore lives here, on the type, rather than in a guide the reader who
/// picked this class for its guarantees will never open.
/// </para>
/// </remarks>
public static class Leiden
{
    /// <summary>Detect communities in the given graph by modularity optimisation.</summary>
    /// <remarks>
    /// Read <see cref="Leiden"/>'s own remarks before relying on any property of the result beyond
    /// "related entities tend to land together": this is Louvain with a refinement pass, and it does
    /// not provide the Leiden paper's well-connectedness guarantee.
    /// </remarks>
    public static IReadOnlyList<Community> Detect(GraphSnapshot graph, LeidenOptions? options = null)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graph.cluster");
        activity?.SetTag("graph.node.count", graph.Entities.Count);
        activity?.SetTag("graph.relationship.count", graph.Relationships.Count);

        options ??= new LeidenOptions();
        var entities = graph.Entities;
        int n = entities.Count;

        if (n == 0)
        {
            activity?.SetTag("graph.community.count", 0);
            return [];
        }

        var nameToIndex = BuildNameIndex(entities);
        var (neighbors, weights) = BuildAdjacency(graph.Relationships, nameToIndex, n);
        double totalWeight = ComputeTotalWeight(weights, n);
        var assignment = RunLeiden(neighbors, weights, n, totalWeight, options);

        var communities = BuildCommunities(assignment, entities);
        activity?.SetTag("graph.community.count", communities.Count);
        return communities;
    }

    /// <summary>Indexes entities by name, the way the store that produced them compares names.</summary>
    /// <remarks>
    /// <see cref="GraphNames.Comparer"/> and not <see cref="StringComparer.Ordinal"/>: a
    /// relationship endpoint whose casing differs from the entity it names is an edge, and matching
    /// it ordinally dropped it from the adjacency while the store went on traversing it.
    /// </remarks>
    private static Dictionary<string, int> BuildNameIndex(IReadOnlyList<GraphEntity> entities)
    {
        var map = new Dictionary<string, int>(entities.Count, GraphNames.Comparer);
        for (int i = 0; i < entities.Count; i++)
        {
            map[entities[i].Name] = i;
        }

        return map;
    }

    private static (List<int>[] Neighbors, List<double>[] Weights) BuildAdjacency(
        IReadOnlyList<GraphRelationship> relationships, Dictionary<string, int> nameToIndex, int n)
    {
        var neighbors = new List<int>[n];
        var edgeWeights = new List<double>[n];
        for (int i = 0; i < n; i++)
        {
            neighbors[i] = [];
            edgeWeights[i] = [];
        }

        foreach (var rel in relationships)
        {
            if (!nameToIndex.TryGetValue(rel.SourceEntity, out int src) ||
                !nameToIndex.TryGetValue(rel.TargetEntity, out int tgt) ||
                src == tgt)
            {
                continue;
            }

            AddEdge(neighbors, edgeWeights, src, tgt, rel.Weight);
            AddEdge(neighbors, edgeWeights, tgt, src, rel.Weight);
        }

        return (neighbors, edgeWeights);
    }

    private static void AddEdge(List<int>[] neighbors, List<double>[] weights, int from, int to, double weight)
    {
        var span = CollectionsMarshal.AsSpan(neighbors[from]);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == to)
            {
                CollectionsMarshal.AsSpan(weights[from])[i] += weight;
                return;
            }
        }

        neighbors[from].Add(to);
        weights[from].Add(weight);
    }

    private static double ComputeTotalWeight(List<double>[] weights, int n)
    {
        double total = 0.0;
        foreach (ref var nodeWeights in weights.AsSpan(0, n))
        {
            foreach (ref readonly double w in CollectionsMarshal.AsSpan(nodeWeights))
            {
                total += w;
            }
        }

        // Each edge counted twice (undirected).
        return total / 2.0;
    }

    private static int[] RunLeiden(List<int>[] neighbors, List<double>[] weights, int n, double totalWeight, LeidenOptions options)
    {
        var rng = new Random(options.RandomSeed);
        var assignment = new int[n];
        for (int i = 0; i < n; i++)
        {
            assignment[i] = i;
        }

        int level = 0;
        while (true)
        {
            bool moved = LocalMovingPhase(neighbors, weights, n, assignment, totalWeight, options, rng);
            if (!moved)
            {
                break;
            }

            assignment = RefinementPhase(neighbors, weights, n, assignment, totalWeight, options, rng);
            if (options.MaxLevels.HasValue && ++level >= options.MaxLevels.Value)
            {
                break;
            }

            var (agg, map) = Aggregate(neighbors, weights, n, assignment);
            if (agg.N == n)
            {
                break;
            }

            neighbors = agg.Neighbors;
            weights = agg.Weights;
            var oldAssignment = assignment;
            n = agg.N;
            totalWeight = ComputeTotalWeight(weights, n);
            assignment = new int[n];
            for (int i = 0; i < n; i++)
            {
                assignment[i] = i;
            }

            // After next phases complete, we need to map back.
            // Store the chain for later flattening.
            // Actually, we flatten after each aggregation round.
            // We'll run recursively and then flatten.
            int[] innerResult = RunLeiden(neighbors, weights, n, totalWeight, options with { MaxLevels = options.MaxLevels.HasValue ? options.MaxLevels.Value - level : null });

            // Map super-node assignments back to original nodes.
            return FlattenAssignment(oldAssignment, map, innerResult);
        }

        return assignment;
    }

    private static bool LocalMovingPhase(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
        double totalWeight, LeidenOptions options, Random rng)
    {
        var nodeDegree = ComputeNodeDegrees(weights, n);
        var communityWeight = ComputeCommunityWeights(assignment, nodeDegree, n);
        bool anyMoved = false;

        for (int iter = 0; iter < options.MaxIterations; iter++)
        {
            bool moved = LocalMovingIteration(
                neighbors, weights, n, assignment, totalWeight,
                options.Resolution, rng, nodeDegree, communityWeight);

            if (!moved)
            {
                break;
            }

            anyMoved = true;
        }

        return anyMoved;
    }

    private static bool LocalMovingIteration(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
        double totalWeight, double resolution, Random rng, double[] nodeDegree, Dictionary<int, double> communityWeight)
    {
        bool moved = false;
        var order = CreateShuffledOrder(n, rng);

        foreach (int node in order)
        {
            if (TryMoveNode(neighbors, weights, node, assignment, totalWeight, resolution, nodeDegree, communityWeight))
            {
                moved = true;
            }
        }

        return moved;
    }

    private static bool TryMoveNode(
        List<int>[] neighbors, List<double>[] weights, int node, int[] assignment,
        double totalWeight, double resolution, double[] nodeDegree, Dictionary<int, double> communityWeight)
    {
        int currentComm = assignment[node];
        var neighborWeights = ComputeNeighborCommunityWeights(neighbors, weights, node, assignment);

        double bestGain = 0.0;
        int bestComm = currentComm;

        double m2 = 2.0 * totalWeight;
        double ki = nodeDegree[node];

        // Remove node from its community for gain computation.
        communityWeight[currentComm] -= ki;
        double wCurrent = neighborWeights.GetValueOrDefault(currentComm, 0.0);
        double removeCost = wCurrent - resolution * ki * communityWeight[currentComm] / m2;

        foreach (var (comm, wComm) in neighborWeights)
        {
            double gain = wComm - resolution * ki * communityWeight[comm] / m2 - removeCost;
            if (gain > bestGain)
            {
                bestGain = gain;
                bestComm = comm;
            }
        }

        // Put node back into chosen community.
        assignment[node] = bestComm;
        communityWeight[bestComm] += ki;

        return bestComm != currentComm;
    }

    private static int[] RefinementPhase(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
        double totalWeight, LeidenOptions options, Random rng)
    {
        // Start with each node in its own sub-community, labelled by that node's own index.
        var singletons = new int[n];
        for (int i = 0; i < n; i++)
        {
            singletons[i] = i;
        }

        return Refine(neighbors, weights, n, assignment, singletons, totalWeight, options, rng);
    }

    /// <summary>
    /// Splits each community of <paramref name="assignment"/> into well-joined sub-communities,
    /// starting from the one-node-per-community labelling in <paramref name="singletonCommunities"/>.
    /// </summary>
    /// <remarks>
    /// <b>The labelling is the caller's to choose, and that is the point of this parameter.</b>
    /// Refined community ids are labels: nothing about them is an index into anything, and the only
    /// requirement is that they be distinct. <see cref="RefinementPhase"/> passes each node's own
    /// index because that is the cheapest distinct label to produce, and the refinement used to
    /// depend on that choice without saying so — see
    /// <see cref="MapRefinedCommunitiesToCommunities"/>. This parameter is how
    /// <c>LeidenRefinementNumberingTests</c> re-runs the same refinement under a labelling that is
    /// not node indices and asserts the partition comes back identical, so the dependency cannot
    /// return unnoticed.
    /// </remarks>
    internal static int[] Refine(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
        int[] singletonCommunities, double totalWeight, LeidenOptions options, Random rng)
    {
        var refined = singletonCommunities.AsSpan(0, n).ToArray();
        var refinedToCommunity = MapRefinedCommunitiesToCommunities(refined, assignment, n);
        var nodeDegree = ComputeNodeDegrees(weights, n);
        var communityWeight = ComputeCommunityWeights(refined, nodeDegree, n);
        double m2 = 2.0 * totalWeight;
        var order = CreateShuffledOrder(n, rng);

        foreach (int node in order)
        {
            RefineSingleNode(
                neighbors, weights, node, assignment[node], refined, refinedToCommunity,
                m2, options.Resolution, nodeDegree, communityWeight);
        }

        return refined;
    }

    /// <summary>
    /// Which community of the partition being refined each refined sub-community sits inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This lookup exists so that a community id is never used as a node index.</b> The question
    /// <see cref="RefineSingleNode"/> has to answer about a candidate sub-community is "which
    /// community is it part of", and it used to answer it with <c>assignment[comm]</c> — an array
    /// indexed by node index everywhere else in this file, subscripted by a sub-community id. That
    /// was right only because <see cref="RefinementPhase"/> labels each starting sub-community with
    /// its own node's index, so the two spaces coincided numerically by construction. Renumbering
    /// sub-communities any other way — densely from zero is the obvious one — read an unrelated
    /// node's entry and answered the wrong question without failing. See
    /// <c>LeidenRefinementNumberingTests</c>, which fails against the old form under both a dense
    /// renumbering and a constant offset.
    /// </para>
    /// <para>
    /// <b>It stays correct as nodes move.</b> A sub-community's entry is never updated, and does not
    /// need to be: every merge <see cref="RefineSingleNode"/> permits is into a sub-community of the
    /// same community, so a sub-community's community is fixed for as long as it has members — even
    /// after the node it was named for has left it.
    /// </para>
    /// </remarks>
    private static Dictionary<int, int> MapRefinedCommunitiesToCommunities(int[] refined, int[] assignment, int n)
    {
        var map = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
        {
            map[refined[i]] = assignment[i];
        }

        return map;
    }

    /// <summary>
    /// Moves one node into the neighbouring sub-community that gains the most modularity, considering
    /// only sub-communities of <paramref name="community"/> — the node's own community in the
    /// partition being refined.
    /// </summary>
    /// <remarks>
    /// Every id this method handles is a sub-community id: the keys of
    /// <paramref name="communityWeight"/>, the keys of the neighbour weights, and the values of
    /// <paramref name="refined"/>. It is passed no array indexed by node index that it could
    /// subscript with one by mistake, and an id missing from
    /// <paramref name="refinedToCommunity"/> throws rather than reading something unrelated.
    /// </remarks>
    private static void RefineSingleNode(
        List<int>[] neighbors, List<double>[] weights, int node, int community, int[] refined,
        Dictionary<int, int> refinedToCommunity,
        double m2, double resolution, double[] nodeDegree, Dictionary<int, double> communityWeight)
    {
        int currentComm = refined[node];
        var neighborWeights = ComputeNeighborCommunityWeights(neighbors, weights, node, refined);

        double bestGain = 0.0;
        int bestComm = currentComm;
        double ki = nodeDegree[node];

        communityWeight[currentComm] -= ki;
        double wCurrent = neighborWeights.GetValueOrDefault(currentComm, 0.0);
        double removeCost = wCurrent - resolution * ki * communityWeight[currentComm] / m2;

        foreach (var (comm, wComm) in neighborWeights)
        {
            // Sub-communities may only merge within one community of the partition being refined.
            if (refinedToCommunity[comm] != community)
            {
                continue;
            }

            double gain = wComm - resolution * ki * communityWeight[comm] / m2 - removeCost;
            if (gain > bestGain)
            {
                bestGain = gain;
                bestComm = comm;
            }
        }

        refined[node] = bestComm;
        communityWeight[bestComm] += ki;
    }

    /// <summary>How much edge weight joins one node to each community around it.</summary>
    /// <remarks>
    /// <b>The node's own self-loop is deliberately not counted.</b> Since
    /// <see cref="BuildAggregatedEdges"/> began folding a community's internal weight into a
    /// self-loop, super-nodes carry one, and it belongs to the node rather than to any community it
    /// might join: modularity's <c>A_ii</c> term contributes the same amount wherever the node sits,
    /// so it cancels out of every gain and every removal cost. Counting it would inflate the weight
    /// binding a node to whichever community it currently occupies by its entire internal weight,
    /// and no node would ever leave anywhere. It is still counted in
    /// <see cref="ComputeNodeDegrees"/>, where it is the whole point.
    /// </remarks>
    private static Dictionary<int, double> ComputeNeighborCommunityWeights(
        List<int>[] neighbors, List<double>[] weights, int node, int[] assignment)
    {
        var result = new Dictionary<int, double>();
        var nSpan = CollectionsMarshal.AsSpan(neighbors[node]);
        var wSpan = CollectionsMarshal.AsSpan(weights[node]);

        for (int i = 0; i < nSpan.Length; i++)
        {
            if (nSpan[i] == node)
            {
                continue;
            }

            int comm = assignment[nSpan[i]];
            ref double val = ref CollectionsMarshal.GetValueRefOrAddDefault(result, comm, out _);
            val += wSpan[i];
        }

        return result;
    }

    private static (AggregatedGraph, int[] Map) Aggregate(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment)
    {
        // Compact community IDs.
        var communityMap = new Dictionary<int, int>();
        var nodeMap = new int[n];
        int nextId = 0;

        for (int i = 0; i < n; i++)
        {
            if (!communityMap.TryGetValue(assignment[i], out int mapped))
            {
                mapped = nextId++;
                communityMap[assignment[i]] = mapped;
            }

            nodeMap[i] = mapped;
        }

        int newN = nextId;
        var newNeighbors = new List<int>[newN];
        var newWeights = new List<double>[newN];
        for (int i = 0; i < newN; i++)
        {
            newNeighbors[i] = [];
            newWeights[i] = [];
        }

        BuildAggregatedEdges(neighbors, weights, n, nodeMap, newNeighbors, newWeights);

        return (new AggregatedGraph(newN, newNeighbors, newWeights), nodeMap);
    }

    /// <summary>
    /// Collapses each community into one super-node, keeping the weight between communities as
    /// edges and the weight inside each community as a self-loop on its super-node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The self-loop is what makes the next level's modularity mean anything, and dropping it
    /// collapsed the whole clustering.</b> Modularity scores a community against a null model whose
    /// only input is each node's total incident weight, so a super-node that discarded its internal
    /// edges arrives at the next level looking exactly as light as its few external ones. The
    /// penalty for merging it into a neighbour is computed from that phantom weight, merging
    /// therefore always pays, and every level swallows the one below it — ten cliques joined in a
    /// ring came back as a single community of a hundred, and a 9,000-entity corpus as a single
    /// community of 8,070.
    /// </para>
    /// <para>
    /// <b>Why the weight lands doubled and that is not an error.</b> Adjacency here is undirected
    /// and stored from both ends, so an internal edge between two distinct members is visited twice
    /// and contributes its weight twice, giving a self-loop of 2·W. That is the quantity the rest of
    /// the algorithm wants: <see cref="ComputeNodeDegrees"/> sums the adjacency list to get a node's
    /// degree, and a self-loop counts twice toward degree because both of its ends are attached to
    /// the same node. It also keeps <see cref="ComputeTotalWeight"/> invariant across levels, which
    /// it must be — <c>m</c> is the same graph however it is aggregated. A self-loop already present
    /// on an incoming super-node is visited once and carries its already-doubled weight forward
    /// unchanged, which is the same arithmetic one level up.
    /// </para>
    /// </remarks>
    private static void BuildAggregatedEdges(
        List<int>[] neighbors, List<double>[] weights, int n, int[] nodeMap,
        List<int>[] newNeighbors, List<double>[] newWeights)
    {
        for (int i = 0; i < n; i++)
        {
            int ci = nodeMap[i];
            var nSpan = CollectionsMarshal.AsSpan(neighbors[i]);
            var wSpan = CollectionsMarshal.AsSpan(weights[i]);

            for (int k = 0; k < nSpan.Length; k++)
            {
                AddEdge(newNeighbors, newWeights, ci, nodeMap[nSpan[k]], wSpan[k]);
            }
        }
    }

    private static int[] FlattenAssignment(int[] oldAssignment, int[] nodeMap, int[] innerResult)
    {
        var result = new int[oldAssignment.Length];
        for (int i = 0; i < oldAssignment.Length; i++)
        {
            int superNode = nodeMap[i];
            result[i] = innerResult[superNode];
        }

        return result;
    }

    private static double[] ComputeNodeDegrees(List<double>[] weights, int n)
    {
        var degrees = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0.0;
            foreach (ref readonly double w in CollectionsMarshal.AsSpan(weights[i]))
            {
                sum += w;
            }

            degrees[i] = sum;
        }

        return degrees;
    }

    private static Dictionary<int, double> ComputeCommunityWeights(int[] assignment, double[] nodeDegree, int n)
    {
        var cw = new Dictionary<int, double>();
        for (int i = 0; i < n; i++)
        {
            ref double val = ref CollectionsMarshal.GetValueRefOrAddDefault(cw, assignment[i], out _);
            val += nodeDegree[i];
        }

        return cw;
    }

    private static int[] CreateShuffledOrder(int n, Random rng)
    {
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        // Fisher-Yates shuffle.
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    private static IReadOnlyList<Community> BuildCommunities(int[] assignment, IReadOnlyList<GraphEntity> entities)
    {
        var groups = new Dictionary<int, List<string>>();
        for (int i = 0; i < assignment.Length; i++)
        {
            if (!groups.TryGetValue(assignment[i], out var list))
            {
                list = [];
                groups[assignment[i]] = list;
            }

            list.Add(entities[i].Name);
        }

        int id = 0;
        var result = new List<Community>(groups.Count);
        foreach (var (_, members) in groups)
        {
            members.Sort(StringComparer.Ordinal);
            result.Add(new Community(id++, 0, members, null));
        }

        return result;
    }

    private sealed record AggregatedGraph(int N, List<int>[] Neighbors, List<double>[] Weights);
}
