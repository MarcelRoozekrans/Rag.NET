using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Pinecone.Tests;

[Collection("PineconeLocal")]
public class PineconeVectorStoreTests
{
    private readonly PineconeContainerFixture _fixture;

    public PineconeVectorStoreTests(PineconeContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Index_CreateExistsDelete_Lifecycle()
    {
        var indexName = UniqueIndexName();
        ICollectionManageable manageable = CreateStore(indexName);
        try
        {
            Assert.False(await manageable.CollectionExistsAsync(indexName, TestContext.Current.CancellationToken));

            // CreateCollectionAsync polls describe until the index reports ready.
            await manageable.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            Assert.True(await manageable.CollectionExistsAsync(indexName, TestContext.Current.CancellationToken));

            await manageable.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
            Assert.False(await manageable.CollectionExistsAsync(indexName, TestContext.Current.CancellationToken));
        }
        finally
        {
            // Also the assertion-failure safety net: the emulator allocates one data-plane
            // port per index from a range of ten, so a leaked index starves later tests.
            // Delete-of-missing is a no-op (the ICollectionManageable contract): Pinecone
            // answers 404, which the store swallows instead of throwing — so this doubles
            // as the second-delete assertion of the happy path.
            await manageable.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DeleteByDocumentId_MoreChunksThanOneListPage_DeletesAll()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            // list-by-prefix pages at 100 ids, so 120 chunks force the store's do/while
            // pagination loop to fetch a second page — a single-page implementation would
            // leave the tail behind.
            const int ChunkCount = 120;
            var chunks = new List<EmbeddedChunk>(ChunkCount + 1);
            for (var i = 0; i < ChunkCount; i++)
                chunks.Add(Chunk("doc-paged", i, $"paged chunk {i}", [1.0f, 0.0f, 0.0f]));
            chunks.Add(Chunk("doc-keep", 0, "keep me", [0.0f, 1.0f, 0.0f]));
            await store.StoreAsync(chunks, TestContext.Current.CancellationToken);

            var beforeDelete = await store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = ChunkCount + 1 },
                TestContext.Current.CancellationToken);
            Assert.Equal(ChunkCount + 1, beforeDelete.Count);

            await store.DeleteByDocumentIdAsync("doc-paged", TestContext.Current.CancellationToken);

            var results = await store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = ChunkCount + 1 },
                TestContext.Current.CancellationToken);

            var survivor = Assert.Single(results);
            Assert.Equal("doc-keep", (string)survivor.Chunk.DocumentId);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task StoreAndSearch_RoundTrip()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            await store.StoreAsync(
                [
                    Chunk("doc-rt", 0, "cats are great pets", [1.0f, 0.0f, 0.0f],
                        Meta(("source", "unit"))),
                    Chunk("doc-rt", 1, "dogs are loyal friends", [0.0f, 1.0f, 0.0f]),
                ],
                TestContext.Current.CancellationToken);

            var results = await store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 2 },
                TestContext.Current.CancellationToken);

            Assert.Equal(2, results.Count);
            // Text lives in metadata — the store reads it back into SearchResult.
            Assert.Equal("cats are great pets", results[0].Chunk.Text);
            Assert.Equal("doc-rt", (string)results[0].Chunk.DocumentId);
            Assert.Equal(0, results[0].Chunk.ChunkIndex);
            Assert.Equal<MetadataValue>("unit", results[0].Chunk.Metadata["source"]);
            Assert.Equal("dogs are loyal friends", results[1].Chunk.Text);
            Assert.True(results[0].Score > results[1].Score, "nearest result must rank first");
            // Native cosine similarity: identical vector scores ~1 — no conversion applied.
            Assert.InRange(results[0].Score, 0.99, 1.0001);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task StoreAndSearch_TypedMetadata_KindsSurviveRoundTrip()
    {
        // A number reading back as the string "3" is the flattening bug the typed metadata
        // design removes (#91) — so the assertion is on Kind, not on textual form.
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
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
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Search_NumericMetadataFilter_Filters()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            // The chunk nearest the query vector is on page 4 and TopK = 1, so only a
            // server-side numeric $eq filter can return the farther page-3 chunk.
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
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Store_SameChunkTwice_Replaces()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
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
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Search_MetadataFilter_Filters()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            // The chunk NEAREST to the query vector is always excluded by the filters
            // below, and TopK = 1: only server-side filtering can return the farther
            // matching chunk — a client-side post-filter of the top-1 hit would come
            // back empty.
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
            // engineering chunk (team=core) are both excluded — only doc-f3 satisfies
            // both keys.
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
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Search_TopKAndMinScore_Honored()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            await store.StoreAsync(
                [
                    Chunk("doc-k", 0, "identical", [1.0f, 0.0f, 0.0f]),   // cosine 1.0
                    Chunk("doc-k", 1, "close", [0.8f, 0.6f, 0.0f]),        // cosine 0.8
                    Chunk("doc-k", 2, "orthogonal", [0.0f, 1.0f, 0.0f]),   // cosine 0.0
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

            // Pins the native-score semantics: MinScore applies directly to Pinecone's
            // cosine similarity, and the orthogonal chunk scores ~0.0.
            var all = await store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);

            Assert.Equal(3, all.Count);
            Assert.Equal("orthogonal", all[2].Chunk.Text);
            Assert.InRange(all[2].Score, -0.01, 0.01);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Namespace_Isolation()
    {
        var indexName = UniqueIndexName();
        var storeA = CreateStore(indexName, ns: "tenant-a");
        var storeB = CreateStore(indexName, ns: "tenant-b");
        try
        {
            await storeA.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            // Same index, same record id, different namespaces — no cross-talk.
            await storeA.StoreAsync(
                [Chunk("doc-ns", 0, "tenant a text", [1.0f, 0.0f, 0.0f])],
                TestContext.Current.CancellationToken);
            await storeB.StoreAsync(
                [Chunk("doc-ns", 0, "tenant b text", [1.0f, 0.0f, 0.0f])],
                TestContext.Current.CancellationToken);

            var resultsA = await storeA.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);
            var resultsB = await storeB.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);

            Assert.Equal("tenant a text", Assert.Single(resultsA).Chunk.Text);
            Assert.Equal("tenant b text", Assert.Single(resultsB).Chunk.Text);

            // Deletes are namespace-scoped too.
            await storeA.DeleteByDocumentIdAsync("doc-ns", TestContext.Current.CancellationToken);
            var afterDeleteA = await storeA.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);
            var afterDeleteB = await storeB.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);

            Assert.Empty(afterDeleteA);
            Assert.Single(afterDeleteB);
        }
        finally
        {
            await storeA.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesOnlyThatDocument()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            await store.StoreAsync(
                [
                    Chunk("doc-del", 0, "delete me 0", [1.0f, 0.0f, 0.0f]),
                    Chunk("doc-del", 1, "delete me 1", [0.0f, 1.0f, 0.0f]),
                    Chunk("doc-keep", 0, "keep me", [0.0f, 0.0f, 1.0f]),
                    // Colon guard for the pinned list-by-prefix + delete-by-ids path:
                    // this document's ids ("doc-del:7:{i}") also start with "doc-del:",
                    // but the remainder is not purely digits, so it must survive.
                    Chunk("doc-del:7", 0, "prefix collision survivor", [0.5f, 0.5f, 0.0f]),
                ],
                TestContext.Current.CancellationToken);

            await store.DeleteByDocumentIdAsync("doc-del", TestContext.Current.CancellationToken);

            var results = await store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);

            Assert.Equal(2, results.Count);
            var remaining = results.Select(r => (string)r.Chunk.DocumentId).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["doc-del:7", "doc-keep"], remaining);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Store_MissingIndex_FailsFastNamingTheFix()
    {
        var store = CreateStore(UniqueIndexName());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.StoreAsync(
                [Chunk("doc-x", 0, "no index", [1.0f, 0.0f, 0.0f])],
                TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CreateCollectionAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Skip = "Pinecone Local does not support sparse values on dense (dotproduct) indexes: "
        + "the gRPC upsert is rejected with INVALID_ARGUMENT ('wrong vector type') and the REST "
        + "path silently drops the sparse values (verified 2026-07-26 against "
        + "ghcr.io/pinecone-io/pinecone-local:latest). The sparse path only runs against the "
        + "real serverless service — documented in docs/guide/vector-stores.md.")]
    public async Task Sparse_StoreAndSearch_RoundTrip()
    {
        // Kept compiling as the executable contract for a real-service run.
        var indexName = UniqueIndexName();
        var store = CreateSparseStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);
            await store.StoreSparseAsync(
                [
                    (Chunk("doc-sp", 0, "sparse hit", [0.1f, 0.2f, 0.3f]), Sparse((5, 1.5f), (42, 2.0f))),
                    (Chunk("doc-sp", 1, "sparse miss", [0.3f, 0.2f, 0.1f]), Sparse((7, 2.0f))),
                ],
                TestContext.Current.CancellationToken);

            var results = await store.SearchSparseAsync(
                Sparse((5, 1.0f), (42, 1.0f)),
                new SearchOptions { TopK = 1 },
                TestContext.Current.CancellationToken);

            var hit = Assert.Single(results);
            Assert.Equal("sparse hit", hit.Chunk.Text);
            // Sparse scores are raw dot products of matching term weights: 1.5 + 2.0.
            Assert.InRange(hit.Score, 3.4, 3.6);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SparseVariant_CreateCollection_UsesDotproduct_AndServesDenseOps()
    {
        var indexName = UniqueIndexName();
        var store = CreateSparseStore(indexName);
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);

            // The sparse variant creates dotproduct indexes (the only metric accepting
            // sparse values) — pinned against the control plane's describe response.
            using var http = new HttpClient();
            var described = await http.GetStringAsync(
                new Uri(_fixture.Endpoint, $"/indexes/{indexName}"),
                TestContext.Current.CancellationToken);
            Assert.Contains("\"metric\":\"dotproduct\"", described, StringComparison.Ordinal);

            // The metric guard passes on dotproduct, and the inherited dense path works
            // (dot product of unit vector with itself = 1).
            await store.StoreAsync(
                [Chunk("doc-dot", 0, "dense on dotproduct", [1.0f, 0.0f, 0.0f])],
                TestContext.Current.CancellationToken);
            var results = await store.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 1 },
                TestContext.Current.CancellationToken);
            Assert.Equal("dense on dotproduct", Assert.Single(results).Chunk.Text);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SearchSparse_ConfiguredDimensionsDisagreeWithIndex_UsesIndexDimension()
    {
        var indexName = UniqueIndexName();
        // Deliberately wrong VectorDimensions (the 1536 default) against a dim-3 index —
        // the mismatch a user hits by forgetting the third UsePinecone argument. The
        // sparse query's zero dense vector must be sized from the LIVE index: Pinecone
        // rejects a dim-1536 vector against a dim-3 index ("Vector dimension 1536 does
        // not match the dimension of the index 3"). Pinecone Local rejects sparse
        // UPSERTS but serves sparse QUERIES, so this covers the resolution path even
        // though Sparse_StoreAndSearch_RoundTrip cannot run here.
        var store = new PineconeSparseVectorStore(new PineconeOptions
        {
            ApiKey = "pclocal",
            IndexName = indexName,
            VectorDimensions = 1536,
            EnableSparseVectors = true,
            Endpoint = _fixture.Endpoint,
        });
        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);

            var results = await store.SearchSparseAsync(
                Sparse((5, 1.0f), (42, 1.0f)),
                new SearchOptions { TopK = 5 },
                TestContext.Current.CancellationToken);

            // Empty (nothing stored) but NOT an exception — the dimension was corrected.
            Assert.Empty(results);
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SparseVariant_OnCosineIndex_FailsFast()
    {
        var indexName = UniqueIndexName();
        var denseStore = CreateStore(indexName);
        try
        {
            // A cosine index created by the dense store (or any pre-existing tooling)…
            await denseStore.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);

            // …fails fast at the sparse variant's first data-plane use: the real service
            // would accept sparse upserts and only reject at query time.
            var sparseStore = CreateSparseStore(indexName);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sparseStore.StoreSparseAsync(
                    [(Chunk("doc-bad", 0, "sparse on cosine", [1.0f, 0.0f, 0.0f]), Sparse((1, 1.0f)))],
                    TestContext.Current.CancellationToken));

            Assert.Contains("dotproduct", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Cosine", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await denseStore.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }

    private PineconeVectorStore CreateStore(string indexName, string? ns = null) =>
        new(BuildOptions(indexName, ns));

    private PineconeSparseVectorStore CreateSparseStore(string indexName) =>
        new(BuildOptions(indexName, ns: null, enableSparse: true));

    private PineconeOptions BuildOptions(string indexName, string? ns, bool enableSparse = false) =>
        new()
        {
            ApiKey = "pclocal", // Pinecone Local ignores API keys but the client requires one.
            IndexName = indexName,
            VectorDimensions = 3,
            Namespace = ns,
            EnableSparseVectors = enableSparse,
            Endpoint = _fixture.Endpoint,
        };

    private static string UniqueIndexName() => $"test-{Guid.CreateVersion7():N}";

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

    private static SparseVector Sparse(params (int Index, float Value)[] terms)
    {
        var indices = new int[terms.Length];
        var values = new float[terms.Length];
        for (var i = 0; i < terms.Length; i++)
        {
            indices[i] = terms[i].Index;
            values[i] = terms[i].Value;
        }

        return new SparseVector { Indices = indices, Values = values };
    }

    private static Dictionary<string, MetadataValue> Meta(params (string Key, string Value)[] entries)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            metadata[key] = value;
        return metadata;
    }
}
