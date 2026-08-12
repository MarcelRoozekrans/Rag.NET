using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

/// <summary>
/// Measures the guarantee <see cref="LouvainWithRefinement"/> claims: that every returned community
/// is connected in the subgraph it induces.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file used to assert the opposite, and the history is the point.</b> The type documented
/// that a community could come back internally disconnected — the defect the Leiden paper exists to
/// remove from Louvain — and this file pinned a ten-node weighted tree that demonstrated it, on the
/// model of <c>FeatureClaimTests.EveryRecordedFalseClaimIsStillFalse</c>: a recorded defect may not
/// outlive the defect it records. Implementing the paper's refinement phase (#180) removed the
/// defect, so the recording was deleted rather than weakened, and the same graph is now asserted the
/// other way round by
/// <see cref="Detect_TheGraphThatUsedToReturnADisconnectedCommunity_ReturnsOnlyConnectedOnes"/>.
/// </para>
/// <para>
/// <b>Where it used to bite, which is where a regression would show first.</b> A sweep of about
/// 30,000 detections against the old implementation found nothing on anything dense — Erdős–Rényi,
/// planted partitions, barbells, hub-and-blob shapes and paths all came back wholly connected — and
/// found the failures on sparse <i>weighted</i> graphs: 48 of 2,220 random weighted trees and 14 of
/// 520 tree-plus-chords graphs held a disconnected community for some resolution and seed, against
/// <b>0 of 2,220 unweighted trees</b>. It was the weights, not the sparsity alone: a heavy edge
/// elsewhere is what paid a node enough to abandon the neighbours that were only attached through
/// it. That family is therefore the one worth sweeping again after any change to the refinement, and
/// <see cref="Detect_RandomWeightedTrees_EveryCommunityIsConnected"/> keeps a sample of it in the
/// suite.
/// </para>
/// </remarks>
public class CommunityConnectivityTests
{
    /// <summary>
    /// The ten-node weighted tree that used to produce a disconnected community.
    /// </summary>
    /// <remarks>
    /// Found by sweeping random weighted trees, then reduced to whole-number weights that still
    /// reproduced. At <see cref="LouvainWithRefinementOptions.Resolution"/> 1.0 — the default — and
    /// seed 1, the old implementation returned <c>{N0,N2,N3,N4,N7,N8}</c> and <c>{N1,N5,N6,N9}</c>,
    /// and the second was not connected: <c>N5</c>'s only edge is to <c>N0</c>, which sat in the
    /// other community, so it shared a community with three nodes it does not touch. The mechanism
    /// was that a node could leave a sub-community it was the only link through, with nothing to put
    /// the remainder back together. <b>The graph is kept because it is the hard case</b>, not because
    /// the partition it now produces is interesting.
    /// </remarks>
    private static readonly (int From, int To, double Weight)[] HardTree =
    [
        (0, 1, 6.0), (0, 2, 3.0), (2, 3, 3.0), (1, 4, 4.0), (0, 5, 1.0),
        (1, 6, 4.0), (0, 7, 5.0), (4, 8, 6.0), (6, 9, 4.0),
    ];

