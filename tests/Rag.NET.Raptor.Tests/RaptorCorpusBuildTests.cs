using System.Linq;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorCorpusBuildTests
{
    private readonly RaptorTestContext _helpers = new();

    [Fact]
    public async Task CorpusBuild_ProducesATree_OverDocumentsTooShortForPerDocumentScope()
    {
        // Each document has 2 chunks — below MinChunksForRaptor (5), so per-document scope
        // builds nothing at all. Corpus scope sees 20 chunks and must build a tree.
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);

        for (var i = 0; i < 10; i++)
        {
            var ctx = _helpers.CreateContext(chunkCount: 2, documentId: $"doc-{i}");
            await behavior.HandleAsync(ctx, CancellationToken.None, static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));
        }

        var final = _helpers.CreateContext(chunkCount: 0, documentId: "trigger");
        var summaryCount = await behavior.BuildCorpusTreeNowAsync(final, TestContext.Current.CancellationToken);

        Assert.True(summaryCount > 0, "corpus scope must build a tree over documents no single one of which qualifies");
        Assert.All(
            final.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")),
            c => Assert.Equal(RaptorCorpusDocumentId.Value, c.Chunk.DocumentId.Value));
    }

    [Fact]
    public async Task CorpusSummaries_HaveUniqueChunkIndexes_AcrossEveryLevel()
    {
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var ctx = _helpers.CreateContext(chunkCount: 24, documentId: "doc-a");
        await behavior.HandleAsync(ctx, CancellationToken.None, static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var target = _helpers.CreateContext(chunkCount: 0, documentId: "trigger");
        await behavior.BuildCorpusTreeNowAsync(target, TestContext.Current.CancellationToken);

        var indexes = target.EmbeddedChunks.Select(c => c.Chunk.ChunkIndex).ToList();

        // Without this the uniqueness assertion below holds trivially on an empty list, so the
        // test would stay green if the corpus tree stopped being built altogether — the same
        // vacuous-pass its per-document sibling guards against.
        Assert.True(indexes.Count > 0, "corpus build produced no summaries, so uniqueness proves nothing");
        Assert.Equal(indexes.Count, indexes.Distinct().Count());
    }

    [Fact]
    public async Task CorpusBuild_DoesNotRebuild_UntilTheCorpusGrowsPastTheThreshold()
    {
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0.50 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);

        var first = _helpers.CreateContext(chunkCount: 20, documentId: "doc-0");
        await behavior.HandleAsync(first, CancellationToken.None, static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));
        var callsAfterFirst = _helpers.ChatClient.ReceivedCalls().Count();

        // One more chunk is 5% growth, well under the 50% threshold.
        var second = _helpers.CreateContext(chunkCount: 1, documentId: "doc-1");
        await behavior.HandleAsync(second, CancellationToken.None, static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(callsAfterFirst, _helpers.ChatClient.ReceivedCalls().Count());
        Assert.DoesNotContain(second.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
    }
}
