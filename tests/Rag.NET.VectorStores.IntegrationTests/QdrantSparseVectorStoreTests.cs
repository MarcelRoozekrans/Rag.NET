using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Qdrant;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.VectorStores.IntegrationTests;

/// <summary>
/// Docker-gated round-trip coverage of the Qdrant named sparse vector ("splade") support:
/// dense store first (as ingestion does), sparse vectors attached to the same points, sparse
/// search honoring TopK/filters, and document deletes removing both representations.
/// </summary>
[Collection("Qdrant")]
public class QdrantSparseVectorStoreTests : IAsyncLifetime
{
    private readonly QdrantFixture _fixture;
    private readonly string _collectionName = $"ragnet-sparse-{Guid.CreateVersion7():N}"[..24];
    private QdrantVectorStore _sut = null!;

    public QdrantSparseVectorStoreTests(QdrantFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _sut = new QdrantVectorStore(
            _fixture.Host, _fixture.GrpcPort, _collectionName, vectorDimensions: 3,
            enableSparseVectors: true);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var manageable = (ICollectionManageable)_sut;
        // CancellationToken.None: cleanup must run even when the test was cancelled.
        await manageable.DeleteCollectionAsync(_collectionName, CancellationToken.None);
        _sut.Dispose();
    }

    private static EmbeddedChunk MakeChunk(string docId, int chunkIndex, string text, float[] embedding) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
        Embedding = embedding,
    };

    private static SparseVector Sparse(int[] indices, float[] values) =>
        new() { Indices = indices, Values = values };

    [Fact]
    public async Task StoreSparse_AndSearchSparse_RanksByDotProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"qdrant-sparse-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            MakeChunk(docId, 0, "cats are great", [1.0f, 0.0f, 0.0f]),
            MakeChunk(docId, 1, "dogs are great", [0.0f, 1.0f, 0.0f]),
        };

        try
        {
            await _sut.StoreAsync(chunks, ct);
            await _sut.StoreSparseAsync(
            [
                (chunks[0], Sparse([10, 42], [1.0f, 2.0f])),
                (chunks[1], Sparse([42, 99], [0.5f, 1.0f])),
            ], ct);

            // query · chunk0 = 3 * 2.0 = 6 ; query · chunk1 = 3 * 0.5 = 1.5
            var results = await _sut.SearchSparseAsync(
                Sparse([42], [3.0f]), new SearchOptions { TopK = 10 }, ct);

            Assert.Equal(2, results.Count);
            Assert.Equal("cats are great", results[0].Chunk.Text);
            Assert.Equal(6.0, results[0].Score, precision: 3);
            Assert.Equal("dogs are great", results[1].Chunk.Text);
            Assert.Equal(1.5, results[1].Score, precision: 3);

            // Dense search on the same points still works (same point ids, both vectors).
            var dense = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f }, new SearchOptions { TopK = 1 }, ct);
            Assert.Single(dense);
            Assert.Equal("cats are great", dense[0].Chunk.Text);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StoreSparse_TopKRespected()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"qdrant-sparse-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            MakeChunk(docId, 0, "a", [1.0f, 0.0f, 0.0f]),
            MakeChunk(docId, 1, "b", [0.0f, 1.0f, 0.0f]),
            MakeChunk(docId, 2, "c", [0.0f, 0.0f, 1.0f]),
        };

        try
        {
            await _sut.StoreAsync(chunks, ct);
            await _sut.StoreSparseAsync(
            [
                (chunks[0], Sparse([7], [1.0f])),
                (chunks[1], Sparse([7], [2.0f])),
                (chunks[2], Sparse([7], [3.0f])),
            ], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([7], [1.0f]), new SearchOptions { TopK = 2 }, ct);

            Assert.Equal(2, results.Count);
            Assert.Equal("c", results[0].Chunk.Text);
            Assert.Equal("b", results[1].Chunk.Text);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesSparseSearchability()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"qdrant-sparse-{Guid.CreateVersion7():N}";

        var chunk = MakeChunk(docId, 0, "text1", [1.0f, 0.0f, 0.0f]);

        try
        {
            await _sut.StoreAsync([chunk], ct);
            await _sut.StoreSparseAsync([(chunk, Sparse([5], [1.0f]))], ct);
            await _sut.DeleteByDocumentIdAsync(docId, ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([5], [1.0f]), new SearchOptions { TopK = 10 }, ct);

            Assert.Empty(results);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Store_SparseMode_IdempotentByDocIdChunkIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"qdrant-sparse-{Guid.CreateVersion7():N}";

        var chunk = MakeChunk(docId, 0, "original", [1.0f, 0.0f, 0.0f]);
        var updated = MakeChunk(docId, 0, "updated", [1.0f, 0.0f, 0.0f]);

        try
        {
            await _sut.StoreAsync([chunk], ct);
            await _sut.StoreAsync([updated], ct); // deterministic id → replaces, no duplicate

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f }, new SearchOptions { TopK = 10 }, ct);

            Assert.Single(results);
            Assert.Equal("updated", results[0].Chunk.Text);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }
}
