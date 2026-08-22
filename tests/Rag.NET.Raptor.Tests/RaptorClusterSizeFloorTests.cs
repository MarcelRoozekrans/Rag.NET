using System.Diagnostics;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// Covers <c>RaptorIngestionBehavior.SelectClusterCount</c>'s size floor (#345): the smallest
/// <c>k</c> the floor allows never drops below
/// <c>ceil(count / TargetClusterSize)</c>, so a level always fits at least that many
/// components, and its <i>average</i> cluster is at or under
/// <see cref="RaptorOptions.TargetClusterSize"/>. This is not a hard per-cluster bound: GMM
/// assignment can still put more than the target into one cluster and less into another, and an
/// empty component vanishes from the delivered clusters, so the count actually stored can be
/// lower than the floor. See <see cref="RaptorOptions.TargetClusterSize"/>'s remarks for the full
/// guarantee.
/// </summary>
[Collection("Telemetry")]
public class RaptorClusterSizeFloorTests
{
    private readonly RaptorTestContext _helpers = new();

    [Fact]
    public async Task ALevelLargerThanTheTarget_ProducesAtLeastTheFloorOfClusters()
    {
        // 600 leaves at a target of 100 needs at least 6 clusters. Before the floor, k was capped
        // at 10 by BIC's maxK and could be as low as 2 — a 300-chunk average cluster size with no
        // bound on any individual cluster's size (#345). The corpus case is 17,648 chunks against
        // the same cap. This asserts the cluster count the floor guarantees, not any individual
        // cluster's size: the floor bounds the average, not the maximum (see
        // RaptorOptions.TargetClusterSize's remarks).
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 100, MaxTreeDepth = 1 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 600);

        await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        var summaries = ctx.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.True(summaries.Count >= 6,
            $"600 leaves at a target of 100 needs at least 6 clusters; got {summaries.Count}");
    }

    [Fact]
    public async Task MaxClustersYieldsToTheFloor_WhenHonouringItWouldExceedTheTarget()
    {
        // MaxClusters is a preference; the size floor is a correctness bound. A cap of 2 over 600
        // leaves at a target of 100 would mean 300-chunk clusters and an unsendable prompt.
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 100, MaxClusters = 2, MaxTreeDepth = 1 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 600);

        await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        var summaries = ctx.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.True(summaries.Count >= 6,
            $"MaxClusters = 2 must yield to the floor of 6; got {summaries.Count} clusters");
    }

    [Fact]
    public async Task WhenMaxClustersYieldsToTheFloor_TheSummarizeSpanRecordsIt()
    {
        // A silently-exceeded cap is exactly what the doc comment promises not to do. The tag is how
        // a user finds out why their configured cap did not hold.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 100, MaxClusters = 2, MaxTreeDepth = 1 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 600);

        await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();
        var summarize = ours.Single(a => string.Equals(a.OperationName, "ragnet.raptor.summarize", StringComparison.Ordinal));
        Assert.Equal(true, summarize.GetTagItem("raptor.cluster.maxclusters.overridden"));
    }

    [Fact]
    public async Task ALevelFarAboveBicMaxK_DerivesKDirectlyWithoutFitting()
    {
        // 600 leaves at a target of 50 gives sizeFloor = 12, which is above BicMaxK (10) — the
        // branch every other test in this class misses, because all of them use TargetClusterSize
        // = 100 over 600 chunks (sizeFloor = 6 <= BicMaxK), which only ever exercises branches 1-3.
        // This is the branch corpus scale actually takes (17,648 chunks gives sizeFloor = 177), so
        // leaving it untested is the same shape of gap #345 itself came from — the fixtures cannot
        // produce the input that fails.
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 50, MaxTreeDepth = 1 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 600);

        await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        var summaries = ctx.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.True(summaries.Count >= 12,
            $"600 leaves at a target of 50 needs at least 12 clusters (sizeFloor = 12, derived " +
            $"directly since it exceeds BicMaxK); got {summaries.Count}");
    }

    [Fact]
    public async Task ALevelSmallerThanTheTarget_IsUnaffectedByTheFloor()
    {
        // The floor is 1 for both withFloor and wideTarget, so Math.Max(bic, 1) == bic and BIC
        // chooses alone for both — this is the regime every pre-existing test runs in, and it must
        // be untouched. A third configuration, bindingFloor, actually binds: TargetClusterSize = 2
        // over 24 chunks gives sizeFloor = 12, which is > BicMaxK, so it takes branch 4 and derives
        // k directly rather than deferring to BIC. Without it, Assert.Equal(a, b) below would
        // compare two sizeFloor = 1 computations to each other — a comparison that cannot fail
        // regardless of whether the floor exists at all.
        var withFloor = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 100, MaxTreeDepth = 1 };
        var wideTarget = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 10_000, MaxTreeDepth = 1 };
        var bindingFloor = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, TargetClusterSize = 2, MaxTreeDepth = 1 };

        var a = await SummaryCountAsync(withFloor, chunkCount: 24);
        var b = await SummaryCountAsync(wideTarget, chunkCount: 24);
        var c = await SummaryCountAsync(bindingFloor, chunkCount: 24);

        Assert.Equal(a, b);
        Assert.True(a > 0, "the fixture must actually build a level for this comparison to mean anything");
        Assert.NotEqual(a, c);
    }

    private async Task<int> SummaryCountAsync(RaptorOptions options, int chunkCount)
    {
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: chunkCount);

        await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        return ctx.EmbeddedChunks.Count(c => c.Chunk.Metadata.ContainsKey("raptor_level"));
    }
}
