using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

public class LeidenTests
{
    [Fact]
    public void Detect_TwoDisconnectedCliques_FindsTwoCommunities()
    {
        var entities = Enumerable.Range(0, 8)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        for (int i = 4; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);

        Assert.Equal(2, communities.Count);
        var c0 = communities[0].MemberEntities.ToHashSet(StringComparer.Ordinal);
        var c1 = communities[1].MemberEntities.ToHashSet(StringComparer.Ordinal);
        Assert.True(
            (c0.SetEquals(["E0", "E1", "E2", "E3"]) && c1.SetEquals(["E4", "E5", "E6", "E7"])) ||
            (c1.SetEquals(["E0", "E1", "E2", "E3"]) && c0.SetEquals(["E4", "E5", "E6", "E7"])));
    }

    [Fact]
    public void Detect_SingleNode_ReturnsSingleCommunity()
    {
        var graph = new GraphSnapshot([new GraphEntity("A", "Node", "A")], [], []);
        var communities = Leiden.Detect(graph);
        Assert.Single(communities);
        Assert.Single(communities[0].MemberEntities);
    }

    [Fact]
    public void Detect_EmptyGraph_ReturnsEmpty()
    {
        var graph = new GraphSnapshot([], [], []);
        var communities = Leiden.Detect(graph);
        Assert.Empty(communities);
    }

