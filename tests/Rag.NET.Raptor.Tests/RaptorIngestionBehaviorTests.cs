using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorIngestionBehaviorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    [Fact]
    public async Task HandleAsync_WhenDisabled_CallsNextWithoutModification()
    {
        var options = new RaptorOptions { Enabled = false };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 10);
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
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 5);
        var originalCount = ctx.EmbeddedChunks.Count;

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(originalCount, ctx.EmbeddedChunks.Count);
    }

    [Fact]
    public async Task HandleAsync_AddsSummaryChunksWithRaptorMetadata()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary of cluster");
        SetupEmbedder(8);

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
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary");
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.All(ctx.EmbeddedChunks, ec => Assert.True(ec.Chunk.Metadata.ContainsKey("raptor_level")));
    }

    [Fact]
    public async Task HandleAsync_RespectsMaxTreeDepth()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 2 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 20, embeddingDims: 8);

        SetupChatClient("Summary");
        SetupEmbedder(8);

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
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        customClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Custom summary")]));
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        await customClient.Received().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _chatClient.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
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
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary");
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
        await _embedder.DidNotReceive().GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithZeroChunks_SkipsRaptor()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 1 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 0);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Empty(ctx.EmbeddedChunks);
    }

    [Fact]
    public async Task HandleAsync_AtExactThreshold_AppliesRaptor()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 6, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary");
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var summaryChunks = ctx.EmbeddedChunks.Where(ec => ec.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.NotEmpty(summaryChunks);
    }

    private static IngestionContext CreateContext(int chunkCount, int embeddingDims = 8)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), FileName = "test.txt", ContentType = "text/plain" },
            GetNextBm25DocId = () => 0,
        };

        var rng = new Random(42);
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = new TextChunk
            {
                Text = $"Chunk {i} content about topic {i % 3}",
                DocumentId = new DocumentId("test-doc"),
                ChunkIndex = i,
            };
            var embedding = GenerateEmbedding(rng, embeddingDims);
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new ReadOnlyMemory<float>(embedding) });
        }

        return ctx;
    }

    private void SetupChatClient(string response)
    {
        _chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

#pragma warning disable HLQ013 // Use foreach — need index-based assignment
    private static float[] GenerateEmbedding(Random rng, int dims)
    {
        var embedding = new float[dims];
        for (var j = 0; j < embedding.Length; j++)
            embedding[j] = (float)rng.NextDouble();
        return embedding;
    }
#pragma warning restore HLQ013

    private void SetupEmbedder(int dims)
    {
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                var rng = new Random(123);
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(
                        Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray())).ToList()));
            });
    }
}
