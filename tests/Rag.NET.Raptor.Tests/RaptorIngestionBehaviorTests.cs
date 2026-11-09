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
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 10 };
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
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1 };
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
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1, StoreLeafChunks = false };
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
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 2 };
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
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1, SummaryChatClient = customClient };
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
            TreeScope = RaptorTreeScope.PerDocument,
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
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 1 };
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 0);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Empty(ctx.EmbeddedChunks);
    }

    [Fact]
    public async Task HandleAsync_AtExactThreshold_AppliesRaptor()
    {
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 6, ReducedDimensionality = 2, MaxTreeDepth = 1 };
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
        // Used to force a depth-2 tree with chunkCount = 6 and no MaxClusters, relying on
        // GaussianMixtureModel.SelectK's #333 bug (it selected k = n for genuinely distinct
        // points, until the minimum-component-size rule fixed that) to turn each of 6 leaves into
        // its own level-1 "cluster", then each of those 6 summaries into its own level-2
        // "cluster" — a non-reducing tree that only terminated because MaxTreeDepth capped it. The non-reducing-level guard this task adds
        // (BuildLevelAsync rejects any level whose k would not shrink the count) now rejects
        // that first level outright, and that is by design: an infinite version of the same
        // shape is exactly #333's defect.
        //
        // A prior version of this test tried to route around that by leaning on
        // ReducedDimensionality's UMAP step instead of an explicit cluster count, on the
        // (disproven) theory that GaussianMixtureModel.SelectK only ever returned k = 1 or k = n
        // for this harness's random embeddings. It did not: HandleAsync_AtExactThreshold_
        // AppliesRaptor (6 chunks, ReducedDimensionality = 2, unmodified) only stays green
        // because SelectK returns some 2 <= k <= 5 there — k = 6 would trip the guard and k = 1
        // would trip the k <= 1 check ahead of it, and either would leave that test's
        // summaryChunks empty. What sweeping chunkCount actually failed to find was two
        // *consecutive* non-degenerate levels through auto-selected k, not "any" real level.
        //
        // Setting MaxClusters explicitly sidesteps SelectK (and #333) entirely, so this no
        // longer depends on what BIC happens to pick. With the Min(MaxClusters, count - 1) clamp
        // (also added this task, alongside the guard: a fixed MaxClusters otherwise re-creates
        // the same non-terminating shape once the tree shrinks down to that exact size), k is
        // guaranteed strictly less than the level's count at every level as long as count > 1, so
        // a real depth-2 tree forms deterministically: 6 leaves -> 3 level-1 summaries (k =
        // Min(3, 5) = 3) -> 2 level-2 summaries (k = Min(3, 2) = 2).
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var ctx = _helpers.CreateContext(chunkCount: 6);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MaxClusters = 3, MaxTreeDepth = 2 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);

        await behavior.HandleAsync(ctx, CancellationToken.None,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var summaries = ctx.EmbeddedChunks
            .Where(c => c.Chunk.Metadata.ContainsKey("raptor_level"))
            .ToList();

        // Guards against the tree silently degrading to depth 1 in future, which would make
        // the ChunkKey-uniqueness assertion below pass vacuously.
        Assert.True(summaries.Count > 1, "test needs a tree with more than one summary to be meaningful");
        Assert.Contains(summaries, c => !string.Equals(c.Chunk.Metadata["raptor_level"].ToString(), "1", StringComparison.Ordinal));

        var keys = ctx.EmbeddedChunks
            .Select(c => new ChunkKey(c.Chunk.DocumentId.Value, c.Chunk.ChunkIndex))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task TreeBuilding_Terminates_AtDefaultOptionsWithNoDepthCap()
    {
        // MaxTreeDepth deliberately left at its default null. Before the non-reducing-level guard
        // this hung forever (#333). The 30-second CancellationTokenSource only fails the test
        // rather than wedging the suite because BuildTreeAsync's level loop calls
        // ct.ThrowIfCancellationRequested() every iteration: a regression reintroducing
        // non-termination surfaces as OperationCanceledException here, not a hang.
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);
        var ctx = _helpers.CreateContext(chunkCount: 24);
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await behavior.HandleAsync(ctx, cts.Token, static (_, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

        Assert.False(cts.IsCancellationRequested, "tree building did not terminate at default options");
    }

    [Fact]
    public async Task TreeReachesDepthTwo_WithBicSelectedK_NoMaxClustersSet()
    {
        // Coverage hole flagged across two reviews: SummaryChunks_HaveUniqueChunkIndexes_
        // AcrossEveryTreeLevel above only reaches depth 2 via an explicit MaxClusters, because
        // SetupEmbedder used to return pure noise for summary embeddings — a level-2 clustering
        // always saw unstructured noise and correctly collapsed to k = 1. Since corpus-level RAPTOR
        // is now the shipped default (#331), that hole matters: this exercises BIC-selected k, at
        // every level, reaching depth 2 for real.
        //
        // SetupChatClientToEchoPrompt keeps each cluster's "topic T" markers visible in its summary
        // text at every level, and SetupEmbedder now reads them back: topics 0 and 1 are placed
        // close together, topic 2 far away. The well-separated i % 3 leaf split (see CreateContext)
        // reliably gives level 1 three summaries, one per topic; clustering those three finds the
        // same two-tier structure (k = 2: {topic 0, topic 1} merge, {topic 2} stays alone) rather
        // than three equidistant points, so level 2 is a real, non-degenerate reduction and not a
        // forced one.
        _helpers.SetupChatClientToEchoPrompt();
        _helpers.SetupEmbedder(dims: 8);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options);
        var ctx = _helpers.CreateContext(chunkCount: 24, embeddingDims: 8);

        await behavior.HandleAsync(ctx, CancellationToken.None,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var levels = ctx.EmbeddedChunks
            .Where(c => c.Chunk.Metadata.ContainsKey("raptor_level"))
            .Select(c => int.Parse(c.Chunk.Metadata["raptor_level"].ToString(), System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(levels);
        Assert.Contains(2, levels);
    }

    // ── Failure classification (#930, re-applying the wrap #504 reverted) ─────

    private static RaptorOptions OneLevel() => new()
    {
        TreeScope = RaptorTreeScope.PerDocument,
        MinChunksForRaptor = 2,
        ReducedDimensionality = 2,
        MaxTreeDepth = 1,
    };

    private static ValueTask<IngestionResult> Next(IngestionContext c, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count });

    /// <summary>Runs one RAPTOR level whose summary chat call throws <paramref name="thrown"/>.</summary>
    private Task RunWithFailingChatAsync(Exception thrown, CancellationToken? ct = null)
    {
        _helpers.ChatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw thrown);
        _helpers.SetupEmbedder(8);
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, OneLevel());

        return sut.HandleAsync(
            _helpers.CreateContext(chunkCount: 6, embeddingDims: 8), ct ?? TestContext.Current.CancellationToken, Next).AsTask();
    }

    /// <summary>
    /// Runs one RAPTOR level whose summary embedding throws <paramref name="thrown"/>. The leaf
    /// chunks arrive already embedded, so the summary is the only text this embedder sees.
    /// </summary>
    private Task RunWithFailingSummaryEmbedderAsync(Exception thrown, CancellationToken? ct = null) =>
        RunWithSummaryEmbedderAsync(_ => throw thrown, ct);

    private Task RunWithSummaryEmbedderAsync(
        Func<NSubstitute.Core.CallInfo, Task<GeneratedEmbeddings<Embedding<float>>>> answer,
        CancellationToken? ct = null)
    {
        _helpers.SetupChatClient("Summary of cluster");
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(answer);
        var sut = new RaptorIngestionBehavior(_helpers.ChatClient, embedder, OneLevel());

        return sut.HandleAsync(
            _helpers.CreateContext(chunkCount: 6, embeddingDims: 8), ct ?? TestContext.Current.CancellationToken, Next).AsTask();
    }

    [Fact]
    public async Task SummaryChatFailure_SurfacesAsModelCallException()
    {
        var provider = new HttpRequestException("HTTP 429 (Too Many Requests)");

        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithFailingChatAsync(provider));

        Assert.Same(provider, ex.InnerException);
        Assert.Contains("summarise", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryChat_BlownBudget_PassesThroughUnwrapped()
    {
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() => RunWithFailingChatAsync(refusal));

        Assert.Same(refusal, ex);
    }

    /// <summary>
    /// The regression #504 hit: under the benchmark harness the chat client is a cache opened
    /// refuse-on-miss, and its refusal was relabelled as a failed model call when no model was called.
    /// </summary>
    [Fact]
    public async Task SummaryChat_CacheRefusal_PassesThroughAsThatExactType()
    {
        var refusal = new CallRefusedException("No cached response; this cache refuses on a miss.");

        var ex = await Assert.ThrowsAsync<CallRefusedException>(() => RunWithFailingChatAsync(refusal));

        Assert.Same(refusal, ex);
    }

    [Fact]
    public async Task SummaryEmbeddingFailure_SurfacesAsModelCallException()
    {
        var provider = new HttpRequestException("HTTP 503 (Service Unavailable)");

        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithFailingSummaryEmbedderAsync(provider));

        Assert.Same(provider, ex.InnerException);
        Assert.Contains("embedding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryEmbedding_BlownBudget_PassesThroughUnwrapped()
    {
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() => RunWithFailingSummaryEmbedderAsync(refusal));

        Assert.Same(refusal, ex);
    }

    [Fact]
    public async Task SummaryEmbedding_CacheRefusal_PassesThroughAsThatExactType()
    {
        var refusal = new CallRefusedException("Never prefetched; this cache refuses on a miss.");

        var ex = await Assert.ThrowsAsync<CallRefusedException>(() => RunWithFailingSummaryEmbedderAsync(refusal));

        Assert.Same(refusal, ex);
    }

    [Fact]
    public async Task SummaryChat_ProviderTimeout_SurfacesAsModelCallException()
    {
        var timeout = new TaskCanceledException("HttpClient.Timeout");

        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithFailingChatAsync(timeout));

        Assert.Same(timeout, ex.InnerException);
    }

    [Fact]
    public async Task SummaryEmbedding_ProviderTimeout_SurfacesAsModelCallException()
    {
        var timeout = new TaskCanceledException("HttpClient.Timeout");

        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithFailingSummaryEmbedderAsync(timeout));

        Assert.Same(timeout, ex.InnerException);
    }

    [Fact]
    public async Task SummaryChat_CallerCancellation_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunWithFailingChatAsync(new OperationCanceledException(cts.Token), cts.Token));
    }

    [Fact]
    public async Task SummaryEmbedding_CallerCancellation_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunWithFailingSummaryEmbedderAsync(new OperationCanceledException(cts.Token), cts.Token));
    }

    [Fact]
    public async Task SummaryChat_ExistingModelCallException_IsNotWrappedTwice()
    {
        var inner = new ModelCallException("inner", new IOException("provider"));

        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithFailingChatAsync(inner));

        Assert.Same(inner, ex);
    }

    [Fact]
    public async Task SummaryEmbedding_ExistingModelCallException_IsNotWrappedTwice()
    {
        var inner = new ModelCallException("inner", new IOException("provider"));

        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithFailingSummaryEmbedderAsync(inner));

        Assert.Same(inner, ex);
    }

    /// <summary>
    /// An embedder that answers with no vector for the summary answered wrongly: a model failure,
    /// not an <see cref="ArgumentOutOfRangeException"/> from indexing an empty result.
    /// </summary>
    [Fact]
    public async Task SummaryEmbedding_WrongVectorCount_SurfacesAsModelCallException()
    {
        var ex = await Assert.ThrowsAsync<ModelCallException>(() => RunWithSummaryEmbedderAsync(
            _ => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>())));

        var count = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("0 embeddings for 1 input", count.Message, StringComparison.Ordinal);
    }
}