    [Fact]
    public void Detect_FullyConnected_ReturnsSingleCommunity()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 5; i++)
            for (int j = i + 1; j < 5; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);
        Assert.Single(communities);
        Assert.Equal(5, communities[0].MemberEntities.Count);
    }

    [Fact]
    public void Detect_ThreeCliquesWithBridges_FindsThreeCommunities()
    {
        var entities = Enumerable.Range(0, 12)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int c = 0; c < 3; c++)
            for (int i = c * 4; i < c * 4 + 4; i++)
                for (int j = i + 1; j < c * 4 + 4; j++)
                    relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        relationships.Add(new GraphRelationship("E3", "E4", "bridge"));
        relationships.Add(new GraphRelationship("E7", "E8", "bridge"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);

        Assert.Equal(3, communities.Count);
        // All 12 entities must be assigned
        var allMembers = communities.SelectMany(c => c.MemberEntities).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(12, allMembers.Count);
    }

    /// <summary>
    /// Two cliques joined by a single edge are two communities.
    /// </summary>
    /// <remarks>
    /// <b>The smallest graph on which merging is unambiguously wrong.</b> Twenty nodes, ninety
    /// edges, and exactly one of them crossing: no resolution worth the name calls that one group.
    /// It is the minimal case of the defect
    /// <see cref="Detect_TenCliquesInARing_FindsTenCommunities"/> shows at scale, and it is here
    /// because a one-line reproduction is what a future regression will be debugged against.
    /// </remarks>
    [Fact]
    public void Detect_TwoCliquesJoinedByOneBridge_FindsTwoCommunities()
    {
        var entities = Enumerable.Range(0, 20)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 10; i++)
            for (int j = i + 1; j < 10; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        for (int i = 10; i < 20; i++)
            for (int j = i + 1; j < 20; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        relationships.Add(new GraphRelationship("E0", "E10", "bridge"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);

        Assert.Equal(2, communities.Count);
    }

    /// <summary>
    /// Ten cliques in a ring, each joined to the next by one edge, are ten communities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a property of Leiden, not a tuning target.</b> Each clique holds 45 internal edges
    /// and spends 2 on the ring, so the partition into ten is the modularity optimum by an enormous
    /// margin, and every published implementation recovers it. It is stated as an equality for that
    /// reason: a bound like <c>Count &gt;= 1</c> is satisfied by returning one community of 100,
    /// which is exactly what this implementation used to do.
    /// </para>
    /// <para>
    /// <b>What it caught.</b> <c>BuildAggregatedEdges</c> discarded every edge whose endpoints fell
    /// in the same community instead of folding it into a self-loop on the super-node. Modularity's
    /// null model is driven by each node's total incident weight, so a super-node that has thrown
    /// its 45 internal edges away looks 45 edges lighter than it is; the penalty for merging it into
    /// a neighbour collapses, merging always pays, and each aggregation level swallows the last.
    /// The corpus symptom was a single community holding 8,070 of 8,999 entities, but no corpus is
    /// needed to see it — a ring of cliques came back as one community of 100.
    /// </para>
    /// </remarks>
    [Fact]
    public void Detect_TenCliquesInARing_FindsTenCommunities()
    {
        const int cliques = 10;
        const int size = 10;
        var entities = new List<GraphEntity>();
        for (int c = 0; c < cliques; c++)
            for (int i = 0; i < size; i++)
                entities.Add(new GraphEntity($"C{c}N{i}", "Node", $"Node {i} of clique {c}"));

        var relationships = new List<GraphRelationship>();
        for (int c = 0; c < cliques; c++)
            for (int i = 0; i < size; i++)
                for (int j = i + 1; j < size; j++)
                    relationships.Add(new GraphRelationship($"C{c}N{i}", $"C{c}N{j}", "in clique"));
        for (int c = 0; c < cliques; c++)
            relationships.Add(new GraphRelationship($"C{c}N0", $"C{(c + 1) % cliques}N0", "bridge"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);

        Assert.Equal(cliques, communities.Count);
        foreach (var community in communities)
        {
            Assert.Equal(size, community.MemberEntities.Count);
        }
    }

    [Fact]
    public void Detect_ResolutionParameter_AffectsGranularity()
    {
        var entities = Enumerable.Range(0, 8)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected", 1.0));
        for (int i = 4; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected", 1.0));
        relationships.Add(new GraphRelationship("E3", "E4", "weak bridge", 0.1));

        var graph = new GraphSnapshot(entities, relationships, []);
        var lowRes = Leiden.Detect(graph, new LeidenOptions { Resolution = 0.5 });
        var highRes = Leiden.Detect(graph, new LeidenOptions { Resolution = 2.0 });

        Assert.True(highRes.Count >= lowRes.Count);
    }

    /// <summary>
    /// One seed is one partition, however many times it is run.
    /// </summary>
    /// <remarks>
    /// <b>This became load-bearing when the refinement became randomised.</b> The merge target is now
    /// drawn, not chosen, so every pinned figure in this repository — community counts on the
    /// MultiHop-RAG slice, the largest-community ceiling, the singleton count — rests on
    /// <see cref="LeidenOptions.RandomSeed"/> reaching every draw and on the number of
    /// draws being a function of the graph alone. Five runs rather than two because a discipline that
    /// leaked state would not necessarily leak it on the first repeat.
    /// </remarks>
    [Fact]
    public void Detect_IsDeterministicWithSameSeed()
    {
        var graph = BuildRandomGraph(nodes: 60, edges: 200, seed: 42);

        string first = Describe(
            Leiden.Detect(graph, new LeidenOptions { RandomSeed = 123 }));

        for (int repeat = 0; repeat < 4; repeat++)
        {
            Assert.Equal(
                first,
                Describe(Leiden.Detect(
                    graph, new LeidenOptions { RandomSeed = 123 })));
        }
    }

    /// <summary>
    /// <see cref="LeidenOptions.Randomness"/> reaches the algorithm.
    /// </summary>
    /// <remarks>
    /// <b>A setting nothing reads is the shape of defect this repository keeps finding</b> — three
    /// dead settings in the #108 audit, and the whole of
    /// <see cref="LeidenOptions"/> unreachable through <c>UseGraphRag</c> before that.
    /// θ is a divisor inside <c>exp(ΔQ / θ)</c>, so a large value flattens the draw toward uniform
    /// over every legal merge and a small one concentrates it on the best; if the two produce the
    /// same partition on a graph with this many near-tied merges, the parameter is not being read.
    /// </remarks>
    [Fact]
    public void Detect_RandomnessChangesTheDraw()
    {
        var graph = BuildRandomGraph(nodes: 60, edges: 200, seed: 7);

        string concentrated = Describe(Leiden.Detect(
            graph, new LeidenOptions { RandomSeed = 5, Randomness = 0.0001 }));
        string flattened = Describe(Leiden.Detect(
            graph, new LeidenOptions { RandomSeed = 5, Randomness = 100.0 }));

        Assert.NotEqual(concentrated, flattened, StringComparer.Ordinal);
    }

    /// <summary>A θ the draw cannot use is refused where it is set, not where it is divided by.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Randomness_ValueTheDrawCannotUse_Throws(double value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new LeidenOptions { Randomness = value });

        Assert.Contains(nameof(LeidenOptions.Randomness), exception.Message, StringComparison.Ordinal);
    }

    private static GraphSnapshot BuildRandomGraph(int nodes, int edges, int seed)
    {
        var entities = Enumerable.Range(0, nodes)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var rng = new Random(seed);
        var relationships = Enumerable.Range(0, edges)
            .Select(_ => new GraphRelationship(
                $"E{rng.Next(nodes)}", $"E{rng.Next(nodes)}", "r", 1.0 + rng.Next(5)))
            .ToList();

        return new GraphSnapshot(entities, relationships, []);
    }

    /// <summary>Renders a partition so that two runs compare as strings and nothing else varies.</summary>
    private static string Describe(IReadOnlyList<Community> communities)
    {
        var rendered = communities
            .Select(c => string.Join(",", c.MemberEntities.OrderBy(x => x, StringComparer.Ordinal)))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return string.Join(" | ", rendered);
    }

    [Fact]
    public void Detect_WithSelfLoops_IgnoresSelfLoops()
    {
        var entities = new[] { new GraphEntity("A", "Node", "A"), new GraphEntity("B", "Node", "B") };
        var relationships = new List<GraphRelationship>
        {
            new("A", "A", "self-loop"),  // self-loop
            new("A", "B", "connected"),
        };
        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);
        // Should not crash; should still find communities
        Assert.NotEmpty(communities);
        Assert.Equal(2, communities.SelectMany(c => c.MemberEntities).Count());
    }

    [Fact]
    public void Detect_IsolatedNodes_EachGetsCommunity()
    {
        var entities = new[]
        {
            new GraphEntity("A", "Node", "A"), new GraphEntity("B", "Node", "B"),
            new GraphEntity("C", "Node", "C"),
        };
        // No relationships at all
        var graph = new GraphSnapshot(entities, [], []);
        var communities = Leiden.Detect(graph);
        var allMembers = communities.SelectMany(c => c.MemberEntities).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, allMembers.Count); // all entities assigned
    }

    /// <summary>
    /// An edge whose endpoint spells an entity with different casing is still an edge.
    /// </summary>
    /// <remarks>
    /// <b>The clusterer and the graph store have to agree on this, and they did not.</b>
    /// <see cref="SqliteGraphStore"/>'s <c>name</c> column is <c>COLLATE NOCASE</c> — two spellings
    /// are one row, and it cannot hold "Google" and "google" as separate entities — and its
    /// neighbour queries match endpoints with <c>COLLATE NOCASE</c> too, so it traverses this edge.
    /// <see cref="Leiden"/> matched endpoints to names with <see cref="StringComparer.Ordinal"/> and
    /// therefore dropped it. Over the sixty-article MultiHop-RAG slice that silently cost the
    /// clustering real structure: 475 of 655 communities held a single entity.
    /// </remarks>
    [Fact]
    public void Detect_EndpointCasingDiffersFromEntityName_StillJoinsThem()
    {
        var entities = new[]
        {
            new GraphEntity("Google", "Organisation", "A search company"),
            new GraphEntity("Alphabet", "Organisation", "Its holding company"),
        };
        var relationships = new[] { new GraphRelationship("google", "Alphabet", "subsidiary of") };

        var communities = Leiden.Detect(new GraphSnapshot(entities, relationships, []));

        Assert.Single(communities);
        Assert.Equal(2, communities[0].MemberEntities.Count);
    }

    [Fact]
    public void Detect_MaxLevelsOne_StopsAfterOneLevel()
    {
        var entities = Enumerable.Range(0, 8)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        for (int i = 4; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph, new LeidenOptions { MaxLevels = 1 });
        // Should still find communities, MaxLevels caps recursion
        Assert.NotEmpty(communities);
    }
}
