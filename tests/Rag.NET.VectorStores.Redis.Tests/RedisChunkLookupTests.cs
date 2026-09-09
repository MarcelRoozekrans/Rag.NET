using Rag.NET.Abstractions;
using Rag.NET.Models;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// <see cref="RedisVectorStore"/>'s keyed lookup, run against real Redis (#318).
/// </summary>
/// <remarks>
/// <para>
/// <b>Redis is the opposite of Qdrant here.</b> A chunk's Redis key <em>is</em> its identity —
/// <c>prefix + documentId + ":" + chunkIndex</c> — so the lookup is a direct hash read and never
/// touches RediSearch. Nothing is parsed as a query, which is why a document id containing the
/// characters RediSearch treats as syntax needs no escaping on this path.
/// </para>
/// </remarks>
public sealed class RedisChunkLookupTests : IAsyncLifetime
{
    private const int Dimensions = 4;

    private readonly RedisContainer _container =
        new RedisBuilder("redis/redis-stack-server:latest").Build();

    private RedisVectorStore _store = null!;
    private IConnectionMultiplexer _connection = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        _store = new RedisVectorStore(_connection, "lookup-idx", Dimensions);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static EmbeddedChunk Chunk(string documentId, int chunkIndex, string text) => new()
    {
        Chunk = new TextChunk
        {
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Text = text,
        },
        Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f, 0f]),
    };

    private async Task StoreAsync(params EmbeddedChunk[] chunks) =>
        await _store.StoreAsync(chunks, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheStoreReportsThatItSupportsLookup()
    {
        var lookup = Assert.IsAssignableFrom<IChunkLookup>(_store);
        Assert.True(lookup.SupportsChunkLookup);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Chunks come back by identity, and the pairs are matched as pairs — a key naming a document
    /// that exists at an index that does not must not return another document's chunk at that
    /// index. Here that follows from the key composition rather than from a filter.
    /// </summary>
    [Fact]
    public async Task ChunksComeBackByKey_AndThePairsAreMatchedAsPairs()
    {
        await StoreAsync(
            Chunk("doc-a", 0, "a0"),
            Chunk("doc-a", 1, "a1"),
            Chunk("doc-b", 0, "b0"),
            Chunk("doc-b", 1, "b1"));

        var found = await _store.GetChunksAsync(
            [new ChunkKey("doc-a", 1), new ChunkKey("doc-b", 0)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, "doc-a", StringComparison.Ordinal) && c.ChunkIndex == 1);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, "doc-b", StringComparison.Ordinal) && c.ChunkIndex == 0);
    }

    /// <summary>
    /// <b>The trap #318 names.</b> <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to
    /// synthetic entity and relationship chunks. On Redis the index becomes part of the key text,
    /// so a negative one puts a hyphen in the key — harmless for a direct read, and exactly the
    /// thing that would need escaping had this gone through RediSearch.
    /// </summary>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        await StoreAsync(
            Chunk("graph", -1, "entity one"),
            Chunk("graph", -2, "relationship one"),
            Chunk("graph", 0, "ordinary"));

        var found = await _store.GetChunksAsync(
            [new ChunkKey("graph", -1), new ChunkKey("graph", -2)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c => string.Equals(c.Text, "entity one", StringComparison.Ordinal));
        Assert.Contains(found, c => string.Equals(c.Text, "relationship one", StringComparison.Ordinal));
        Assert.DoesNotContain(found, c => string.Equals(c.Text, "ordinary", StringComparison.Ordinal));
    }

    /// <summary>A key with no stored chunk is absent, not an error.</summary>
    [Fact]
    public async Task AKeyWithNoStoredChunkIsAbsentRatherThanAnError()
    {
        await StoreAsync(Chunk("doc-a", 0, "a0"));

        var found = await _store.GetChunksAsync(
            [new ChunkKey("doc-a", 0), new ChunkKey("doc-a", 99), new ChunkKey("missing", 0)],
            TestContext.Current.CancellationToken);

        var only = Assert.Single(found);
        Assert.Equal("a0", only.Text);
    }

    /// <summary>No keys means no round trip and no rows.</summary>
    [Fact]
    public async Task NoKeysReturnsNothing()
    {
        await StoreAsync(Chunk("doc-a", 0, "a0"));

        var found = await _store.GetChunksAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    /// <summary>
    /// A document id containing RediSearch syntax round-trips, because a direct key read parses
    /// nothing. The store escapes such ids for its TAG filters; this path has no filter to escape.
    /// </summary>
    [Fact]
    public async Task ADocumentIdContainingSearchSyntaxIsFoundAnyway()
    {
        await StoreAsync(Chunk("doc-with:colon-and-hyphen", 0, "awkward"));

        var found = await _store.GetChunksAsync(
            [new ChunkKey("doc-with:colon-and-hyphen", 0)],
            TestContext.Current.CancellationToken);

        var only = Assert.Single(found);
        Assert.Equal("awkward", only.Text);
    }

    /// <summary>
    /// The keyed read returns the metadata <c>StoreAsync</c> persisted (#513).
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite — that metadata was absent because the store
    /// persisted none — and was written to fail on the day that changed rather than to be deleted
    /// then. This is that day.
    /// </remarks>
    [Fact]
    public async Task MetadataSurvivesTheRoundTrip()
    {
        await StoreAsync(new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                DocumentId = new DocumentId("doc-m"),
                ChunkIndex = 0,
                Text = "with metadata",
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["source"] = "unit-test",
                    ["page"] = 3,
                    ["draft"] = false,
                },
            },
            Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f, 0f]),
        });

        var found = await _store.GetChunksAsync(
            [new ChunkKey("doc-m", 0)], TestContext.Current.CancellationToken);

        var only = Assert.Single(found);
        Assert.Equal("with metadata", only.Text);
        Assert.Equal(3, only.Metadata.Count);
        Assert.Equal((MetadataValue)"unit-test", only.Metadata["source"]);
        Assert.Equal(MetadataValueKind.Number, only.Metadata["page"].Kind);
        Assert.Equal(3d, only.Metadata["page"].NumberValue);
        Assert.False(only.Metadata["draft"].BooleanValue);
    }

    /// <summary>
    /// A hash written before this store persisted metadata has no <c>metadata</c> field at all.
    /// That is not corruption: it reads as an empty dictionary, so an upgraded deployment keeps
    /// serving its existing chunks rather than throwing on every one of them.
    /// </summary>
    [Fact]
    public async Task AHashWithNoMetadataFieldReadsAsEmptyRatherThanThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = _connection.GetDatabase();
        await database.HashSetAsync(
            "lookup-idx:doc-legacy:0",
            [
                new HashEntry("document_id", "doc-legacy"),
                new HashEntry("chunk_index", 0),
                new HashEntry("text", "written by an older version"),
            ]);

        var found = await _store.GetChunksAsync([new ChunkKey("doc-legacy", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("written by an older version", only.Text);
        Assert.Empty(only.Metadata);
    }

    /// <summary>
    /// A <c>metadata</c> field that is present but will not deserialize is backend corruption, and
    /// this store throws naming the document and chunk — matching
    /// <c>WeaviateVectorStore</c>'s reviewed posture rather than the three stores that still return
    /// an empty dictionary (#521). On this store in particular, "reads as no metadata" is
    /// indistinguishable from the defect #513 fixed.
    /// </summary>
    [Fact]
    public async Task ACorruptMetadataFieldThrowsNamingTheChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = _connection.GetDatabase();
        await database.HashSetAsync(
            "lookup-idx:doc-corrupt:2",
            [
                new HashEntry("document_id", "doc-corrupt"),
                new HashEntry("chunk_index", 2),
                new HashEntry("text", "corrupt metadata"),
                new HashEntry("metadata", "{not json"),
            ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.GetChunksAsync([new ChunkKey("doc-corrupt", 2)], ct));

        Assert.Contains("doc-corrupt", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }
}
