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
    public async Task StoreSparse_AndSearchSparse_RanksByDotProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";

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

            // Dense search over the same rows still works — one table, two representations.
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
    public async Task SearchSparse_DisjointVector_ReturnsEmpty()
    {
        // A chunk sharing no term must be ABSENT, not present with score 0 — InMemoryVectorStore
        // only ever scores chunks sharing at least one term. The `score > 0` predicate is what
        // enforces it; without it MinScore = 0 would return every row in the table.
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";
        var chunk = MakeChunk(docId, 0, "shares nothing");

        try
        {
            await _sut.StoreAsync([chunk], ct);
            await _sut.StoreSparseAsync([(chunk, Sparse([11], [1.0f]))], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([12], [1.0f]), new SearchOptions { TopK = 10, MinScore = 0.0 }, ct);

            Assert.Empty(results);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SearchSparse_TopKRespected()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            MakeChunk(docId, 0, "a"),
            MakeChunk(docId, 1, "b"),
            MakeChunk(docId, 2, "c"),
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
    public async Task SearchSparse_MinScore_AppliesOnDotProductScale()
    {
        // 2.0 is meaningless as a cosine threshold and would reject everything on the dense
        // path; on the sparse path it is a raw unbounded dot product. Same option name, two
        // scales — which is exactly why this is pinned.
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";

        var chunks = new List<EmbeddedChunk>
        {
            MakeChunk(docId, 0, "strong"),
            MakeChunk(docId, 1, "weak"),
        };

        try
        {
            await _sut.StoreAsync(chunks, ct);
            await _sut.StoreSparseAsync(
            [
                (chunks[0], Sparse([7], [4.0f])), // score 8.0
                (chunks[1], Sparse([7], [0.5f])), // score 1.0
            ], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([7], [2.0f]), new SearchOptions { TopK = 10, MinScore = 2.0 }, ct);

            Assert.Single(results);
            Assert.Equal("strong", results[0].Chunk.Text);
            Assert.Equal(8.0, results[0].Score, precision: 3);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SearchSparse_MetadataFilter_Filters()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId1 = $"pgv-{Guid.CreateVersion7():N}";
        var docId2 = $"pgv-{Guid.CreateVersion7():N}";

        var engineering = MakeChunk(
            docId1, 0, "engineering doc",
            metadata: new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" });
        var marketing = MakeChunk(
            docId2, 0, "marketing doc",
            metadata: new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" });

        try
        {
            await _sut.StoreAsync([engineering, marketing], ct);
            await _sut.StoreSparseAsync(
            [
                (engineering, Sparse([7], [1.0f])),
                (marketing, Sparse([7], [9.0f])), // higher scoring, so only the filter can exclude it
            ], ct);

            var results = await _sut.SearchSparseAsync(
                Sparse([7], [1.0f]),
                new SearchOptions
                {
                    TopK = 10,
                    MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["department"] = "engineering",
                    },
                },
                ct);

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
    public async Task DeleteByDocumentId_RemovesSparseSearchability()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"pgv-{Guid.CreateVersion7():N}";
        var chunk = MakeChunk(docId, 0, "text1");

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
        Dictionary<string, MetadataValue>? metadata = null) => new()
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId(docId),
                ChunkIndex = chunkIndex,
                Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            Embedding = embedding ?? new float[] { 1.0f, 0.0f, 0.0f },
        };

    private static SparseVector Sparse(int[] indices, float[] values) =>
        new() { Indices = indices, Values = values };
}
