using System.Runtime.InteropServices;
using Rag.NET.Telemetry;

namespace Rag.NET.Graph.Algorithms;

/// <summary>
/// Modularity community detection: Louvain's local moving and aggregation, with a refinement pass
/// between them that constrains what may be aggregated together.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> Local moving to a modularity local optimum at
/// <see cref="LouvainWithRefinementOptions.Resolution"/>; then the refinement pass of Traag, Waltman
/// and van Eck (<i>From Louvain to Leiden: guaranteeing well-connected communities</i>, Scientific
/// Reports 9:5233, 2019), which rebuilds sub-communities inside each community from singletons; then
/// aggregation of <i>that refined partition</i> into super-nodes while the next level starts from
/// the unrefined one, and the same again one level up until every community is a single super-node.
/// </para>
/// <para>
/// <b>Every returned community is connected in the subgraph it induces.</b> That is the property
/// the paper exists to supply and Louvain lacks, and it is a consequence of three constraints in
/// <see cref="RefinementState"/> and <see cref="RefineSingleNode"/> together with the seeding step
/// in <see cref="RunLevels"/>, not of merely having a refinement pass. Only a node alone in its
/// refined sub-community may move; it may only join a sub-community of its own community in the
/// partition being refined, and only when both it and that sub-community are γ-connected to that
/// community; and the target is drawn at random, weighted by <c>exp(ΔQ / θ)</c> over the candidates
/// whose modularity increase is non-negative, rather than taken as the largest gain. A move with no
/// edge behind it has <c>ΔQ = -γ·k·K/2m &lt; 0</c> and is never drawn, so a refined sub-community is
/// connected by construction; the loop then exits only once every community of the working partition
/// is one such sub-community, which makes the returned communities connected too.
/// </para>
/// <para>
/// <b>What that guarantee is not.</b> Both early exits hand back the refined partition rather than
/// the working one, precisely so that connectedness survives them: a caller who caps the hierarchy
/// with <see cref="LouvainWithRefinementOptions.MaxLevels"/>, and the case where a level's
/// refinement merges nothing at all and so cannot aggregate, both get connected communities by
/// getting a <i>finer</i> partition than the uncapped run would. That is a cost in quality, not in
/// the guarantee, and <see cref="RefineUntilItMerges"/> exists to make the second case rare. The
/// guarantee is also not about optimality: the partition is a modularity local optimum reached by a
/// randomised search, and a different <see cref="LouvainWithRefinementOptions.RandomSeed"/> is a
/// different local optimum.
/// </para>
/// <para>
/// <b>The name is descriptive and stays that way.</b> This type was called <c>Leiden</c> while it
/// lacked all three constraints above, which made the name a claim about a guarantee it did not
/// have; the old name survives as an <see cref="Leiden">obsolete forwarder</see>. It now implements
/// the paper's construction over modularity, with two documented simplifications that do not bear on
/// the guarantee: the local moving phase is a repeated sweep rather than the paper's queue-driven
/// <c>MoveNodesFast</c>, and it does not offer a node an empty community to move into, so the set of
/// nodes the refinement will consider can be smaller than the paper's <c>R</c> — which leaves more
/// nodes as refined singletons and costs a level, never connectedness.
/// </para>
/// </remarks>
public static class LouvainWithRefinement
{
    /// <summary>How many times one level will redraw a refinement that merged nothing at all.</summary>
    /// <remarks>
    /// Four rather than one because a redraw is cheap next to the level it saves, and rather than a
    /// larger number because the outcome it guards against needs every node in every multi-node
    /// community to draw "stay" on the same pass — if that keeps happening, no candidate has a
    /// non-negative quality increase and no further draw will find one.
    /// </remarks>
    private const int RefinementAttempts = 4;

    /// <summary>Detect communities in the given graph by modularity optimisation.</summary>
    /// <remarks>
    /// Every returned community is connected in the subgraph it induces — see
    /// <see cref="LouvainWithRefinement"/>'s own remarks for where that comes from and for what it
    /// still does not promise.
    /// </remarks>
    public static IReadOnlyList<Community> Detect(GraphSnapshot graph, LouvainWithRefinementOptions? options = null)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graph.cluster");
        activity?.SetTag("graph.node.count", graph.Entities.Count);
        activity?.SetTag("graph.relationship.count", graph.Relationships.Count);

        options ??= new LouvainWithRefinementOptions();
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
        var assignment = RunLevels(neighbors, weights, n, totalWeight, options);

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

