using Rag.NET.Models;
using Rag.NET.Models.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// Metadata filtering against real Redis (#513). Before this, <c>SearchAsync</c> never read
/// <c>MetadataFilter</c> at all and a filtered search silently returned unfiltered results.
/// </summary>
public sealed class RedisMetadataFilterTests : IAsyncLifetime
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
        _store = new RedisVectorStore(
            _connection, "filter-idx", Dimensions, filterableMetadataKeys: ["tenant", "page"]);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static EmbeddedChunk Chunk(
        string documentId, string text, float[] embedding, params (string Key, MetadataValue Value)[] metadata)
    {
        var dictionary = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
            dictionary[key] = value;

        return new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                DocumentId = new DocumentId(documentId),
                ChunkIndex = 0,
                Text = text,
                Metadata = dictionary,
            },
            Embedding = new ReadOnlyMemory<float>(embedding),
        };
    }

    /// <summary>
    /// A declared filterable key is written as its own <c>md_*</c> hash field, alongside the JSON
    /// blob. This is what the index attribute matches; the blob is what the read paths decode.
    /// </summary>
    [Fact]
    public async Task ADeclaredKeyIsWrittenAsItsOwnTagField()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-t", "tagged", [1f, 0f, 0f, 0f], ("tenant", "acme"))], ct);

        var stored = await _connection.GetDatabase().HashGetAsync("filter-idx:doc-t:0", "md_tenant");

        Assert.Equal(
            RedisVectorStore.MetadataToken((MetadataValue)"acme"),
            stored.ToString());
    }

    /// <summary>An undeclared key is written to the blob only — it gets no field of its own.</summary>
    [Fact]
    public async Task AnUndeclaredKeyGetsNoFieldOfItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [Chunk("doc-o", "other", [1f, 0f, 0f, 0f], ("unlisted", "x"))], ct);

        var stored = await _connection.GetDatabase().HashGetAsync("filter-idx:doc-o:0", "md_unlisted");

        Assert.True(stored.IsNull);
    }

}
