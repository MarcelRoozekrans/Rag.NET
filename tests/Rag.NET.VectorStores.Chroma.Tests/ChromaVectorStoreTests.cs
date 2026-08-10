using System.Net.Http.Json;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chroma.Tests;

[Collection("Chroma")]
public class ChromaVectorStoreTests
{
    private readonly ChromaContainerFixture _fixture;

    public ChromaVectorStoreTests(ChromaContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StoreAndSearch_RoundTrip()
    {
        using var store = CreateStore(UniqueCollectionName());
        await store.StoreAsync(
            [
                Chunk("doc-rt", 0, "cats are great pets", [1.0f, 0.0f, 0.0f],
                    new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "unit" }),
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
        Assert.Equal<MetadataValue>("unit", results[0].Chunk.Metadata["source"]);
        Assert.Equal("dogs are loyal friends", results[1].Chunk.Text);
        Assert.True(results[0].Score > results[1].Score, "nearest result must rank first");
    }

    [Fact]
    public async Task StoreAndSearch_TypedMetadata_KindsSurviveRoundTrip()
    {
        // A number reading back as the string "3" is the flattening bug the typed metadata
        // design removes (#91) — so the assertion is on Kind, not on textual form.
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        using var store = CreateStore(UniqueCollectionName());
        await store.StoreAsync(
            [
                Chunk("doc-typed", 0, "typed metadata chunk", [1.0f, 0.0f, 0.0f],
                    new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["page"] = 3,
                        ["rating"] = 4.5,
                        ["published"] = true,
                        ["reviewed_at"] = reviewedAt,
                        ["source"] = "unit",
                    }),
            ],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
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
        using var store = CreateStore(UniqueCollectionName());
        // The chunk nearest the query vector is on page 4 and TopK = 1, so only a
        // server-side numeric filter can return the farther page-3 chunk.
        await store.StoreAsync(
            [
                Chunk("doc-p4", 0, "page four chunk", [1.0f, 0.0f, 0.0f],
                    new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 4 }),
                Chunk("doc-p3", 0, "page three chunk", [0.8f, 0.6f, 0.0f],
                    new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 }),
            ],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
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
    public async Task Search_IdenticalVector_ScoreNearOne()
    {
        using var store = CreateStore(UniqueCollectionName());
        await store.StoreAsync(
            [Chunk("doc-score", 0, "identity chunk", [0.6f, 0.8f, 0.0f])],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 0.6f, 0.8f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        // Pins the conversion for the identical vector: distance 0 ⇒ Score = 1 - distance ≈ 1.
        // (This alone does not discriminate cosine from squared L2 — both give distance 0
        // here; the cosine space itself is pinned by Search_TopKAndMinScore_Honored's
        // orthogonal-score assertion.)
        var result = Assert.Single(results);
        Assert.InRange(result.Score, 0.99, 1.0001);
    }

    [Fact]
    public async Task Store_SameChunkTwice_Replaces()
    {
        using var store = CreateStore(UniqueCollectionName());
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

        // Same record id {documentId}:{chunkIndex} ⇒ upsert replaces: count stays 1.
        var result = Assert.Single(results);
        Assert.Equal("updated text", result.Chunk.Text);
    }

    [Fact]
    public async Task Search_MetadataFilter_Filters()
    {
        using var store = CreateStore(UniqueCollectionName());
        // The chunk NEAREST to the query vector is always excluded by the filters below,
        // and TopK = 1: only server-side filtering can return the farther matching chunk —
        // a client-side post-filter of the top-1 hit would come back empty.
        await store.StoreAsync(
            [
                Chunk("doc-f1", 0, "marketing core doc", [1.0f, 0.0f, 0.0f],
                    Meta(("department", "marketing"), ("team", "core"))),
                Chunk("doc-f2", 0, "engineering core doc", [0.8f, 0.6f, 0.0f],
                    Meta(("department", "engineering"), ("team", "core"))),
                Chunk("doc-f3", 0, "engineering web doc", [0.6f, 0.8f, 0.0f],
                    Meta(("department", "engineering"), ("team", "web"))),
            ],
            TestContext.Current.CancellationToken);

        var singleKey = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 1,
                MetadataFilter = Meta(("department", "engineering")),
            },
            TestContext.Current.CancellationToken);

        var singleKeyHit = Assert.Single(singleKey);
        Assert.Equal("engineering core doc", singleKeyHit.Chunk.Text);

        // $and-composition: the nearest chunk overall (marketing) and the nearest
        // engineering chunk (team=core) are both excluded — only doc-f3 satisfies both keys.
        var twoKeysAnd = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 1,
                MetadataFilter = Meta(("department", "engineering"), ("team", "web")),
            },
            TestContext.Current.CancellationToken);

