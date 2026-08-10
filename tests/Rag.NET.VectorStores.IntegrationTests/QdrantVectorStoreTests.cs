using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Qdrant;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.VectorStores.IntegrationTests;

[Collection("Qdrant")]
public class QdrantVectorStoreTests : IAsyncLifetime
{
    private readonly QdrantFixture _fixture;
    private readonly string _collectionName = $"ragnet-test-{Guid.CreateVersion7():N}"[..24];
    private QdrantVectorStore _sut = null!;

    public QdrantVectorStoreTests(QdrantFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _sut = new QdrantVectorStore(_fixture.Host, _fixture.GrpcPort, _collectionName, vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var manageable = (ICollectionManageable)_sut;
        // CancellationToken.None: cleanup must run even when the test was cancelled.
        await manageable.DeleteCollectionAsync(_collectionName, CancellationToken.None);
        _sut.Dispose();
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var docId = $"qdrant-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = new DocumentId(docId), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = new DocumentId(docId), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 1 },
                TestContext.Current.CancellationToken);

            Assert.Single(results);
            Assert.Equal("cats are great", results[0].Chunk.Text);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var docId = $"qdrant-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = new DocumentId(docId), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
            await _sut.DeleteByDocumentIdAsync(docId, TestContext.Current.CancellationToken);

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);

            Assert.Empty(results);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var docId1 = $"qdrant-{Guid.CreateVersion7():N}";
        var docId2 = $"qdrant-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc",
                    DocumentId = new DocumentId(docId1),
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc",
                    DocumentId = new DocumentId(docId2),
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" },
                },
                Embedding = new float[] { 0.9f, 0.1f, 0.0f },
            },
        };

        try
        {
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
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId1, CancellationToken.None);
            await _sut.DeleteByDocumentIdAsync(docId2, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = (ICollectionManageable)_sut;
        var tempCollection = $"temp_{Guid.CreateVersion7():N}"[..20];

        await manageable.CreateCollectionAsync(tempCollection, 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync(tempCollection, TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync(tempCollection, TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync(tempCollection, TestContext.Current.CancellationToken));
    }
}
