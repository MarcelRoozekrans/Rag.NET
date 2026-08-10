using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Qdrant.Tests;

public class QdrantVectorStoreTests : IAsyncLifetime
{
    private readonly IContainer _qdrant = new ContainerBuilder("qdrant/qdrant:latest")
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Actix runtime found"))
        .Build();

    private QdrantVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _qdrant.StartAsync(TestContext.Current.CancellationToken);
        var port = _qdrant.GetMappedPublicPort(6334);
        _sut = new QdrantVectorStore("localhost", port, "test-collection", vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _qdrant.DisposeAsync();
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("cats are great", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = new DocumentId("doc-to-delete"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_RespectsMinScore()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "close match", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "far match", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 0.0f, 1.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10, MinScore = 0.9 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("close match", results[0].Chunk.Text);
    }

    [Fact]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" },
                },
                Embedding = new float[] { 0.9f, 0.1f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("engineering doc", results[0].Chunk.Text);
    }

    [Fact]
    public async Task StoreAndSearch_TypedMetadata_KindsSurviveRoundTrip()
    {
        // A number reading back as the string "3" is the flattening bug the typed metadata
        // design removes (#91) — so the assertion is on Kind, not on textual form.
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        await _sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "typed metadata chunk", DocumentId = new DocumentId("doc-typed"), ChunkIndex = 0,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                        {
                            ["page"] = 3,
                            ["rating"] = 4.5,
                            ["published"] = true,
                            ["reviewed_at"] = reviewedAt,
                            ["source"] = "unit",
                        },
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        var metadata = Assert.Single(results).Chunk.Metadata;
        Assert.Equal(MetadataValueKind.Number, metadata["page"].Kind);
        Assert.Equal(3d, metadata["page"].NumberValue);
        Assert.Equal(4.5, metadata["rating"].NumberValue);
        Assert.Equal(MetadataValueKind.Boolean, metadata["published"].Kind);
        Assert.True(metadata["published"].BooleanValue);
        Assert.Equal(MetadataValueKind.DateTimeOffset, metadata["reviewed_at"].Kind);
        Assert.Equal(reviewedAt, metadata["reviewed_at"].DateTimeOffsetValue);
        Assert.Equal(MetadataValueKind.String, metadata["source"].Kind);
    }

    [Fact]
    public async Task Search_NumericMetadataFilter_Filters()
    {
        // The chunk nearest the query vector is on page 4, so only a server-side numeric
        // condition (the closed gte = lte range the store builds for numbers) can exclude it
        // while returning the farther page-3 chunk.
        await _sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "page four chunk", DocumentId = new DocumentId("doc-p4"), ChunkIndex = 0,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 4 },
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "page three chunk", DocumentId = new DocumentId("doc-p3"), ChunkIndex = 0,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 },
                    },
                    Embedding = new float[] { 0.8f, 0.6f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 1,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 },
            },
            TestContext.Current.CancellationToken);

        var hit = Assert.Single(results);
        Assert.Equal("page three chunk", hit.Chunk.Text);
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = _sut;

        await manageable.CreateCollectionAsync("temp-collection", 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync("temp-collection", TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync("temp-collection", TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync("temp-collection", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteCollection_Missing_IsNoOp()
    {
        // The ICollectionManageable contract makes delete-of-missing a no-op. Qdrant answers
        // result:false for an absent collection, which QdrantClient surfaces as an exception —
        // the store has to absorb exactly that case.
        ICollectionManageable manageable = _sut;

        await manageable.DeleteCollectionAsync(
            $"never-created-{Guid.CreateVersion7():N}",
            TestContext.Current.CancellationToken);
    }
}