    /// <summary>
    /// The graph the defect was recorded on returns only connected communities, at the settings it
    /// was recorded at and across a spread of others.
    /// </summary>
    /// <remarks>
    /// The default resolution and seed 1 are the settings the counterexample was pinned at, and are
    /// asserted first so a regression names them. The rest are there because the guarantee is not a
    /// property of one seed: the refinement draws its merge targets at random, so a seed that
    /// happens to work says nothing on its own.
    /// </remarks>
    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(1.0, 42)]
    [InlineData(0.5, 7)]
    [InlineData(2.0, 2026)]
    public void Detect_TheGraphThatUsedToReturnADisconnectedCommunity_ReturnsOnlyConnectedOnes(
        double resolution, int seed)
    {
        var graph = BuildGraph(10, HardTree);

        AssertEveryCommunityIsConnected(
            graph, new LouvainWithRefinementOptions { Resolution = resolution, RandomSeed = seed });
    }

    /// <summary>
    /// The family the counterexample was drawn from — random weighted trees — comes back connected.
    /// </summary>
    /// <remarks>
    /// <b>One pinned graph is a regression test, not evidence of a guarantee.</b> The old
    /// implementation failed on roughly one in fifty random weighted trees when swept across
    /// resolutions and seeds, so a few hundred of them is enough to be very unlikely to pass by luck
    /// while staying inside a unit test's time budget. The wider sweep — hundreds of thousands of
    /// communities — is not run here; this is the tripwire, not the measurement.
    /// </remarks>
    [Fact]
    public void Detect_RandomWeightedTrees_EveryCommunityIsConnected()
    {
        var rng = new Random(20260812);
        for (int sample = 0; sample < 300; sample++)
        {
            int nodes = 6 + rng.Next(15);
            var edges = new List<(int, int, double)>(nodes - 1);
            for (int node = 1; node < nodes; node++)
            {
                edges.Add((rng.Next(node), node, 1.0 + (rng.Next(10) * 1.0)));
            }

            AssertEveryCommunityIsConnected(
                BuildGraph(nodes, [.. edges]),
                new LouvainWithRefinementOptions { RandomSeed = sample + 1 });
        }
    }

    [Fact]
    public void Detect_TenCliquesInARing_EveryCommunityIsConnected()
    {
        var graph = BuildCliques(10, 10, bridged: true);
        AssertEveryCommunityIsConnected(graph, new LouvainWithRefinementOptions());
    }

    [Fact]
    public void Detect_TwoCliquesJoinedByOneBridge_EveryCommunityIsConnected()
    {
        var edges = new List<(int, int, double)>();
        AddClique(edges, 0, 10);
        AddClique(edges, 10, 10);
        edges.Add((0, 10, 1.0));
        AssertEveryCommunityIsConnected(BuildGraph(20, [.. edges]), new LouvainWithRefinementOptions());
    }

    [Fact]
    public void Detect_ThreeCliquesWithBridges_EveryCommunityIsConnected()
    {
        var edges = new List<(int, int, double)>();
        AddClique(edges, 0, 4);
        AddClique(edges, 4, 4);
        AddClique(edges, 8, 4);
        edges.Add((3, 4, 1.0));
        edges.Add((7, 8, 1.0));
        AssertEveryCommunityIsConnected(BuildGraph(12, [.. edges]), new LouvainWithRefinementOptions());
    }

    [Fact]
    public void Detect_TwoDisjointCliques_EveryCommunityIsConnected()
    {
        AssertEveryCommunityIsConnected(BuildCliques(2, 8, bridged: false), new LouvainWithRefinementOptions());
    }

    /// <summary>
    /// The dense case holds across resolutions and seeds, as it did before the refinement was fixed —
    /// which is what makes the tree above the interesting shape rather than the only one that works.
    /// </summary>
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 42)]
    [InlineData(2.0, 99)]
    [InlineData(1.0, 2026)]
    public void Detect_PlantedPartitions_EveryCommunityIsConnected(double resolution, int seed)
    {
        for (int sample = 0; sample < 5; sample++)
        {
            var rng = new Random(sample + 1);
            var edges = new List<(int, int, double)>();
            const int blocks = 4;
            const int size = 8;
            for (int i = 0; i < blocks * size; i++)
            {
                for (int j = i + 1; j < blocks * size; j++)
                {
                    if (rng.NextDouble() < (i / size == j / size ? 0.6 : 0.04))
                    {
                        edges.Add((i, j, 1.0));
                    }
                }
            }

            var graph = BuildGraph(blocks * size, [.. edges]);
            AssertEveryCommunityIsConnected(graph, new LouvainWithRefinementOptions { Resolution = resolution, RandomSeed = seed });
        }
    }

    private static void AssertEveryCommunityIsConnected(GraphSnapshot graph, LouvainWithRefinementOptions options)
    {
        foreach (var community in LouvainWithRefinement.Detect(graph, options))
        {
            Assert.True(
                IsConnected(graph, community.MemberEntities),
                $"Community {{{string.Join(",", community.MemberEntities)}}} is not connected in the subgraph it induces.");
        }
    }

    /// <summary>
    /// Whether the members induce a connected subgraph, using the same endpoint matching
    /// <see cref="LouvainWithRefinement"/> itself uses so that an edge it counted is an edge here.
    /// </summary>
    private static bool IsConnected(GraphSnapshot graph, IReadOnlyList<string> members)
    {
        var adjacency = new Dictionary<string, List<string>>(GraphNames.Comparer);
        foreach (string member in members)
        {
            adjacency[member] = [];
        }

        foreach (var relationship in graph.Relationships)
        {
            if (GraphNames.Comparer.Equals(relationship.SourceEntity, relationship.TargetEntity) ||
                !adjacency.TryGetValue(relationship.SourceEntity, out var fromNeighbors) ||
                !adjacency.TryGetValue(relationship.TargetEntity, out var toNeighbors))
            {
                continue;
            }

            fromNeighbors.Add(relationship.TargetEntity);
            toNeighbors.Add(relationship.SourceEntity);
        }

        var reached = new HashSet<string>(GraphNames.Comparer) { members[0] };
        var pending = new Stack<string>();
        pending.Push(members[0]);
        while (pending.Count > 0)
        {
            foreach (string neighbor in adjacency[pending.Pop()])
            {
                if (reached.Add(neighbor))
                {
                    pending.Push(neighbor);
                }
            }
        }

        return reached.Count == members.Count;
    }

    private static void AddClique(List<(int, int, double)> edges, int start, int size)
    {
        for (int i = start; i < start + size; i++)
        {
            for (int j = i + 1; j < start + size; j++)
            {
                edges.Add((i, j, 1.0));
            }
        }
    }

    private static GraphSnapshot BuildCliques(int cliques, int size, bool bridged)
    {
        var edges = new List<(int, int, double)>();
        for (int c = 0; c < cliques; c++)
        {
            AddClique(edges, c * size, size);
        }

        if (bridged)
        {
            for (int c = 0; c < cliques; c++)
            {
                edges.Add((c * size, (c + 1) % cliques * size, 1.0));
            }
        }

        return BuildGraph(cliques * size, [.. edges]);
    }

    private static GraphSnapshot BuildGraph(int nodeCount, (int From, int To, double Weight)[] edges)
    {
        var entities = new List<GraphEntity>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            entities.Add(new GraphEntity($"N{i}", "Node", $"Node {i}"));
        }

        var relationships = new List<GraphRelationship>(edges.Length);
        foreach (var (from, to, weight) in edges)
        {
            relationships.Add(new GraphRelationship($"N{from}", $"N{to}", "connected", weight));
        }

        return new GraphSnapshot(entities, relationships, []);
    }
}
