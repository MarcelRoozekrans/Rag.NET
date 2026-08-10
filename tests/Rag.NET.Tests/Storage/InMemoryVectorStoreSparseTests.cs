using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class InMemoryVectorStoreSparseTests
{
    private static EmbeddedChunk MakeChunk(
        string docId, int chunkIndex, IDictionary<string, MetadataValue>? metadata = null) => new()
        {
            Chunk = new TextChunk
            {
                Text = $"{docId}-{chunkIndex}",
                DocumentId = new DocumentId(docId),
                ChunkIndex = chunkIndex,
                Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            Embedding = new float[] { 1f, 0f },
        };

    private static SparseVector Sparse(int[] indices, float[] values) =>
        new() { Indices = indices, Values = values };

    [Fact]
    public async Task SearchSparse_DotProductRanking()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreSparseAsync(
        [
            (MakeChunk("doc-a", 0), Sparse([1, 5, 9], [1f, 2f, 3f])),
            (MakeChunk("doc-b", 0), Sparse([5], [4f])),
            (MakeChunk("doc-c", 0), Sparse([100], [1f])),
        ], ct);

        // query · a = 2*2 + 1*3 = 7 ; query · b = 2*4 = 8 ; query · c = 0 (disjoint, absent)
        var results = await store.SearchSparseAsync(
            Sparse([5, 9], [2f, 1f]), new SearchOptions { TopK = 10 }, ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-b", results[0].Chunk.DocumentId.Value);
        Assert.Equal(8.0, results[0].Score, precision: 5);
        Assert.Equal("doc-a", results[1].Chunk.DocumentId.Value);
        Assert.Equal(7.0, results[1].Score, precision: 5);
    }

    [Fact]
    public async Task SearchSparse_TopK_Respected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreSparseAsync(
        [
            (MakeChunk("doc-a", 0), Sparse([1], [1f])),
            (MakeChunk("doc-b", 0), Sparse([1], [2f])),
            (MakeChunk("doc-c", 0), Sparse([1], [3f])),
        ], ct);

        var results = await store.SearchSparseAsync(
            Sparse([1], [1f]), new SearchOptions { TopK = 2 }, ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-c", results[0].Chunk.DocumentId.Value);
        Assert.Equal("doc-b", results[1].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task SearchSparse_MinScore_Respected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreSparseAsync(
        [
            (MakeChunk("doc-low", 0), Sparse([1], [1f])),
            (MakeChunk("doc-high", 0), Sparse([1], [5f])),
        ], ct);

        var results = await store.SearchSparseAsync(
            Sparse([1], [1f]), new SearchOptions { TopK = 10, MinScore = 2.0 }, ct);

        Assert.Single(results);
        Assert.Equal("doc-high", results[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task SearchSparse_MetadataFilter_Respected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreSparseAsync(
        [
            (MakeChunk("doc-eng", 0, new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" }),
                Sparse([1], [1f])),
            (MakeChunk("doc-mkt", 0, new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" }),
                Sparse([1], [5f])),
        ], ct);

        var results = await store.SearchSparseAsync(
            Sparse([1], [1f]),
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            ct);

        Assert.Single(results);
        Assert.Equal("doc-eng", results[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task StoreSparse_Idempotent_ByDocIdChunkIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreSparseAsync([(MakeChunk("doc-a", 0), Sparse([1, 2], [1f, 1f]))], ct);
        // Re-store the same (DocumentId, ChunkIndex) with a different vector: replaces, no duplicate.
        await store.StoreSparseAsync([(MakeChunk("doc-a", 0), Sparse([1], [3f]))], ct);

        var byTerm1 = await store.SearchSparseAsync(Sparse([1], [1f]), new SearchOptions { TopK = 10 }, ct);
        Assert.Single(byTerm1);
        Assert.Equal(3.0, byTerm1[0].Score, precision: 5);

        // Term 2 postings from the first store must be gone.
        var byTerm2 = await store.SearchSparseAsync(Sparse([2], [1f]), new SearchOptions { TopK = 10 }, ct);
        Assert.Empty(byTerm2);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesSparsePostings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreSparseAsync(
        [
            (MakeChunk("doc-a", 0), Sparse([1], [1f])),
            (MakeChunk("doc-a", 1), Sparse([1], [2f])),
            (MakeChunk("doc-b", 0), Sparse([1], [3f])),
        ], ct);

        await store.DeleteByDocumentIdAsync("doc-a", ct);

        var results = await store.SearchSparseAsync(Sparse([1], [1f]), new SearchOptions { TopK = 10 }, ct);
        Assert.Single(results);
        Assert.Equal("doc-b", results[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task SearchSparse_EmptyQuery_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();
        await store.StoreSparseAsync([(MakeChunk("doc-a", 0), Sparse([1], [1f]))], ct);

        var results = await store.SearchSparseAsync(SparseVector.Empty, new SearchOptions { TopK = 10 }, ct);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ConcurrentStoreAndSearch_NoTornReads()
    {
        using var store = new InMemoryVectorStore();

        Parallel.For(0, 500, i =>
        {
            if (i % 2 == 0)
            {
                store.StoreSparseAsync(
                    [(MakeChunk($"doc-{i}", 0), Sparse([(i % 10) + 1], [1f]))],
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                var hits = store.SearchSparseAsync(
                    Sparse([(i % 10) + 1], [1f]),
                    new SearchOptions { TopK = 5 },
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.NotNull(hits);
            }
        });

        var final = await store.SearchSparseAsync(
            Sparse([1], [1f]), new SearchOptions { TopK = 500 }, TestContext.Current.CancellationToken);
        Assert.NotEmpty(final);
    }
}
