using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class GraphEntityExtractionBehaviorTests : IAsyncDisposable
{
    private const string ValidJson = """
        {"entities":[{"name":"Microsoft","type":"Organization","description":"Tech company"}],"relationships":[{"source":"Microsoft","target":"GraphRAG","description":"developed","weight":1.0}]}
        """;

    private const string GleanedJson = """
        {"entities":[{"name":"OpenAI","type":"Organization","description":"AI research lab"}],"relationships":[]}
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    [Fact]
    public async Task HandleAsync_WhenDisabled_SkipsExtraction()
    {
        var options = new GraphRagOptions { Enabled = false };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        Assert.True(nextCalled);
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Relationships);
    }

    [Fact]
    public async Task HandleAsync_ExtractsEntitiesAndStoresInGraphStore()
    {
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        SetupChatClient(ValidJson);
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Single(snapshot.Entities);
        Assert.Equal("Microsoft", snapshot.Entities[0].Name);
        Assert.Equal("Organization", snapshot.Entities[0].Type);
        Assert.Equal("Tech company", snapshot.Entities[0].Description);
        Assert.Equal("test-doc", snapshot.Entities[0].SourceDocumentId);
    }

    [Fact]
    public async Task HandleAsync_ExtractsRelationshipsAndStoresInGraphStore()
    {
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        SetupChatClient(ValidJson);
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Single(snapshot.Relationships);
        Assert.Equal("Microsoft", snapshot.Relationships[0].SourceEntity);
        Assert.Equal("GraphRAG", snapshot.Relationships[0].TargetEntity);
        Assert.Equal("developed", snapshot.Relationships[0].Description);
        Assert.Equal("test-doc", snapshot.Relationships[0].SourceDocumentId);
    }

    [Fact]
    public async Task HandleAsync_PerformsGleaningPass()
    {
        var options = new GraphRagOptions { GleaningPasses = 1 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        // First call: initial extraction. Second call: gleaning pass.
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJson)]),
                new ChatResponse([new ChatMessage(ChatRole.Assistant, GleanedJson)]));
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        // Verify LLM was called twice (once for extraction, once for gleaning)
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Entities.Count); // Microsoft + OpenAI
    }

    [Fact]
    public async Task HandleAsync_GleaningPassZero_NoFollowUp()
    {
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        SetupChatClient(ValidJson);
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        // Only one call per chunk — no gleaning
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UsesCustomExtractionClient()
    {
        var customClient = Substitute.For<IChatClient>();
        var options = new GraphRagOptions { GleaningPasses = 0, ExtractionChatClient = customClient };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        customClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJson)]));
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await customClient.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AppendsEntityChunksToContext()
    {
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        SetupChatClient(ValidJson);
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var entityChunks = ctx.EmbeddedChunks.Where(ec => ec.Chunk.Metadata.TryGetValue("graph_type", out var t) && t == "entity").ToList();
        Assert.Single(entityChunks);
        Assert.Equal<MetadataValue>("Microsoft", entityChunks[0].Chunk.Metadata["graph_entity_name"]);
        Assert.Equal<MetadataValue>("Organization", entityChunks[0].Chunk.Metadata["graph_entity_type"]);

        var relChunks = ctx.EmbeddedChunks.Where(ec => ec.Chunk.Metadata.TryGetValue("graph_type", out var t) && t == "relationship").ToList();
        Assert.Single(relChunks);
        Assert.Equal<MetadataValue>("Microsoft", relChunks[0].Chunk.Metadata["graph_source_entity"]);
        Assert.Equal<MetadataValue>("GraphRAG", relChunks[0].Chunk.Metadata["graph_target_entity"]);
    }

    [Fact]
    public async Task HandleAsync_MalformedJsonFromLLM_ContinuesGracefully()
    {
        // Setup LLM to return non-JSON text
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        SetupChatClient("This is not valid JSON at all!");
        SetupEmbedder(4);

        var nextCalled = false;
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        // The behavior should log a warning and continue without crashing
        Assert.True(nextCalled);
        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Entities);
    }

    [Fact]
    public async Task HandleAsync_EmptyChunks_SkipsWithoutError()
    {
        // Setup context with zero chunks
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("empty-doc"), FileName = "empty.txt" },
            GetNextBm25DocId = () => 0,
        };
        // No chunks added to ctx

        var nextCalled = false;
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        // Verify: next() is called, no LLM calls made
        Assert.True(nextCalled);
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IngestionContext CreateContext()
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };

        ctx.Chunks.Add(new TextChunk
        {
            Text = "Microsoft developed GraphRAG for knowledge graph construction.",
            DocumentId = new DocumentId("test-doc"),
            ChunkIndex = 0,
        });

        return ctx;
    }

    private void SetupChatClient(string response)
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

    private void SetupEmbedder(int dims)
    {
        _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
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
