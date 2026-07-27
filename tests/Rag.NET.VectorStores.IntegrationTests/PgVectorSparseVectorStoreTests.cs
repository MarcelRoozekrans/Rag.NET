using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PgVector;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.VectorStores.IntegrationTests;

/// <summary>
/// Docker-gated round-trip coverage of the pgvector <c>sparsevec</c> column: SPLADE vectors
/// written onto the rows the dense store already created, sparse search scored server-side by
/// <c>&lt;#&gt;</c>, and the guarantee that a dense re-store leaves the sparse vector alone.
/// </summary>
[Collection("PgVector")]
public class PgVectorSparseVectorStoreTests : IAsyncLifetime
{
    private readonly PgVectorFixture _fixture;
    private PgVectorSparseVectorStore _sut = null!;

    public PgVectorSparseVectorStoreTests(PgVectorFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _sut = new PgVectorSparseVectorStore(_fixture.ConnectionString, vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StoreAsync_AfterStoreSparseAsync_DoesNotClearTheSparseVector()
    {
        // THE point of keeping sparse_embedding out of the dense upsert's DO UPDATE SET list.
        // Pinecone has to carry the opposite as an ORDERING CONTRACT ("StoreAsync after
        // StoreSparseAsync silently drops the sparse vector"); here either order is safe, and
        // this test is what keeps it that way.
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";
        var chunk = MakeChunk(docId, 0, "original text");

        try
        {
            await _sut.StoreAsync([chunk], ct);
            await _sut.StoreSparseAsync([(chunk, Sparse([42], [2.0f]))], ct);

            // A dense re-store of the same chunk — exactly what ReindexStaleAsync does.
            await _sut.StoreAsync([MakeChunk(docId, 0, "re-stored text")], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([42], [3.0f]), new SearchOptions { TopK = 10 }, ct);

            Assert.Single(results);
            Assert.Equal("re-stored text", results[0].Chunk.Text); // dense columns did update
            Assert.Equal(6.0, results[0].Score, precision: 3);     // sparse vector survived
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StoreSparse_SameChunkTwice_Replaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";
        var chunk = MakeChunk(docId, 0, "text");

        try
        {
            await _sut.StoreAsync([chunk], ct);
            await _sut.StoreSparseAsync([(chunk, Sparse([42], [1.0f]))], ct);
            await _sut.StoreSparseAsync([(chunk, Sparse([42], [5.0f]))], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([42], [1.0f]), new SearchOptions { TopK = 10 }, ct);

            // One row, carrying the second write — not two rows, and not the first weight.
            Assert.Single(results);
            Assert.Equal(5.0, results[0].Score, precision: 3);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StoreSparse_ChunkNeverStoredDensely_WritesNothing()
    {
        // The UPDATE keys on (document_id, chunk_index): with no dense row there is nothing to
        // attach to. Ingestion always stores dense first, so this is the degenerate case, and it
        // must be a silent no-op rather than a half-populated row.
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";

        try
        {
            await _sut.StoreSparseAsync([(MakeChunk(docId, 0, "orphan"), Sparse([42], [1.0f]))], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([42], [1.0f]), new SearchOptions { TopK = 10 }, ct);

            Assert.Empty(results);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    private static EmbeddedChunk MakeChunk(
        string docId,
        int chunkIndex,
        string text,
        float[]? embedding = null,
        Dictionary<string, string>? metadata = null) => new()
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId(docId),
                ChunkIndex = chunkIndex,
                Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
            },
            Embedding = embedding ?? new float[] { 1.0f, 0.0f, 0.0f },
        };

    private static SparseVector Sparse(int[] indices, float[] values) =>
        new() { Indices = indices, Values = values };
}
