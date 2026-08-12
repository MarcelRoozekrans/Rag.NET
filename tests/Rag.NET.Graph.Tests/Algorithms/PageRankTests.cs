using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

public class PageRankTests
{
    [Fact]
    public void Compute_StarGraph_CenterHasHighestRank()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        var relationships = Enumerable.Range(1, 4)
            .Select(i => new GraphRelationship($"E{i}", "E0", "points to"))
            .ToList();

        var graph = new GraphSnapshot(entities, relationships, []);
        var ranks = PageRank.Compute(graph);

        Assert.Equal(5, ranks.Count);
        Assert.True(ranks["E0"] > ranks["E1"]);
        Assert.True(ranks["E0"] > ranks["E2"]);
    }

    [Fact]
    public void Compute_EmptyGraph_ReturnsEmpty()
    {
        var graph = new GraphSnapshot([], [], []);
        var ranks = PageRank.Compute(graph);
        Assert.Empty(ranks);
    }

    [Fact]
    public void Compute_SingleNode_ReturnsOne()
    {
        var graph = new GraphSnapshot([new GraphEntity("A", "Node", "A")], [], []);
        var ranks = PageRank.Compute(graph);
        Assert.Equal(1.0, ranks["A"], precision: 5);
    }

    [Fact]
    public void Compute_ScoresSumToOne()
    {
        var entities = Enumerable.Range(0, 10)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        var rng = new Random(42);
        var relationships = Enumerable.Range(0, 20)
            .Select(_ => new GraphRelationship($"E{rng.Next(10)}", $"E{rng.Next(10)}", "r"))
            .ToList();

        var graph = new GraphSnapshot(entities, relationships, []);
        var ranks = PageRank.Compute(graph);

        Assert.InRange(ranks.Values.Sum(), 0.99, 1.01);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        var relationships = new List<GraphRelationship>
        {
            new("E0", "E1", "r"), new("E1", "E2", "r"),
            new("E2", "E3", "r"), new("E3", "E4", "r"),
            new("E4", "E0", "r"),
        };
        var graph = new GraphSnapshot(entities, relationships, []);

        var run1 = PageRank.Compute(graph);
        var run2 = PageRank.Compute(graph);

        foreach (var key in run1.Keys)
            Assert.Equal(run1[key], run2[key], precision: 10);
    }

    [Fact]
    public void Compute_WithSelfLoops_IgnoresSelfLoops()
    {
        var entities = new[] { new GraphEntity("A", "Node", "A"), new GraphEntity("B", "Node", "B") };
        var relationships = new List<GraphRelationship>
        {
            new("A", "A", "self-loop"),
            new("A", "B", "points to"),
        };
        var graph = new GraphSnapshot(entities, relationships, []);
        var ranks = PageRank.Compute(graph);
        Assert.Equal(2, ranks.Count);
        Assert.InRange(ranks.Values.Sum(), 0.99, 1.01);
    }

    /// <summary>
    /// An edge whose endpoint spells an entity with different casing still carries rank.
    /// </summary>
    /// <remarks>
    /// The clusterer's defect, in <see cref="PageRank"/>'s copy of the same adjacency build — see
    /// <see cref="GraphNames"/>. Matching endpoints ordinally left the target looking like an
    /// unreferenced node, so it scored the corpus's flat baseline instead of the rank the edge the
    /// store holds should have given it.
    /// </remarks>
    [Fact]
    public void Compute_EndpointCasingDiffersFromEntityName_StillCarriesRank()
    {
        var entities = new[]
        {
            new GraphEntity("Google", "Organisation", "A search company"),
            new GraphEntity("Alphabet", "Organisation", "Its holding company"),
        };
        var relationships = new[] { new GraphRelationship("google", "Alphabet", "subsidiary of") };

        var ranks = PageRank.Compute(new GraphSnapshot(entities, relationships, []));

        Assert.True(
            ranks["Alphabet"] > ranks["Google"],
            "the only edge in the graph points at Alphabet, so it must outrank its source");
    }

    [Fact]
    public void Compute_AllDanglingNodes_DistributesEqualRank()
    {
        // Nodes with no outgoing edges
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        var graph = new GraphSnapshot(entities, [], []);
        var ranks = PageRank.Compute(graph);
        // All nodes should have equal rank
        var expected = 1.0 / 5;
        foreach (var rank in ranks.Values)
            Assert.Equal(expected, rank, precision: 3);
    }
}
