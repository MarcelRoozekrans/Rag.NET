using System.Runtime.InteropServices;
using Rag.NET.Telemetry;

namespace Rag.NET.Graph.Algorithms;

/// <summary>Leiden community detection algorithm — a refinement of Louvain that guarantees well-connected communities.</summary>
public static class Leiden
{
    /// <summary>Detect communities in the given graph using the Leiden algorithm.</summary>
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

    private static Dictionary<string, int> BuildNameIndex(IReadOnlyList<GraphEntity> entities)
    {
        var map = new Dictionary<string, int>(entities.Count, StringComparer.Ordinal);
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
        // Start with each node in its own sub-community.
        var refined = new int[n];
        for (int i = 0; i < n; i++)
        {
            refined[i] = i;
        }

        var nodeDegree = ComputeNodeDegrees(weights, n);
        var communityWeight = ComputeCommunityWeights(refined, nodeDegree, n);
        double m2 = 2.0 * totalWeight;
        var order = CreateShuffledOrder(n, rng);

        foreach (int node in order)
        {
            RefineSingleNode(neighbors, weights, node, assignment, refined, m2, options.Resolution, nodeDegree, communityWeight);
        }

        return refined;
    }

    private static void RefineSingleNode(
        List<int>[] neighbors, List<double>[] weights, int node, int[] assignment, int[] refined,
        double m2, double resolution, double[] nodeDegree, Dictionary<int, double> communityWeight)
    {
        int currentComm = refined[node];
        int leidenComm = assignment[node];
        var neighborWeights = ComputeNeighborCommunityWeights(neighbors, weights, node, refined);

        double bestGain = 0.0;
        int bestComm = currentComm;
        double ki = nodeDegree[node];

        communityWeight[currentComm] -= ki;
        double wCurrent = neighborWeights.GetValueOrDefault(currentComm, 0.0);
        double removeCost = wCurrent - resolution * ki * communityWeight[currentComm] / m2;

        foreach (var (comm, wComm) in neighborWeights)
        {
            // Only merge within the same Leiden community.
            if (assignment.Length > comm && assignment[comm] != leidenComm)
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

    private static Dictionary<int, double> ComputeNeighborCommunityWeights(
        List<int>[] neighbors, List<double>[] weights, int node, int[] assignment)
    {
        var result = new Dictionary<int, double>();
        var nSpan = CollectionsMarshal.AsSpan(neighbors[node]);
        var wSpan = CollectionsMarshal.AsSpan(weights[node]);

        for (int i = 0; i < nSpan.Length; i++)
        {
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
                int cj = nodeMap[nSpan[k]];
                if (ci == cj)
                {
                    continue;
                }

                AddEdge(newNeighbors, newWeights, ci, cj, wSpan[k]);
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