    /// <summary>
    /// Runs the level hierarchy: local moving, refinement, aggregation of the refined partition, and
    /// the same again over the super-nodes until every community is a single super-node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two partitions are kept apart here, and keeping them apart is the seeding step the
    /// guarantee needs.</b> <c>partition</c> is the paper's P and <c>refined</c> its
    /// P<sub>refined</sub>: "the aggregate network is created based on the refined partition
    /// P<sub>refined</sub>. However, the initial partition for the aggregate network is based on P."
    /// This method used to overwrite P with the refined partition and then restart the next level
    /// from singletons, which threw away everything the level below had learned and left the
    /// refinement decorative — the sub-communities were aggregated and then immediately dissolved.
    /// </para>
    /// <para>
    /// <b>Why the exit condition is "P is the singleton partition" and not "nothing moved".</b> Each
    /// super-node is a refined sub-community, and so is connected by construction; a community of P
    /// is a <i>set</i> of super-nodes and need not be. The returned communities are therefore
    /// connected exactly when the loop stops with every community of P holding one super-node, which
    /// is the paper's <c>done</c>. Stopping on "no node moved" would return P at a level where its
    /// communities are still sets, which is where the disconnected communities came from.
    /// </para>
    /// </remarks>
    private static int[] RunLevels(List<int>[] neighbors, List<double>[] weights, int n, double totalWeight, LouvainWithRefinementOptions options)
    {
        var rng = new Random(options.RandomSeed);
        int originalN = n;
        var membership = CreateIdentity(n);
        var partition = CreateIdentity(n);

        int level = 0;
        while (true)
        {
            LocalMovingPhase(neighbors, weights, n, partition, totalWeight, options, rng);
            if (CountCommunities(partition, n) == n)
            {
                break;
            }

            var refined = RefineUntilItMerges(neighbors, weights, n, partition, totalWeight, options, rng);
            var (aggregated, nodeMap) = Aggregate(neighbors, weights, n, refined);
            ProjectInPlace(membership, nodeMap, originalN);

            if (aggregated.N == n || (options.MaxLevels.HasValue && ++level >= options.MaxLevels.Value))
            {
                return membership;
            }

            partition = ProjectPartition(partition, nodeMap, n, aggregated.N);
            neighbors = aggregated.Neighbors;
            weights = aggregated.Weights;
            n = aggregated.N;
            totalWeight = ComputeTotalWeight(weights, n);
        }

        return FlattenAssignment(membership, partition, originalN);
    }

    /// <summary>
    /// Refines the partition, retrying while the refinement merges nothing at all.
    /// </summary>
    /// <remarks>
    /// <b>The retry exists because the refinement is a lottery and a lottery can come up empty.</b>
    /// Every node in every multi-node community has some probability of drawing "stay where you
    /// are", and if every one of them does so the refined partition is the singleton partition, the
    /// aggregate graph is the same size as the graph, and <see cref="RunLevels"/> has to stop a level
    /// early and hand back singletons. Redrawing costs one refinement pass and makes that outcome
    /// require the same unlucky sweep several times over. It cannot be ruled out entirely — if no
    /// node in any community has a legal merge with a non-negative quality increase, no number of
    /// draws will find one — which is why the caller still has a path for it.
    /// </remarks>
    private static int[] RefineUntilItMerges(
        List<int>[] neighbors, List<double>[] weights, int n, int[] partition,
        double totalWeight, LouvainWithRefinementOptions options, Random rng)
    {
        int[] refined = [];
        for (int attempt = 0; attempt < RefinementAttempts; attempt++)
        {
            refined = RefinementPhase(neighbors, weights, n, partition, totalWeight, options, rng);
            if (CountCommunities(refined, n) < n)
            {
                break;
            }
        }

        return refined;
    }

    private static void LocalMovingPhase(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
        double totalWeight, LouvainWithRefinementOptions options, Random rng)
    {
        var nodeDegree = ComputeNodeDegrees(weights, n);
        var communityWeight = ComputeCommunityWeights(assignment, nodeDegree, n);

        for (int iter = 0; iter < options.MaxIterations; iter++)
        {
            bool moved = LocalMovingIteration(
                neighbors, weights, n, assignment, totalWeight,
                options.Resolution, rng, nodeDegree, communityWeight);

            if (!moved)
            {
                break;
            }
        }
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
        double totalWeight, LouvainWithRefinementOptions options, Random rng) =>

