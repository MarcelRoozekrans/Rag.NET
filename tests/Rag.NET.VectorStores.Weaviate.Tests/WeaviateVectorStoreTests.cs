using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Weaviate.Tests;

[Collection("Weaviate")]
public class WeaviateVectorStoreTests
{
    private readonly WeaviateContainerFixture _fixture;

    public WeaviateVectorStoreTests(WeaviateContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StoreAndSearch_RoundTrip()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [
                Chunk("doc-rt", 0, "cats are great pets", [1.0f, 0.0f, 0.0f],
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "unit" }),
                Chunk("doc-rt", 1, "dogs are loyal friends", [0.0f, 1.0f, 0.0f]),
            ],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("cats are great pets", results[0].Chunk.Text);
        Assert.Equal("doc-rt", (string)results[0].Chunk.DocumentId);
        Assert.Equal(0, results[0].Chunk.ChunkIndex);
        Assert.Equal("unit", results[0].Chunk.Metadata["source"]);
        Assert.Equal("dogs are loyal friends", results[1].Chunk.Text);
        Assert.True(results[0].Score > results[1].Score, "nearest result must rank first");
    }

    [Fact]
    public async Task Search_IdenticalVector_ScoreNearOne()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [Chunk("doc-score", 0, "identity chunk", [0.6f, 0.8f, 0.0f])],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 0.6f, 0.8f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        // Pins the dense mapping: cosine distance 0 for the identical vector ⇒
        // Score = 1 - distance / 2 ≈ 1.
        var result = Assert.Single(results);
        Assert.InRange(result.Score, 0.99, 1.0001);
    }

    [Fact]
    public async Task Store_SameChunkTwice_Replaces()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [Chunk("doc-replace", 0, "original text", [1.0f, 0.0f, 0.0f])],
            TestContext.Current.CancellationToken);
        await store.StoreAsync(
            [Chunk("doc-replace", 0, "updated text", [1.0f, 0.0f, 0.0f])],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("updated text", result.Chunk.Text);
    }

    [Fact]
    public async Task Search_MetadataFilter_FiltersServerSide()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [
                Chunk("doc-f1", 0, "engineering core doc", [1.0f, 0.0f, 0.0f],
                    Meta(("department", "engineering"), ("team", "core"))),
                Chunk("doc-f2", 0, "marketing core doc", [0.9f, 0.1f, 0.0f],
                    Meta(("department", "marketing"), ("team", "core"))),
                Chunk("doc-f3", 0, "engineering web doc", [0.8f, 0.2f, 0.0f],
                    Meta(("department", "engineering"), ("team", "web"))),
            ],
            TestContext.Current.CancellationToken);

        var singleKey = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = Meta(("department", "engineering")),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, singleKey.Count);
        Assert.All(singleKey, r => Assert.Equal("engineering", r.Chunk.Metadata["department"]));

        var twoKeysAnd = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = Meta(("department", "engineering"), ("team", "core")),
            },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(twoKeysAnd);
        Assert.Equal("engineering core doc", result.Chunk.Text);
    }

    [Fact]
    public async Task Search_TopKAndMinScore_Honored()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [
                Chunk("doc-k", 0, "identical", [1.0f, 0.0f, 0.0f]),   // cos 1.0 → score 1.0
                Chunk("doc-k", 1, "close", [0.8f, 0.6f, 0.0f]),        // cos 0.8 → score 0.9
                Chunk("doc-k", 2, "orthogonal", [0.0f, 1.0f, 0.0f]),   // cos 0.0 → score 0.5
            ],
            TestContext.Current.CancellationToken);

        var topK = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, topK.Count);
        Assert.Equal("identical", topK[0].Chunk.Text);
        Assert.Equal("close", topK[1].Chunk.Text);

        var minScore = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10, MinScore = 0.95 },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(minScore);
        Assert.Equal("identical", result.Chunk.Text);
    }

    [Fact]
    public async Task HybridSearch_FindsKeywordOnlyMatch()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [
                Chunk("doc-h1", 0, "alpha bravo charlie", [1.0f, 0.0f, 0.0f]),
                Chunk("doc-h2", 0, "zebra quantum xylophone", [0.0f, 1.0f, 0.0f]),
            ],
            TestContext.Current.CancellationToken);

        // The query vector is orthogonal to doc-h2's vector — only BM25 can surface it.
        var results = await store.HybridSearchAsync(
            "zebra quantum xylophone",
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 5 },
            TestContext.Current.CancellationToken);

        var keywordHit = Assert.Single(
            results,
            r => string.Equals(r.Chunk.Text, "zebra quantum xylophone", StringComparison.Ordinal));
        Assert.InRange(keywordHit.Score, 0.0, 1.0);
        Assert.True(keywordHit.Score > 0.0, "the fused score of the BM25 match must be positive");
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksOfDoc()
    {
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [
                Chunk("doc-del", 0, "delete me 0", [1.0f, 0.0f, 0.0f]),
                Chunk("doc-del", 1, "delete me 1", [0.0f, 1.0f, 0.0f]),
                Chunk("doc-keep", 0, "keep me", [0.0f, 0.0f, 1.0f]),
            ],
            TestContext.Current.CancellationToken);

        await store.DeleteByDocumentIdAsync("doc-del", TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("doc-keep", (string)result.Chunk.DocumentId);
    }

    [Fact]
    public async Task Collection_CreateExistsDelete_Lifecycle()
    {
        using var store = CreateStore(UniqueClassName());
        ICollectionManageable manageable = store;
        var className = UniqueClassName();

        Assert.False(await manageable.CollectionExistsAsync(className, TestContext.Current.CancellationToken));

        await manageable.CreateCollectionAsync(className, 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync(className, TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync(className, TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync(className, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tenant_Isolation()
    {
        var className = UniqueClassName();
        using var storeA = CreateStore(className, tenant: "tenant_a");
        using var storeB = CreateStore(className, tenant: "tenant_b");

        await storeA.StoreAsync(
            [Chunk("doc-t", 0, "tenant a secret", [1.0f, 0.0f, 0.0f])],
            TestContext.Current.CancellationToken);
        await storeB.InitializeAsync(TestContext.Current.CancellationToken);

        var tenantAResults = await storeA.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);
        var tenantBResults = await storeB.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(tenantAResults);
        Assert.Equal("tenant a secret", result.Chunk.Text);
        Assert.Empty(tenantBResults);
    }

    [Fact]
    public async Task GraphQlError_Throws()
    {
        var className = UniqueClassName();
        using var store = CreateStore(className);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.DeleteCollectionAsync(className, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 1 },
                TestContext.Current.CancellationToken));

        // The exception must carry Weaviate's own GraphQL error message, which names the class.
        Assert.Contains(className, exception.Message, StringComparison.Ordinal);
    }

    private WeaviateVectorStore CreateStore(string className, string? tenant = null) =>
        new(new WeaviateOptions
        {
            Endpoint = _fixture.Endpoint,
            ClassName = className,
            VectorDimensions = 3,
            Tenant = tenant,
        });

    private static string UniqueClassName() => $"Test{Guid.CreateVersion7():N}";

    private static EmbeddedChunk Chunk(
        string documentId,
        int chunkIndex,
        string text,
        float[] embedding,
        Dictionary<string, string>? metadata = null) => new()
    {
        Chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
        },
        Embedding = embedding,
    };

    private static Dictionary<string, string> Meta(params (string Key, string Value)[] entries)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            metadata[key] = value;
        return metadata;
    }
}
