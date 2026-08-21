using System.Diagnostics;
using Rag.NET.Models;
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Tests;

[Collection("Telemetry")]
public class RaptorTelemetryTests
{
    private readonly RaptorTestContext _helpers = new();

    [Fact]
    public async Task HandleAsync_EmitsBuildAndSummarizeSpansNestedTogether()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 6, embeddingDims: 8);

        _helpers.SetupChatClient("Summary of cluster");
        _helpers.SetupEmbedder(8);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();

        var buildSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.raptor.build", StringComparison.Ordinal));
        Assert.NotNull(buildSpan);
        Assert.Equal("test-doc", buildSpan.GetTagItem("document.id"));
        Assert.Equal(1, buildSpan.GetTagItem("raptor.tree.depth"));

        var summarizeSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.raptor.summarize", StringComparison.Ordinal));
        Assert.NotNull(summarizeSpan);
        Assert.Equal(1, summarizeSpan.GetTagItem("raptor.tree.level"));
        Assert.Equal(6, summarizeSpan.GetTagItem("raptor.chunk.count"));
        // ragnet.raptor.summarize is emitted from inside the ragnet.raptor.build activity scope.
        Assert.Equal(buildSpan.SpanId.ToString(), summarizeSpan.ParentSpanId.ToString());
    }

    /// <summary>
    /// The corpus path used to pass <c>activity: null</c> and open no span at all, so
    /// <c>raptor.tree.depth</c> and <c>raptor.summary.count</c> never existed under the shipped
    /// default (<see cref="RaptorTreeScope.Corpus"/>) and <c>ragnet.raptor.summarize</c> nested
    /// under whatever ambient activity happened to be current instead of its own parent — while
    /// <c>docs/reference/opentelemetry.md</c> kept documenting <c>ragnet.raptor.build</c> as that
    /// parent regardless (I2).
    /// </summary>
    [Fact]
    public async Task HandleAsync_UnderCorpusScope_EmitsBuildSpanTaggedWithTheReservedCorpusId()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var ctx = _helpers.CreateContext(chunkCount: 12);

        await behavior.HandleAsync(ctx, TestContext.Current.CancellationToken,
            static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();

        var buildSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.raptor.build", StringComparison.Ordinal));
        Assert.NotNull(buildSpan);
        Assert.Equal(RaptorCorpusDocumentId.Value, buildSpan.GetTagItem("document.id"));
        Assert.NotNull(buildSpan.GetTagItem("raptor.tree.depth"));
        Assert.NotNull(buildSpan.GetTagItem("raptor.summary.count"));
    }
}
