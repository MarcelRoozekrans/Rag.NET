using System.Diagnostics;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

[Collection("Telemetry")]
public class GraphAlgorithmsTelemetryTests
{
    private static (List<Activity> Activities, ActivityListener Listener) CreateListener()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    [Fact]
    public void Detect_EmitsClusterSpanWithTags()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;
        using var parent = new Activity("test-parent").Start();

        var entities = Enumerable.Range(0, 4)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship> { new("E0", "E1", "connected") };
        var graph = new GraphSnapshot(entities, relationships, []);

        var communities = LouvainWithRefinement.Detect(graph);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.graph.cluster", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal(4, span.GetTagItem("graph.node.count"));
        Assert.Equal(1, span.GetTagItem("graph.relationship.count"));
        Assert.Equal(communities.Count, span.GetTagItem("graph.community.count"));
    }

    [Fact]
    public void Compute_EmitsPageRankSpanWithTags()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;
        using var parent = new Activity("test-parent").Start();

        var entities = new List<GraphEntity> { new("A", "Node", "A"), new("B", "Node", "B") };
        var relationships = new List<GraphRelationship> { new("A", "B", "connected") };
        var graph = new GraphSnapshot(entities, relationships, []);

        PageRank.Compute(graph);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.graph.pagerank", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal(2, span.GetTagItem("graph.node.count"));
        Assert.Equal(1, span.GetTagItem("graph.relationship.count"));
    }
}
