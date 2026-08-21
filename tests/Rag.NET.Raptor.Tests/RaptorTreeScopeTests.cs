using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorTreeScopeTests
{
    private readonly RaptorTestContext _helpers = new();

    [Fact]
    public async Task CorpusScope_WritesLeavesToTheLeafStore_AndFilesAnyTreeUnderTheCorpusIdNotTheDocument()
    {
        // Renamed from "...AndBuildsNoPerDocumentTree" (Task 3): that assertion predated tree
        // building under Corpus scope. Task 4 adds the debounce, and ShouldBuild always builds
        // on the first call regardless of CorpusGrowthThreshold (mirroring
        // CommunityDetectionBehavior.ShouldDetect) — so this single ingest, being the first,
        // does trigger a corpus build. What Corpus scope still never does is build a tree
        // attributed to the ingesting document itself; that invariant is what this test now
        // checks.
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var ctx = _helpers.CreateContext(chunkCount: 12);

        await behavior.HandleAsync(ctx, CancellationToken.None,
            static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(12, await leafStore.CountAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            ctx.EmbeddedChunks,
            c => c.Chunk.Metadata.ContainsKey("raptor_level")
                && string.Equals(c.Chunk.DocumentId.Value, ctx.Metadata.DocumentId.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PerDocumentScope_WritesNothingToTheLeafStore()
    {
        // MaxTreeDepth is back at its null default. It was capped at 2 only because
        // GaussianMixtureModel.SelectK selected k = n for genuinely distinct points (#333), so an
        // unbounded depth never terminated; with that fixed the loop ends on its own when a level
        // stops reducing, and the cap no longer has a reason to be here. Running uncapped means
        // this test carries the same non-termination exposure as
        // TreeBuilding_Terminates_AtDefaultOptionsWithNoDepthCap, so it takes the same 30-second
        // guard: a regression should fail here, not wedge the suite.
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        _helpers.SetupChatClient("Summary");
        _helpers.SetupEmbedder(8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var ctx = _helpers.CreateContext(chunkCount: 12);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await behavior.HandleAsync(ctx, cts.Token,
            static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.False(cts.IsCancellationRequested, "tree building did not terminate at default options");
        Assert.Equal(0, await leafStore.CountAsync(TestContext.Current.CancellationToken));
        Assert.Contains(ctx.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
    }
}
