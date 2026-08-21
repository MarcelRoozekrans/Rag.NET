using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorIngestionBehaviorTests
{
    private readonly RaptorTestContext _helpers = new();

    [Fact]
    public async Task HandleAsync_WhenDisabled_CallsNextWithoutModification()
    {
        var options = new RaptorOptions { Enabled = false };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 10);
        var originalCount = ctx.EmbeddedChunks.Count;
        var nextCalled = false;

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }); });

        Assert.True(nextCalled);
        Assert.Equal(originalCount, ctx.EmbeddedChunks.Count);
    }

    [Fact]
    public async Task HandleAsync_BelowMinChunks_SkipsRaptor()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 10 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 5);
        var originalCount = ctx.EmbeddedChunks.Count;

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(originalCount, ctx.EmbeddedChunks.Count);
    }

    [Fact]
    public async Task HandleAsync_AddsSummaryChunksWithRaptorMetadata()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 6, embeddingDims: 8);

        _helpers.SetupChatClient("Summary of cluster");
        _helpers.SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var summaryChunks = ctx.EmbeddedChunks.Where(ec => ec.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.NotEmpty(summaryChunks);
        Assert.All(summaryChunks, sc =>
        {
            Assert.Equal<MetadataValue>("1", sc.Chunk.Metadata["raptor_level"]);
            Assert.True(sc.Chunk.Metadata.ContainsKey("raptor_cluster_id"));
            Assert.True(sc.Chunk.Metadata.ContainsKey("raptor_child_ids"));
        });
    }

    [Fact]
    public async Task HandleAsync_StoreLeafChunksFalse_RemovesOriginals()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1, StoreLeafChunks = false };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 6, embeddingDims: 8);

        _helpers.SetupChatClient("Summary");
        _helpers.SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.All(ctx.EmbeddedChunks, ec => Assert.True(ec.Chunk.Metadata.ContainsKey("raptor_level")));
    }

    [Fact]
    public async Task HandleAsync_RespectsMaxTreeDepth()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 2 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 20, embeddingDims: 8);

        _helpers.SetupChatClient("Summary");
        _helpers.SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var maxLevel = ctx.EmbeddedChunks
            .Where(ec => ec.Chunk.Metadata.ContainsKey("raptor_level"))
            .Select(ec => int.Parse(ec.Chunk.Metadata["raptor_level"].ToString(), System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(maxLevel <= 2);
    }

    [Fact]
    public async Task HandleAsync_UsesSummaryChatClientWhenProvided()
    {
        var customClient = Substitute.For<IChatClient>();
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1, SummaryChatClient = customClient };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 6, embeddingDims: 8);

        customClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Custom summary")]));
        _helpers.SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        await customClient.Received().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _helpers.ChatClient.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UsesSummaryEmbedderWhenProvided()
    {
        var customEmbedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var options = new RaptorOptions
        {
            MinChunksForRaptor = 2,
            ReducedDimensionality = 2,
            MaxTreeDepth = 1,
            SummaryEmbedder = customEmbedder,
        };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 6, embeddingDims: 8);

        _helpers.SetupChatClient("Summary");
        // Setup the CUSTOM embedder, not the default one
        customEmbedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                var rng = new Random(456);
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(
                        Enumerable.Range(0, 8).Select(_ => (float)rng.NextDouble()).ToArray())).ToList()));
            });

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        await customEmbedder.Received().GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        await _helpers.Embedder.DidNotReceive().GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithZeroChunks_SkipsRaptor()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 1 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 0);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Empty(ctx.EmbeddedChunks);
    }

    [Fact]
    public async Task HandleAsync_AtExactThreshold_AppliesRaptor()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 6, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 6, embeddingDims: 8);

        _helpers.SetupChatClient("Summary");
        _helpers.SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var summaryChunks = ctx.EmbeddedChunks.Where(ec => ec.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.NotEmpty(summaryChunks);
    }

    [Fact]
    public async Task SummaryChunks_HaveUniqueChunkIndexes_AcrossEveryTreeLevel()
    {
        // Used to force a depth-2 tree with chunkCount = 6 and rely on GaussianMixtureModel
        // .SelectK's #333 bug (it selects k = n for genuinely distinct points) to turn each of 6
        // leaves into its own level-1 "cluster", then each of those 6 summaries into its own
        // level-2 "cluster" — a non-reducing tree that only terminated because MaxTreeDepth
        // capped it. The non-reducing-level guard this task adds (BuildLevelAsync rejects any
        // level whose k would not shrink the count) now rejects that first level outright, and
        // that is by design: an infinite version of the same shape is exactly #333's defect.
        //
        // Sweeping chunkCount from 6 to 100 (with and without a UMAP-reducing
        // ReducedDimensionality) against this test harness's independently-random embeddings
        // never produced two consecutive non-degenerate levels: GMM/BIC over unclustered points
        // either picks k = 1 (no split) or k = n (rejected by the guard), and the rare
        // in-between k it does pick leaves too few points for a second real split. A genuine
        // depth-2 tree is not constructible against this bug, which is exactly why Task 6 exists
        // to fix it. Until then, this test asserts what the guard still allows to be checked:
        // #332's invariant — unique chunk indices — holds over however many levels a real (as
        // opposed to degenerate) build produces, including the single real level below.
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var ctx = _helpers.CreateContext(chunkCount: 15);
        var options = new RaptorOptions { ReducedDimensionality = 2, MaxTreeDepth = 2 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);

        await behavior.HandleAsync(ctx, CancellationToken.None,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var summaries = ctx.EmbeddedChunks
            .Where(c => c.Chunk.Metadata.ContainsKey("raptor_level"))
            .ToList();

        // Guards against the guard itself silently swallowing the level entirely, which would
        // make the ChunkKey-uniqueness assertion below pass vacuously.
        Assert.NotEmpty(summaries);

        var keys = ctx.EmbeddedChunks
            .Select(c => new ChunkKey(c.Chunk.DocumentId.Value, c.Chunk.ChunkIndex))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task TreeBuilding_Terminates_AtDefaultOptionsWithNoDepthCap()
    {
        // MaxTreeDepth deliberately left at its default null. Before the non-reducing-level guard
        // this hung forever (#333). The timeout is the assertion: a regression reintroducing
        // non-termination fails here rather than wedging the suite.
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var ctx = _helpers.CreateContext(chunkCount: 24);
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, new RaptorOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await behavior.HandleAsync(ctx, cts.Token, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        Assert.False(cts.IsCancellationRequested, "tree building did not terminate at default options");
    }
}
