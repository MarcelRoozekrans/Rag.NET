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
}
