using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorTreeScopeTests
{
    private readonly RaptorTestContext _helpers = new();

    [Fact]
    public async Task CorpusScope_WritesLeavesToTheLeafStore_AndBuildsNoPerDocumentTree()
    {
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var ctx = _helpers.CreateContext(chunkCount: 12);

        await behavior.HandleAsync(ctx, CancellationToken.None,
            static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(12, await leafStore.CountAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(ctx.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
    }

    [Fact]
    public async Task PerDocumentScope_WritesNothingToTheLeafStore()
    {
        // MaxTreeDepth is capped at 2 rather than left at its null default: GaussianMixtureModel
        // .SelectK always selects k = n for genuinely distinct points (#333), so with an
        // unbounded depth the tree-building loop never terminates. Not this task's defect to fix.
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        _helpers.SetupChatClient("Summary");
        _helpers.SetupEmbedder(8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MaxTreeDepth = 2 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var ctx = _helpers.CreateContext(chunkCount: 12);

        await behavior.HandleAsync(ctx, CancellationToken.None,
            static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(0, await leafStore.CountAsync(TestContext.Current.CancellationToken));
        Assert.Contains(ctx.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
    }
}
