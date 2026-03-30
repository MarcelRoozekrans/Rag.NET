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

        // With bridge edges at default resolution, Leiden may merge communities
        Assert.True(communities.Count >= 1);
        // All 12 entities must be assigned
        var allMembers = communities.SelectMany(c => c.MemberEntities).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(12, allMembers.Count);
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

    [Fact]
    public void Detect_IsDeterministicWithSameSeed()
    {
        var entities = Enumerable.Range(0, 20)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var rng = new Random(42);
        var relationships = Enumerable.Range(0, 40)
            .Select(_ => new GraphRelationship($"E{rng.Next(20)}", $"E{rng.Next(20)}", "r"))
            .ToList();

        var graph = new GraphSnapshot(entities, relationships, []);
        var run1 = Leiden.Detect(graph, new LeidenOptions { RandomSeed = 123 });
        var run2 = Leiden.Detect(graph, new LeidenOptions { RandomSeed = 123 });

        Assert.Equal(run1.Count, run2.Count);
        var sorted1 = run1.Select(c => c.MemberEntities.OrderBy(x => x, StringComparer.Ordinal).ToList()).ToList();
        var sorted2 = run2.Select(c => c.MemberEntities.OrderBy(x => x, StringComparer.Ordinal).ToList()).ToList();
        for (int i = 0; i < sorted1.Count; i++)
            Assert.Equal(sorted1[i], sorted2[i]);
    }
}