        // Start with each node in its own sub-community, labelled by that node's own index.
        Refine(neighbors, weights, n, assignment, CreateIdentity(n), totalWeight, options, rng);

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
    /// <c>RefinementNumberingTests</c> re-runs the same refinement under a labelling that is
    /// not node indices and asserts the partition comes back identical, so the dependency cannot
    /// return unnoticed.
    /// </remarks>
    internal static int[] Refine(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
        int[] singletonCommunities, double totalWeight, LouvainWithRefinementOptions options, Random rng)
    {
        var state = new RefinementState(
            neighbors, weights, n, assignment, singletonCommunities, totalWeight, options.Resolution);
        var order = CreateShuffledOrder(n, rng);

        foreach (int node in order)
        {
            RefineSingleNode(state, node, options.Randomness, rng);
        }

        return state.Refined;
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
    /// <c>RefinementNumberingTests</c>, which fails against the old form under both a dense
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
    /// Merges one node into a sub-community of its own community, drawn at random and weighted by
    /// the modularity increase — the paper's <c>MergeNodesSubset</c> body for a single node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three refusals here are the guarantee, and dropping any one of them brings back
    /// disconnected communities.</b> A node that is no longer alone in its sub-community is left
    /// where it is, so a sub-community is only ever added to and no node can leave one it was the
    /// sole link through — that departure, with nothing to put the remainder back together, is the
    /// mechanism the recorded ten-node counterexample ran on. A node that is not itself γ-connected
    /// to its community is left alone, and so is a sub-community that is not; and a candidate whose
    /// modularity increase is negative is never drawn, which is what rules out joining a
    /// sub-community there is no edge to.
    /// </para>
    /// <para>
    /// <b>Candidates come from the node's neighbours, which is narrower than the paper's <c>T</c> and
    /// deliberately so.</b> <c>T</c> is every well-connected sub-community of <c>S</c>; the ones the
    /// node has no edge to score <c>ΔQ = -γ·k·K/2m</c>, which is negative and so excluded — except
    /// when the node has degree zero, where it is exactly zero and the node could be drawn into a
    /// community it does not touch. Enumerating neighbours removes that case rather than relying on
    /// a strict inequality over a floating-point zero.
    /// </para>
    /// </remarks>
    private static void RefineSingleNode(RefinementState state, int node, double randomness, Random rng)
    {
        if (!state.IsAloneInItsSubCommunity(node) || !state.IsWellConnectedToItsCommunity(node))
        {
            return;
        }

        var candidates = new List<MergeCandidate>();
        double best = 0.0;
        foreach (var (subCommunity, edgeWeight) in state.NeighbouringSubCommunities(node))
        {
            if (!state.IsEligibleTarget(node, subCommunity))
            {
                continue;
            }

            double gain = state.QualityIncrease(node, subCommunity, edgeWeight);
            if (gain < 0.0)
            {
                continue;
            }

            candidates.Add(new MergeCandidate(subCommunity, edgeWeight, gain));
            best = Math.Max(best, gain);
        }

        int chosen = ChooseTarget(candidates, best, randomness, rng);
        if (chosen >= 0)
        {
            state.Merge(node, candidates[chosen]);
        }
    }

    /// <summary>
    /// Draws one candidate with probability proportional to <c>exp(ΔQ / θ)</c>, or -1 for "stay".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Staying put is a candidate, not a fallback.</b> The paper's <c>T</c> contains the node's
    /// own singleton sub-community, whose quality increase is zero and whose weight is therefore
    /// <c>exp(0)</c>; leaving it out would make the draw always move a node that has any
    /// non-negative candidate at all, which is a different algorithm with a different stationary
    /// behaviour. It is the reason <see cref="RefineUntilItMerges"/> exists.
    /// </para>
    /// <para>
    /// <b>Every exponent is shifted by the best gain so nothing overflows.</b> <c>exp(ΔQ / θ)</c>
    /// with the paper's θ of 0.01 overflows to infinity by a quality increase of about 7.1, which on
    /// a graph with heavy edges is an ordinary value, and infinity divided by infinity is a
    /// <c>NaN</c> probability. Subtracting the maximum leaves every weight in <c>(0, 1]</c> and the
    /// total at least 1, and scales out of the ratio exactly.
    /// </para>
    /// </remarks>
    private static int ChooseTarget(List<MergeCandidate> candidates, double best, double randomness, Random rng)
    {
        if (candidates.Count == 0)
        {
            return -1;
        }

        double stayWeight = Math.Exp(-best / randomness);
        double total = stayWeight;
        foreach (ref readonly var candidate in CollectionsMarshal.AsSpan(candidates))
        {
            total += Math.Exp((candidate.Gain - best) / randomness);
        }

        double draw = rng.NextDouble() * total;
        if (draw < stayWeight)
        {
            return -1;
        }

        double cumulative = stayWeight;
        for (int i = 0; i < candidates.Count; i++)
        {
            cumulative += Math.Exp((candidates[i].Gain - best) / randomness);
            if (draw < cumulative)
            {
                return i;
            }
        }

        // Only reachable when rounding leaves the draw just past the accumulated total.
        return candidates.Count - 1;
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

    /// <summary>Reads each original node's community off the partition of the top level.</summary>
    private static int[] FlattenAssignment(int[] membership, int[] partition, int originalN)
    {
        var result = new int[originalN];
        for (int i = 0; i < originalN; i++)
        {
            result[i] = partition[membership[i]];
        }

        return result;
    }

    /// <summary>The one-per-node labelling every partition in this file starts from.</summary>
    private static int[] CreateIdentity(int n)
    {
        var identity = new int[n];
        for (int i = 0; i < n; i++)
        {
            identity[i] = i;
        }

        return identity;
    }

    /// <summary>How many distinct communities a labelling holds.</summary>
    private static int CountCommunities(int[] assignment, int n)
    {
        var seen = new HashSet<int>(n);
        foreach (ref readonly int community in assignment.AsSpan(0, n))
        {
            seen.Add(community);
        }

        return seen.Count;
    }

    /// <summary>Follows each original node from the node it was in to the super-node it is now in.</summary>
    private static void ProjectInPlace(int[] membership, int[] nodeMap, int originalN)
    {
        foreach (ref int node in membership.AsSpan(0, originalN))
        {
            node = nodeMap[node];
        }
    }

    /// <summary>
    /// Carries the partition P onto the aggregate graph — the paper's "the initial partition for the
    /// aggregate network is based on P".
    /// </summary>
    /// <remarks>
    /// Every node collapsed into one super-node shared a refined sub-community, and a refined
    /// sub-community never straddles two communities of P, so all of them agree on the community to
    /// write and it does not matter which of them writes it.
    /// </remarks>
    private static int[] ProjectPartition(int[] partition, int[] nodeMap, int n, int aggregatedN)
    {
        var projected = new int[aggregatedN];
        for (int i = 0; i < n; i++)
        {
            projected[nodeMap[i]] = partition[i];
        }

        return projected;
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

    /// <summary>How much of one node's incident weight goes to the rest of its own community.</summary>
    private static double[] ComputeBoundaryToOwnCommunity(
        List<int>[] neighbors, List<double>[] weights, int n, int[] assignment)
    {
        var boundary = new double[n];
        for (int i = 0; i < n; i++)
        {
            var nSpan = CollectionsMarshal.AsSpan(neighbors[i]);
            var wSpan = CollectionsMarshal.AsSpan(weights[i]);
            double sum = 0.0;

            for (int k = 0; k < nSpan.Length; k++)
            {
                if (nSpan[k] != i && assignment[nSpan[k]] == assignment[i])
                {
                    sum += wSpan[k];
                }
            }

            boundary[i] = sum;
        }

        return boundary;
    }

    private static int[] CreateShuffledOrder(int n, Random rng)
    {
        var order = CreateIdentity(n);

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

    /// <summary>One sub-community a node may merge into, with the weight and gain that go with it.</summary>
    /// <param name="SubCommunity">The refined sub-community id.</param>
    /// <param name="EdgeWeight">Weight joining the node to it, needed again to fix up its boundary.</param>
    /// <param name="Gain">The modularity increase the merge would produce.</param>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct MergeCandidate(int SubCommunity, double EdgeWeight, double Gain);

    /// <summary>
    /// The bookkeeping one refinement pass needs, and the only place the γ-connectedness tests live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the quantities are maintained rather than recomputed.</b> The paper's condition —
    /// "<c>E(C, S − C) ≥ γ‖C‖·(‖S‖ − ‖C‖)</c>" — is asked of a sub-community that grows as the pass
    /// runs, so its boundary onto the rest of its community changes with every merge. Recomputing it
    /// per candidate would make the pass quadratic in community size; maintaining it costs one
    /// subtraction per merge, because the only edges that stop being boundary edges are the ones
    /// between the joining node and the community it joins, and they leave from both ends.
    /// </para>
    /// <para>
    /// <b>The condition is stated for CPM in the paper, and this is its modularity form.</b>
    /// Modularity is CPM with each node's size taken as its degree and the resolution rescaled by
    /// <c>2m</c>, so <c>‖C‖</c> is the community's total incident weight and the threshold is
    /// <c>γ·K_C·(K_S − K_C) / 2m</c>. Written that way it is also readable as the statement the
    /// local moving phase would make: the sub-community does not improve modularity by leaving its
    /// community.
    /// </para>
    /// </remarks>
    private sealed class RefinementState
    {
        private readonly List<int>[] _neighbors;
        private readonly List<double>[] _weights;
        private readonly int[] _assignment;
        private readonly double[] _nodeDegree;
        private readonly double[] _nodeBoundary;
        private readonly Dictionary<int, double> _communityWeight;
        private readonly Dictionary<int, double> _subCommunityWeight;
        private readonly Dictionary<int, double> _subCommunityBoundary;
        private readonly Dictionary<int, int> _subCommunitySize;
        private readonly Dictionary<int, int> _subCommunityToCommunity;
        private readonly double _m2;
        private readonly double _resolution;

        internal RefinementState(
            List<int>[] neighbors, List<double>[] weights, int n, int[] assignment,
            int[] singletonCommunities, double totalWeight, double resolution)
        {
            _neighbors = neighbors;
            _weights = weights;
            _assignment = assignment;
            _resolution = resolution;
            _m2 = 2.0 * totalWeight;
            Refined = singletonCommunities.AsSpan(0, n).ToArray();
            _subCommunityToCommunity = MapRefinedCommunitiesToCommunities(Refined, assignment, n);
            _nodeDegree = ComputeNodeDegrees(weights, n);
            _nodeBoundary = ComputeBoundaryToOwnCommunity(neighbors, weights, n, assignment);
            _communityWeight = ComputeCommunityWeights(assignment, _nodeDegree, n);
            _subCommunityWeight = ComputeCommunityWeights(Refined, _nodeDegree, n);
            _subCommunityBoundary = new Dictionary<int, double>(n);
            _subCommunitySize = new Dictionary<int, int>(n);

            for (int i = 0; i < n; i++)
            {
                _subCommunityBoundary[Refined[i]] = _nodeBoundary[i];
                _subCommunitySize[Refined[i]] = 1;
            }
        }

        /// <summary>The sub-community each node currently belongs to.</summary>
        internal int[] Refined { get; }

        /// <summary>Whether the node is still the only member of its sub-community.</summary>
        internal bool IsAloneInItsSubCommunity(int node) => _subCommunitySize[Refined[node]] == 1;

        /// <summary>Whether the node is γ-connected to its community — the paper's <c>R</c>.</summary>
        internal bool IsWellConnectedToItsCommunity(int node) =>
            _nodeBoundary[node] >= Threshold(_nodeDegree[node], _communityWeight[_assignment[node]]);

        /// <summary>How much weight joins the node to each sub-community around it.</summary>
        internal Dictionary<int, double> NeighbouringSubCommunities(int node) =>
            ComputeNeighborCommunityWeights(_neighbors, _weights, node, Refined);

        /// <summary>
        /// Whether a sub-community may be merged into: inside the node's own community, and
        /// γ-connected to it.
        /// </summary>
        internal bool IsEligibleTarget(int node, int subCommunity)
        {
            if (_subCommunityToCommunity[subCommunity] != _assignment[node])
            {
                return false;
            }

            double weight = _subCommunityWeight[subCommunity];
            return _subCommunityBoundary[subCommunity] >=
                Threshold(weight, _communityWeight[_assignment[node]]);
        }

        /// <summary>
        /// The modularity increase from moving the node out of its singleton into a sub-community.
        /// </summary>
        /// <remarks>
        /// The removal cost the local moving phase subtracts is absent because it is zero here: the
        /// node is alone, so it has no weight to its own sub-community and nothing is left behind.
        /// </remarks>
        internal double QualityIncrease(int node, int subCommunity, double edgeWeight) =>
            edgeWeight - (_resolution * _nodeDegree[node] * _subCommunityWeight[subCommunity] / _m2);

        /// <summary>Moves the node into the chosen sub-community and retires the one it leaves empty.</summary>
        internal void Merge(int node, in MergeCandidate target)
        {
            int vacated = Refined[node];
            if (vacated == target.SubCommunity)
            {
                return;
            }

            // The edges between the node and its new sub-community stop being boundary edges at both
            // of their ends, which is why the doubling is there and not an error.
            _subCommunityBoundary[target.SubCommunity] +=
                _nodeBoundary[node] - (2.0 * target.EdgeWeight);
            _subCommunityWeight[target.SubCommunity] += _nodeDegree[node];
            _subCommunitySize[target.SubCommunity]++;

            _subCommunityBoundary.Remove(vacated);
            _subCommunityWeight.Remove(vacated);
            _subCommunitySize.Remove(vacated);
            Refined[node] = target.SubCommunity;
        }

        /// <summary>The weight a part must carry to the rest of its community to count as γ-connected.</summary>
        private double Threshold(double partWeight, double communityWeight) =>
            _resolution * partWeight * (communityWeight - partWeight) / _m2;
    }
}
