using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Pinecone.Tests;

/// <summary>
/// <see cref="PineconeVectorStore"/>'s keyed lookup, run against Pinecone Local (#318).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinecone can answer by id, because this store derives one.</b> <c>RecordId</c> composes
/// <c>documentId + ":" + chunkIndex</c>, so the lookup is a <c>Fetch</c> — no query vector, no
/// filter. Same shape as Redis, and the opposite of Qdrant, whose ids are random GUIDs.
/// </para>
/// <para>
/// <b>One index for the whole class, deleted in teardown.</b> Pinecone Local allocates one
/// data-plane port per index from a range of ten, so an index leaked by a failing test starves
/// later ones. Each test uses its own document ids instead of its own index.
/// </para>
/// </remarks>
[Collection("PineconeLocal")]
public class PineconeChunkLookupTests : IAsyncLifetime
{
    private readonly PineconeContainerFixture _fixture;
    private readonly string _indexName = $"lookup-{Guid.CreateVersion7():N}";
    private PineconeVectorStore _store = null!;

    public PineconeChunkLookupTests(PineconeContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _store = new PineconeVectorStore(new PineconeOptions
        {
            ApiKey = "pclocal", // Pinecone Local ignores API keys but the client requires one.
            IndexName = _indexName,
            VectorDimensions = 3,
            Endpoint = _fixture.Endpoint,
        });

        await _store.CreateCollectionAsync(_indexName, 3, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Delete-of-missing is a no-op per the ICollectionManageable contract, so this is safe
        // even if creation failed — and skipping it would starve the emulator's port range.
        await _store.DeleteCollectionAsync(_indexName, TestContext.Current.CancellationToken);
        _store.Dispose();
    }

    private static EmbeddedChunk Chunk(
        string documentId,
        int chunkIndex,
        string text,
        Dictionary<string, MetadataValue>? metadata = null) => new()
    {
        Chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
        },
        Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f]),
    };

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
    /// index. Here that follows from the id composition rather than from a filter.
    /// </summary>
    [Fact]
    public async Task ChunksComeBackByKey_AndThePairsAreMatchedAsPairs()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("pairs-a", 0, "a0"),
                Chunk("pairs-a", 1, "a1"),
                Chunk("pairs-b", 0, "b0"),
                Chunk("pairs-b", 1, "b1"),
            ], ct);

        var found = await _store.GetChunksAsync(
            [new ChunkKey("pairs-a", 1), new ChunkKey("pairs-b", 0)], ct);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, "pairs-a", StringComparison.Ordinal) && c.ChunkIndex == 1);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, "pairs-b", StringComparison.Ordinal) && c.ChunkIndex == 0);
    }

    /// <summary>
    /// <b>The trap #318 names, and the one that has caught an unsigned implementation on every
    /// backend so far.</b> <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to synthetic
    /// entity and relationship chunks, so negative indices are exactly the records GraphRAG asks
    /// for.
    /// </summary>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("neg-graph", -1, "entity one"),
                Chunk("neg-graph", -2, "relationship one"),
                Chunk("neg-graph", 0, "ordinary"),
            ], ct);

        var found = await _store.GetChunksAsync(
            [new ChunkKey("neg-graph", -1), new ChunkKey("neg-graph", -2)], ct);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c => string.Equals(c.Text, "entity one", StringComparison.Ordinal));
        Assert.Contains(found, c => string.Equals(c.Text, "relationship one", StringComparison.Ordinal));
        Assert.DoesNotContain(found, c => string.Equals(c.Text, "ordinary", StringComparison.Ordinal));
    }

    /// <summary>A key with no stored chunk is absent, not an error.</summary>
    [Fact]
    public async Task AKeyWithNoStoredChunkIsAbsentRatherThanAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("absent-a", 0, "a0")], ct);

        var found = await _store.GetChunksAsync(
            [new ChunkKey("absent-a", 0), new ChunkKey("absent-a", 99), new ChunkKey("absent-missing", 0)],
            ct);

        var only = Assert.Single(found);
        Assert.Equal("a0", only.Text);
    }

    /// <summary>No keys means no round trip and no records.</summary>
    [Fact]
    public async Task NoKeysReturnsNothing()
    {
        var found = await _store.GetChunksAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    /// <summary>
    /// A document id containing the record-id separator still round-trips.
    /// </summary>
    /// <remarks>
    /// <c>BuildChunk</c> refuses to recover identity from a record id precisely because a document
    /// id may contain <c>:</c>. Constructing an id has no such ambiguity — a chunk index cannot
    /// contain a colon — and the identity still comes back out of metadata rather than out of the
    /// id, which is what makes this safe in one direction and not the other.
    /// </remarks>
    [Fact]
    public async Task ADocumentIdContainingTheSeparatorRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("sep:colon:id", 3, "separated")], ct);

        var found = await _store.GetChunksAsync([new ChunkKey("sep:colon:id", 3)], ct);

        var only = Assert.Single(found);
        Assert.Equal("separated", only.Text);
        Assert.Equal("sep:colon:id", only.DocumentId.Value);
        Assert.Equal(3, only.ChunkIndex);
    }

    /// <summary>
    /// Metadata survives the keyed read. Local search puts these chunks in front of a model, so a
    /// lookup returning text without its metadata would be a quieter kind of wrong.
    /// </summary>
    [Fact]
    public async Task MetadataComesBackWithTheChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("meta-doc", 0, "with metadata",
                    new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "unit-test" }),
            ], ct);

        var found = await _store.GetChunksAsync([new ChunkKey("meta-doc", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal<MetadataValue>("unit-test", only.Metadata["source"]);
    }
}
