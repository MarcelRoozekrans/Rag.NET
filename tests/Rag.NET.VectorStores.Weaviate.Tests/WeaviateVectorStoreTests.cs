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
        using var store = CreateStore(UniqueClassName());
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
        using var store = CreateStore(UniqueClassName());
        // The chunk nearest the query vector is on page 4 and TopK = 1, so only a
        // server-side numeric filter (valueNumber where operand) can return the farther
        // page-3 chunk.
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
    public async Task HybridSearch_FusesKeywordAndVectorArms()
    {
        // Three chunks arranged so only genuine two-arm fusion returns the right pair: the
        // query vector is nearest "alpha", the BM25 text query matches only "zebra", and
        // TopK = 2. A dense-only search returns alpha + one orthogonal filler; a keyword-only
        // search returns just zebra. Scores pin the documented contract: Weaviate's
        // relative-score-fusion value in [0, 1].
        using var store = CreateStore(UniqueClassName());
        await store.StoreAsync(
            [
                Chunk("doc-hybrid", 0, "alpha document", [1.0f, 0.0f, 0.0f]),
                Chunk("doc-hybrid", 1, "middle document", [0.0f, 1.0f, 0.0f]),
                Chunk("doc-hybrid", 2, "zebra document", [0.0f, 0.0f, 1.0f]),
            ],
            TestContext.Current.CancellationToken);

        var results = await store.HybridSearchAsync(
            "zebra",
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => string.Equals(r.Chunk.Text, "alpha document", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Chunk.Text, "zebra document", StringComparison.Ordinal));
        Assert.All(results, r => Assert.InRange(r.Score, 0.0, 1.0));
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

        // And-composition: the nearest chunk overall (marketing) and the nearest engineering
        // chunk (team=core) are both excluded — only doc-f3 satisfies both keys.
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
                // Keyword only in metadata, orthogonal vector: BM25 is scoped to the text
                // property, so this chunk must not receive any fused score.
                Chunk("doc-h3", 0, "completely unrelated words", [0.0f, 0.0f, 1.0f],
                    Meta(("tag", "zebra quantum xylophone"))),
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

        // Pins the properties: ["text"] scoping — a keyword match in a meta_* property must
        // contribute nothing to the BM25 side (its vector side is orthogonal too, so ~0).
        foreach (var result in results)
        {
            if (string.Equals((string)result.Chunk.DocumentId, "doc-h3", StringComparison.Ordinal))
            {
                Assert.True(
                    result.Score <= 0.01,
                    $"meta_* properties must not feed BM25, but doc-h3 scored {result.Score}");
            }
        }
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

        // Sentinel: a Weaviate instance with ZERO classes rejects every GraphQL request with
        // HTTP 422 before any resolver runs. Keeping one other class alive guarantees the
        // 200-with-errors[] path this test pins, independent of suite order.
        var sentinelClassName = UniqueClassName();
        await store.CreateCollectionAsync(sentinelClassName, 3, TestContext.Current.CancellationToken);

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
