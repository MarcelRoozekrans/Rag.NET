using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class InMemoryVectorStoreTests
{
    private static EmbeddedChunk MakeChunk(
        string docId, int chunkIndex, float[] embedding, IDictionary<string, MetadataValue>? metadata = null) => new()
        {
            Chunk = new TextChunk
            {
                Text = $"{docId}-{chunkIndex}",
                DocumentId = new DocumentId(docId),
                ChunkIndex = chunkIndex,
                Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            Embedding = embedding,
        };

    [Fact]
    public async Task StoreAndSearch_RanksByCosineSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreAsync(
        [
            MakeChunk("doc-x", 0, [1f, 0f]),
            MakeChunk("doc-y", 0, [0f, 1f]),
            MakeChunk("doc-diag", 0, [1f, 1f]),
        ], ct);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 2 }, ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-x", results[0].Chunk.DocumentId.Value);
        Assert.Equal(1.0, results[0].Score, precision: 5);
        Assert.Equal("doc-diag", results[1].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task Search_MinScoreAndMetadataFilter_Respected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreAsync(
        [
            MakeChunk("doc-eng", 0, [1f, 0f],
                new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" }),
            MakeChunk("doc-mkt", 0, [1f, 0f],
                new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" }),
            MakeChunk("doc-far", 0, [0f, 1f],
                new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" }),
        ], ct);

        var results = await store.SearchAsync(
            new float[] { 1f, 0f },
            new SearchOptions
            {
                TopK = 10,
                MinScore = 0.5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            ct);

        Assert.Single(results);
        Assert.Equal("doc-eng", results[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task Store_Idempotent_ByDocIdChunkIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f])], ct);
        await store.StoreAsync([MakeChunk("doc-a", 0, [0f, 1f])], ct);

        var results = await store.SearchAsync(new float[] { 0f, 1f }, new SearchOptions { TopK = 10 }, ct);

        Assert.Single(results);
        Assert.Equal(1.0, results[0].Score, precision: 5);
    }

    [Fact]
    public async Task Search_DimensionMismatch_ScoresZero()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreAsync(
        [
            MakeChunk("doc-2d", 0, [1f, 0f]),
            MakeChunk("doc-3d", 0, [1f, 0f, 0f]), // dimension differs from the query
        ], ct);

        // EmbeddingMath.CosineSimilarity pins mismatched dimensions to 0: included with
        // score 0 under the default MinScore of 0.0 ...
        var all = await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 10 }, ct);
        Assert.Equal(2, all.Count);
        Assert.Equal("doc-2d", all[0].Chunk.DocumentId.Value);
        var mismatched = all.Single(r => string.Equals(r.Chunk.DocumentId.Value, "doc-3d", StringComparison.Ordinal));
        Assert.Equal(0.0, mismatched.Score);

        // ... and excluded by any positive MinScore.
        var filtered = await store.SearchAsync(
            new float[] { 1f, 0f }, new SearchOptions { TopK = 10, MinScore = 0.1 }, ct);
        Assert.Single(filtered);
        Assert.Equal("doc-2d", filtered[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        await store.StoreAsync(
        [
            MakeChunk("doc-a", 0, [1f, 0f]),
            MakeChunk("doc-a", 1, [1f, 0f]),
            MakeChunk("doc-b", 0, [1f, 0f]),
        ], ct);

        await store.DeleteByDocumentIdAsync("doc-a", ct);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 10 }, ct);
        Assert.Single(results);
        Assert.Equal("doc-b", results[0].Chunk.DocumentId.Value);
    }
}
