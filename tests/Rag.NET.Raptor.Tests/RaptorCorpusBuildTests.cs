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

    /// <summary>
    /// A corpus build asks for the previous tree's append-only entries to be purged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the RAPTOR half of #336; <c>StorageBehavior</c> holds the other.</b> The tree is
    /// appended to the ingesting article's <c>EmbeddedChunks</c> but filed under
    /// <c>raptor://corpus-tree</c>, so nothing purged the previous tree's BM25 postings and every
    /// rebuild appended another full copy without bound.
    /// </para>
    /// <para>
    /// <b>The negative case matters as much as the positive one.</b> Registering the id when no
    /// tree was produced would purge the standing tree's postings and put nothing back — turning a
    /// duplication bug into a disappearance one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CorpusBuild_AsksForThePreviousTreesPostingsToBePurged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(ct);

        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);

        // First ingest: one document's worth of leaves, too few to cluster into a tree.
        var first = _helpers.CreateContext(chunkCount: 1, documentId: "doc-0");
        await behavior.HandleAsync(first, ct, static (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.DoesNotContain(RaptorCorpusDocumentId.Value, first.AdditionalAppendOnlyPurgeIds);

        // Second ingest: now the corpus has enough leaves, so a tree is built and appended.
        var second = _helpers.CreateContext(chunkCount: 4, documentId: "doc-1");
        await behavior.HandleAsync(second, ct, static (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Contains(RaptorCorpusDocumentId.Value, second.AdditionalAppendOnlyPurgeIds);
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

        // ShouldBuild always builds on the first call regardless of the threshold, so this must
        // be positive — otherwise the "no further calls happened" assertion below holds vacuously
        // on 0 == 0, which would stay green even if the first build silently stopped happening.
        Assert.True(callsAfterFirst > 0, "the first ingest must trigger a build, or this test proves nothing");

        // One more chunk is 5% growth, well under the 50% threshold.
        var second = _helpers.CreateContext(chunkCount: 1, documentId: "doc-1");
        await behavior.HandleAsync(second, CancellationToken.None, static (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(callsAfterFirst, _helpers.ChatClient.ReceivedCalls().Count());
        Assert.DoesNotContain(second.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
    }
}
