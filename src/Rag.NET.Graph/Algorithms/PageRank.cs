using System.Runtime.InteropServices;

namespace Rag.NET.Graph.Algorithms;

/// <summary>Standard iterative PageRank on an entity-relationship graph.</summary>
public static class PageRank
{
    /// <summary>Compute PageRank scores for all entities. Scores sum to 1.0.</summary>
    public static IReadOnlyDictionary<string, double> Compute(
        GraphSnapshot graph,
        double dampingFactor = 0.85,
        int maxIterations = 100,
        double tolerance = 1e-6)
    {
        var entities = graph.Entities;
        int n = entities.Count;

        if (n == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var nameToIndex = BuildNameIndex(entities);
        var (outgoing, incoming) = BuildAdjacency(graph.Relationships, nameToIndex, n);
        var ranks = RunIterations(outgoing, incoming, n, dampingFactor, maxIterations, tolerance);

        return BuildResult(entities, ranks);
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

    private static (List<int>[] Outgoing, List<int>[] Incoming) BuildAdjacency(
        IReadOnlyList<GraphRelationship> relationships,
        Dictionary<string, int> nameToIndex,
        int n)
    {
        var outgoing = new List<int>[n];
        var incoming = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            outgoing[i] = [];
            incoming[i] = [];
        }

        foreach (var rel in relationships)
        {
            if (!nameToIndex.TryGetValue(rel.SourceEntity, out int src) ||
                !nameToIndex.TryGetValue(rel.TargetEntity, out int tgt) ||
                src == tgt)
            {
                continue;
            }

            outgoing[src].Add(tgt);
            incoming[tgt].Add(src);
        }

        return (outgoing, incoming);
    }

    private static double[] RunIterations(
        List<int>[] outgoing,
        List<int>[] incoming,
        int n,
        double dampingFactor,
        int maxIterations,
        double tolerance)
    {
        double initial = 1.0 / n;
        var ranks = new double[n];
        var newRanks = new double[n];
        Array.Fill(ranks, initial);

        double baseFactor = (1.0 - dampingFactor) / n;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double danglingContrib = dampingFactor * ComputeDanglingSum(ranks, outgoing, n) / n;

            ComputeNewRanks(newRanks, ranks, incoming, outgoing, baseFactor, danglingContrib, dampingFactor, n);

            bool converged = MaxDelta(ranks, newRanks, n) < tolerance;
            (ranks, newRanks) = (newRanks, ranks);

            if (converged)
            {
                break;
            }
        }

        return ranks;
    }

    private static double ComputeDanglingSum(double[] ranks, List<int>[] outgoing, int n)
    {
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (outgoing[i].Count == 0)
            {
                sum += ranks[i];
            }
        }

        return sum;
    }

    private static void ComputeNewRanks(
        double[] newRanks,
        double[] ranks,
        List<int>[] incoming,
        List<int>[] outgoing,
        double baseFactor,
        double danglingContrib,
        double dampingFactor,
        int n)
    {
        for (int i = 0; i < n; i++)
        {
            double incomingSum = 0.0;
            var span = CollectionsMarshal.AsSpan(incoming[i]);
            foreach (ref readonly int j in span)
            {
                incomingSum += ranks[j] / outgoing[j].Count;
            }

            newRanks[i] = baseFactor + danglingContrib + (dampingFactor * incomingSum);
        }
    }

    private static double MaxDelta(double[] a, double[] b, int n)
    {
        double max = 0.0;
        for (int i = 0; i < n; i++)
        {
            double diff = Math.Abs(b[i] - a[i]);
            if (diff > max)
            {
                max = diff;
            }
        }

        return max;
    }

    private static Dictionary<string, double> BuildResult(IReadOnlyList<GraphEntity> entities, double[] ranks)
    {
        var result = new Dictionary<string, double>(entities.Count, StringComparer.Ordinal);
        for (int i = 0; i < entities.Count; i++)
        {
            result[entities[i].Name] = ranks[i];
        }

        return result;
    }
}
