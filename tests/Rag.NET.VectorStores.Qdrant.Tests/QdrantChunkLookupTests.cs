using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Qdrant.Tests;

/// <summary>
/// <see cref="QdrantVectorStore"/>'s keyed lookup, run against a real Qdrant (#318).
/// </summary>
/// <remarks>
/// <para>
/// <b>Qdrant cannot answer this by point id, unlike every SQL backend.</b>
/// <c>CreatePointId</c> returns <see cref="Guid.NewGuid"/>, so a point's id carries no relationship
/// to its <c>(document_id, chunk_index)</c> — the identity exists only as payload. The lookup is a
/// filter, and these tests are against the container because whether a nested <c>should</c> of
/// <c>must</c> pairs actually matches pairs, and whether a negative integer round-trips through a
/// payload match, are facts about Qdrant rather than about this code.
/// </para>
/// <para>
/// The contract is <see cref="IChunkLookup"/>'s and is shared with every other backend, so these
/// mirror the PgVector set deliberately: a divergence between two implementations of one interface
/// is easier to see when the tests are the same shape.
/// </para>
/// </remarks>
public class QdrantChunkLookupTests : IAsyncLifetime
{
    private readonly IContainer _qdrant = new ContainerBuilder("qdrant/qdrant:latest")
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Actix runtime found"))
        .Build();

    private const string CollectionName = "lookup-collection";

    private QdrantVectorStore _sut = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        await _qdrant.StartAsync(TestContext.Current.CancellationToken);
        _port = _qdrant.GetMappedPublicPort(6334);
        _sut = new QdrantVectorStore("localhost", _port, CollectionName, vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _qdrant.DisposeAsync();
    }

    private static EmbeddedChunk Chunk(string documentId, int chunkIndex, string text) => new()
    {
        Chunk = new TextChunk
        {
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Text = text,
        },
        Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f]),
    };

    private async Task StoreAsync(params EmbeddedChunk[] chunks) =>
        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheStoreReportsThatItSupportsLookup()
    {
        var lookup = Assert.IsAssignableFrom<IChunkLookup>(_sut);
        Assert.True(lookup.SupportsChunkLookup);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Chunks come back by identity, and the pairs are matched as pairs — a key naming a document
    /// that exists at an index that does not must not drag in another document's chunk at that
    /// index. Filtering the two payload fields independently would do exactly that.
    /// </summary>
    [Fact]
    public async Task ChunksComeBackByKey_AndThePairsAreMatchedAsPairs()
    {
        await StoreAsync(
            Chunk("doc-a", 0, "a0"),
            Chunk("doc-a", 1, "a1"),
            Chunk("doc-b", 0, "b0"),
            Chunk("doc-b", 1, "b1"));

        var found = await _sut.GetChunksAsync(
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
    /// synthetic entity and relationship chunks, so negative indices are exactly the rows GraphRAG
    /// asks for. A payload condition that could not express a negative integer would return nothing
    /// for precisely the lookups this capability exists to serve, while every other test here
    /// still passed.
    /// </summary>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        await StoreAsync(
            Chunk("graph", -1, "entity one"),
            Chunk("graph", -2, "relationship one"),
            Chunk("graph", 0, "ordinary"));

        var found = await _sut.GetChunksAsync(
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

        var found = await _sut.GetChunksAsync(
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

        var found = await _sut.GetChunksAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    /// <summary>
    /// Metadata survives the keyed read. Local search puts these chunks in front of a model, so a
    /// lookup returning text without its metadata would be a quieter kind of wrong.
    /// </summary>
    [Fact]
    public async Task MetadataComesBackWithTheChunk()
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
                },
            },
            Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f]),
        });

        var found = await _sut.GetChunksAsync(
            [new ChunkKey("doc-m", 0)], TestContext.Current.CancellationToken);

        var only = Assert.Single(found);
        Assert.True(only.Metadata.TryGetValue("source", out var source));
        Assert.Equal("unit-test", source.ToString());
    }

    /// <summary>
    /// A <c>metadata</c> payload field that will not deserialize is backend corruption, and
    /// <see cref="QdrantVectorStore"/> throws naming the document and chunk rather than silently
    /// returning the chunk with empty metadata (#521). Written directly through a raw
    /// <see cref="QdrantClient"/> upsert: nothing reachable through the public API
    /// can produce malformed JSON in this field, and the point id carries no identity here — the
    /// lookup matches on the <c>document_id</c>/<c>chunk_index</c> payload alone.
    /// </summary>
    [Fact]
    public async Task ACorruptMetadataPayloadFieldThrowsNamingTheChunk()
    {
        using var rawClient = new QdrantClient("localhost", _port);
        await rawClient.UpsertAsync(CollectionName,
            [
                new PointStruct
                {
                    Id = Guid.NewGuid(),
                    Vectors = new ReadOnlyMemory<float>([1f, 0f, 0f]).ToArray(),
                    Payload =
                    {
                        ["text"] = "will not survive the read",
                        ["document_id"] = "doc-corrupt",
                        ["chunk_index"] = 9,
                        ["metadata"] = "{not json",
                    },
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetChunksAsync([new ChunkKey("doc-corrupt", 9)], TestContext.Current.CancellationToken));

        Assert.Contains("doc-corrupt", error.Message, StringComparison.Ordinal);
        Assert.Contains("9", error.Message, StringComparison.Ordinal);
    }
}