        var twoKeysHit = Assert.Single(twoKeysAnd);
        Assert.Equal("engineering web doc", twoKeysHit.Chunk.Text);
    }

    [Fact]
    public async Task Search_TopKAndMinScore_Honored()
    {
        using var store = CreateStore(UniqueCollectionName());
        await store.StoreAsync(
            [
                Chunk("doc-k", 0, "identical", [1.0f, 0.0f, 0.0f]),   // distance 0.0 → score 1.0
                Chunk("doc-k", 1, "close", [0.8f, 0.6f, 0.0f]),        // distance 0.2 → score 0.8
                Chunk("doc-k", 2, "orthogonal", [0.0f, 1.0f, 0.0f]),   // distance 1.0 → score 0.0
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
            new SearchOptions { TopK = 10, MinScore = 0.9 },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(minScore);
        Assert.Equal("identical", result.Chunk.Text);

        // Pins the cosine space end-to-end: with the default MinScore (0.0) all three
        // chunks come back and the orthogonal chunk scores ~0.0 (1 - cosine distance 1.0).
        // Under Chroma's default squared-L2 space it would score -1.0 (1 - 2.0) and be
        // filtered out — this assertion is what actually proves hnsw:space=cosine took.
        var all = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
        Assert.Equal("orthogonal", all[2].Chunk.Text);
        Assert.InRange(all[2].Score, -0.01, 0.01);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesOnlyThatDocument()
    {
        using var store = CreateStore(UniqueCollectionName());
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
        using var store = CreateStore(UniqueCollectionName());
        ICollectionManageable manageable = store;
        var collectionName = UniqueCollectionName();

        Assert.False(await manageable.CollectionExistsAsync(collectionName, TestContext.Current.CancellationToken));

        await manageable.CreateCollectionAsync(collectionName, 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync(collectionName, TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync(collectionName, TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync(collectionName, TestContext.Current.CancellationToken));

        // Delete-of-missing is a no-op (the ICollectionManageable contract): Chroma
        // answers 404, which the store swallows instead of throwing.
        await manageable.DeleteCollectionAsync(collectionName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Store_ExistingNonCosineCollection_FailsFast()
    {
        var collectionName = UniqueCollectionName();
        // Pre-create the collection with the squared-L2 space behind the store's back,
        // as a user's own tooling might have (l2 is also Chroma's default space).
        using var http = new HttpClient();
        using var response = await http.PostAsJsonAsync(
            new Uri(_fixture.Endpoint, "/api/v2/tenants/default_tenant/databases/default_database/collections"),
            new
            {
                name = collectionName,
                metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["hnsw:space"] = "l2" },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // get_or_create returns the existing L2 collection unchanged — without the guard,
        // Score = 1 - distance would silently be on the wrong scale.
        using var store = CreateStore(collectionName);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.StoreAsync(
                [Chunk("doc-space", 0, "wrong space", [1.0f, 0.0f, 0.0f])],
                TestContext.Current.CancellationToken));

        Assert.Contains("'l2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cosine", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionRecreated_IdCacheRecovers()
    {
        var collectionName = UniqueCollectionName();
        using var store = CreateStore(collectionName);
        await store.StoreAsync(
            [Chunk("doc-cache", 0, "before recreate", [1.0f, 0.0f, 0.0f])],
            TestContext.Current.CancellationToken);

        // Delete and recreate the collection behind the store's back: the recreated
        // collection has a NEW UUID, so the store's cached id is stale.
        using var saboteur = CreateStore(UniqueCollectionName());
        await saboteur.DeleteCollectionAsync(collectionName, TestContext.Current.CancellationToken);
        await saboteur.CreateCollectionAsync(collectionName, 3, TestContext.Current.CancellationToken);

        // The next store operation gets a 404 for the stale UUID, re-resolves once, and
        // succeeds against the recreated collection.
        await store.StoreAsync(
            [Chunk("doc-cache", 0, "after recreate", [1.0f, 0.0f, 0.0f])],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("after recreate", result.Chunk.Text);
    }

    private ChromaVectorStore CreateStore(string collectionName) =>
        new(new ChromaOptions
        {
            Endpoint = _fixture.Endpoint,
            CollectionName = collectionName,
        });

    private static string UniqueCollectionName() => $"test-{Guid.CreateVersion7():N}";

    private static EmbeddedChunk Chunk(
        string documentId,
        int chunkIndex,
        string text,
        float[] embedding,
        Dictionary<string, MetadataValue>? metadata = null) => new()
    {
        Chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
        },
        Embedding = embedding,
    };

    private static Dictionary<string, MetadataValue> Meta(params (string Key, string Value)[] entries)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            metadata[key] = value;
        return metadata;
    }
}
