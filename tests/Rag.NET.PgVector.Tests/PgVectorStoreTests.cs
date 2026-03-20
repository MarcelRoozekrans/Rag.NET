using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rag.NET.PgVector.Tests;

public class PgVectorStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .Build();

    private PgVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _sut = new PgVectorStore(_postgres.GetConnectionString(), vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _postgres.DisposeAsync();
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
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
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
                MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("engineering doc", results[0].Chunk.Text);
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
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = _sut;

        await manageable.CreateCollectionAsync("temp_collection", 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync("temp_collection", TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync("temp_collection", TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync("temp_collection", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foo; DROP TABLE rag_chunks; --")]
    [InlineData("FOO")]
    [InlineData("1starts_with_digit")]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    public async Task CreateCollectionAsync_InvalidName_ThrowsArgumentException(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateCollectionAsync(name, 3, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foo; DROP TABLE rag_chunks; --")]
    [InlineData("1bad")]
    public async Task DeleteCollectionAsync_InvalidName_ThrowsArgumentException(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.DeleteCollectionAsync(name, TestContext.Current.CancellationToken));
    }
}
