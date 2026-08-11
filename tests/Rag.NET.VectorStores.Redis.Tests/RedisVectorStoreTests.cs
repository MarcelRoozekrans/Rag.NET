using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.VectorStores.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// The Redis store against real RediSearch, not a mock — the store's whole surface is what the
/// module does with a query, so a faked client would assert this repository's idea of RediSearch
/// rather than RediSearch.
/// <para>
/// The image is <c>redis/redis-stack-server</c> rather than plain <c>redis</c>, because vector
/// search lives in the RediSearch module and plain Redis answers <c>FT.CREATE</c> with an unknown
/// command.
/// </para>
/// </summary>
public sealed class RedisVectorStoreTests : IAsyncLifetime
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
        _store = new RedisVectorStore(_connection, "test-idx", Dimensions);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task SearchAsync_RanksTheNearestChunkFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-a", 0, "alpha", [1f, 0f, 0f, 0f]),
                Chunk("doc-b", 0, "beta", [0f, 1f, 0f, 0f]),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f }, new SearchOptions { TopK = 2 }, ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-a", results[0].Chunk.DocumentId.Value);
        Assert.Equal("alpha", results[0].Chunk.Text);
    }

    /// <summary>
    /// The score is a <b>similarity</b>, not the cosine distance RediSearch returns.
    /// <para>
    /// <c>vector_score</c> is a distance in [0, 2] where 0 is identical — the opposite direction
    /// from every score in this library. Publishing it unconverted would invert every ranking and
    /// silently break <c>MinScore</c>, which issue #86 named as the likeliest defect here. An
    /// exact-match query must therefore score ~1, not ~0.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SearchAsync_ScoresAnExactMatchNearOne_NotNearZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-a", 0, "alpha", [1f, 0f, 0f, 0f])], ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f }, new SearchOptions { TopK = 1 }, ct);

        Assert.Equal(1.0, Assert.Single(results).Score, precision: 3);
    }

    /// <summary>
    /// <c>MinScore</c> filters on the converted similarity, so a threshold means what it means
    /// everywhere else in the library.
    /// </summary>
    [Fact]
    public async Task SearchAsync_MinScore_ExcludesTheDistantChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-a", 0, "alpha", [1f, 0f, 0f, 0f]),
                Chunk("doc-b", 0, "beta", [0f, 1f, 0f, 0f]),   // orthogonal: similarity ~0
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions { TopK = 10, MinScore = 0.5 },
            ct);

        Assert.Equal("doc-a", Assert.Single(results).Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task DeleteByDocumentIdAsync_RemovesEveryChunkOfThatDocumentOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-a", 0, "alpha one", [1f, 0f, 0f, 0f]),
                Chunk("doc-a", 1, "alpha two", [0.9f, 0.1f, 0f, 0f]),
                Chunk("doc-b", 0, "beta", [0f, 1f, 0f, 0f]),
            ],
            ct);

        await _store.DeleteByDocumentIdAsync("doc-a", ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f }, new SearchOptions { TopK = 10 }, ct);

        Assert.Equal("doc-b", Assert.Single(results).Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task StoreAsync_ReStoringTheSameChunk_ReplacesRatherThanDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-a", 0, "first", [1f, 0f, 0f, 0f])], ct);
        await _store.StoreAsync([Chunk("doc-a", 0, "second", [1f, 0f, 0f, 0f])], ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f }, new SearchOptions { TopK = 10 }, ct);

        Assert.Equal("second", Assert.Single(results).Chunk.Text);
    }

    /// <summary>
    /// Initialising twice must not throw, because a store pointed at an already-provisioned index
    /// is the normal case on every restart — and re-creating the index would discard its vectors.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await _store.InitializeAsync(ct);

        Assert.True(await _store.CollectionExistsAsync("test-idx", ct));
    }

    [Fact]
    public async Task CollectionExistsAsync_IsFalseForAnIndexThatWasNeverCreated()
    {
        Assert.False(
            await _store.CollectionExistsAsync("absent-idx", TestContext.Current.CancellationToken));
    }

    private static EmbeddedChunk Chunk(string documentId, int chunkIndex, string text, float[] vector) =>
        new()
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId(documentId),
                ChunkIndex = chunkIndex,
            },
            Embedding = vector,
        };
}
