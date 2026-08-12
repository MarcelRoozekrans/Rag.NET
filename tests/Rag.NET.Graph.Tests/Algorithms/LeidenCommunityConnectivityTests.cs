using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

/// <summary>
/// Measures the guarantee <see cref="Leiden"/>'s remarks say it does not provide: that every
/// returned community is connected in the subgraph it induces.
/// </summary>
/// <remarks>
/// <para>
/// <b>A disclaimer nobody has tested is a guess.</b> The type documents that a community can come
/// back internally disconnected — the defect the Leiden paper exists to remove from Louvain — and
/// until this file that was an argument from reading the code rather than a measurement. It is now
/// a measurement, and the answer is that the defect is real:
/// <see cref="Detect_TheRecordedCounterexample_StillReturnsADisconnectedCommunity"/> holds a
/// ten-node tree on which a returned community has a member with no edge to any other member.
/// </para>
/// <para>
/// <b>Where it bites, from a sweep of about 30,000 detections.</b> Nothing dense produced one:
/// Erdős–Rényi (7,485 runs), heavy-tailed weighted Erdős–Rényi (2,400), planted partitions (2,700),
/// barbells (960), hub-and-blob shapes (3,360) and paths (1,560) all came back wholly connected.
/// Sparse weighted graphs do produce them — 48 of 2,220 random weighted trees, and 14 of 520
/// tree-plus-chords graphs, held a disconnected community for at least one resolution and seed.
/// <b>Unweighted trees produced none in 2,220 graphs</b>, so it is the weights, not the sparsity
/// alone, that opens the gap: a heavy edge elsewhere is what pays a node enough to abandon the
/// neighbours that were only attached through it.
/// </para>
/// <para>
/// <b>What that does and does not mean for a corpus.</b> An extracted entity graph is sparse and
/// weighted, which is the family that produces these; but the rate is low and the fragments are
/// small, and no measurement here says how often it happens on real data — the MultiHop-RAG slice
/// has never been checked for it. Two of these tests assert the clique shapes stay connected, which
/// is the common case holding, not the guarantee holding.
/// </para>
/// </remarks>
public class LeidenCommunityConnectivityTests
{
    /// <summary>
    /// A ten-node weighted tree on which community detection returns a disconnected community.
    /// </summary>
    /// <remarks>
    /// Found by sweeping random weighted trees, then reduced to whole-number weights that still
    /// reproduce. At <see cref="LeidenOptions.Resolution"/> 1.0 — the default — and seed 1, the
    /// returned communities are <c>{N0,N2,N3,N4,N7,N8}</c> and <c>{N1,N5,N6,N9}</c>, and the second
    /// is not connected: <c>N5</c>'s only edge is to <c>N0</c>, which is in the other community, so
    /// it sits in a community with three nodes it does not touch. The general mechanism is that a
    /// node may leave a sub-community it was the only link through, and nothing puts the remainder
    /// back together.
    /// </remarks>
    private static readonly (int From, int To, double Weight)[] CounterexampleTree =
    [
        (0, 1, 6.0), (0, 2, 3.0), (2, 3, 3.0), (1, 4, 4.0), (0, 5, 1.0),
        (1, 6, 4.0), (0, 7, 5.0), (4, 8, 6.0), (6, 9, 4.0),
    ];

    /// <summary>
    /// The recorded counterexample must stay a counterexample, on the model of
    /// <c>FeatureClaimTests.EveryRecordedFalseClaimIsStillFalse</c>: a recorded defect cannot
    /// outlive the defect it records.
    /// </summary>
    /// <remarks>
    /// <b>If this test fails, that is probably good news, and it is not a licence to edit it.</b> It
    /// fails when this graph stops producing a disconnected community — which is what implementing
    /// the Leiden paper's refinement would do. In that case delete this test, delete the paragraph
    /// in <see cref="Leiden"/>'s remarks that says the guarantee is absent, and assert the general
    /// property instead. It also fails if the move order changes for unrelated reasons, in which
    /// case the guarantee is still absent and the honest repair is to find the counterexample again
    /// rather than to weaken the assertion.
    /// </remarks>
    [Fact]
    public void Detect_TheRecordedCounterexample_StillReturnsADisconnectedCommunity()
    {
        var graph = BuildGraph(10, CounterexampleTree);

        var communities = Leiden.Detect(graph, new LeidenOptions { RandomSeed = 1 });

        var disconnected = new List<string>();
        foreach (var community in communities)
        {
            if (!IsConnected(graph, community.MemberEntities))
            {
                disconnected.Add(string.Join(",", community.MemberEntities));
            }
        }

        Assert.Equal(["N1,N5,N6,N9"], disconnected);
    }

    [Fact]
    public void Detect_TenCliquesInARing_EveryCommunityIsConnected()
    {
        var graph = BuildCliques(10, 10, bridged: true);
        AssertEveryCommunityIsConnected(graph, new LeidenOptions());
    }

    [Fact]
    public void Detect_TwoCliquesJoinedByOneBridge_EveryCommunityIsConnected()
    {
        var edges = new List<(int, int, double)>();
        AddClique(edges, 0, 10);
        AddClique(edges, 10, 10);
        edges.Add((0, 10, 1.0));
        AssertEveryCommunityIsConnected(BuildGraph(20, [.. edges]), new LeidenOptions());
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
        AssertEveryCommunityIsConnected(BuildGraph(12, [.. edges]), new LeidenOptions());
    }

    [Fact]
    public void Detect_TwoDisjointCliques_EveryCommunityIsConnected()
    {
        AssertEveryCommunityIsConnected(BuildCliques(2, 8, bridged: false), new LeidenOptions());
    }

    /// <summary>
    /// The dense case holds across resolutions and seeds, which is what makes the tree above a
    /// finding about sparse weighted graphs rather than about the algorithm falling over generally.
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
            AssertEveryCommunityIsConnected(graph, new LeidenOptions { Resolution = resolution, RandomSeed = seed });
        }
    }

    private static void AssertEveryCommunityIsConnected(GraphSnapshot graph, LeidenOptions options)
    {
        foreach (var community in Leiden.Detect(graph, options))
        {
            Assert.True(
                IsConnected(graph, community.MemberEntities),
                $"Community {{{string.Join(",", community.MemberEntities)}}} is not connected in the subgraph it induces.");
        }
    }

    /// <summary>
    /// Whether the members induce a connected subgraph, using the same endpoint matching
    /// <see cref="Leiden"/> itself uses so that an edge it counted is an edge here.
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
