using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Chroma.Tests;

/// <summary>
/// <see cref="ChromaVectorStore"/>'s keyed lookup, run against a real Chroma (#318).
/// </summary>
/// <remarks>
/// <para>
/// <b>This backend needed a new endpoint, which is why it was not done alongside Redis.</b> Its
/// record id is derived the same way — <c>documentId:chunkIndex</c> — so it looks like the same
/// shape, but the client carried only <c>/query</c> and <c>/delete</c>. <c>/query</c> cannot serve
/// a keyed read at all: local search picks its chunks by graph provenance and no embedding returns
/// them.
/// </para>
/// <para>
/// <b>Against the container, because the new endpoint is the risk.</b> Whether <c>/get</c> answers
/// with flat arrays rather than <c>/query</c>'s nested rows, whether it omits ids it does not have
/// instead of erroring, and whether a negative <c>chunk_index</c> round-trips through Chroma's
/// typed metadata are all facts about Chroma, not about this code.
/// </para>
/// </remarks>
[Collection("Chroma")]
public class ChromaChunkLookupTests
{
    private readonly ChromaContainerFixture _fixture;

    public ChromaChunkLookupTests(ChromaContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private ChromaVectorStore CreateStore(string collectionName) =>
        new(new ChromaOptions
        {
            Endpoint = _fixture.Endpoint,
            CollectionName = collectionName,
        });

    private static string UniqueCollectionName() => $"lookup-{Guid.CreateVersion7():N}";

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
        using var store = CreateStore(UniqueCollectionName());

        var lookup = Assert.IsAssignableFrom<IChunkLookup>(store);
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
        using var store = CreateStore(UniqueCollectionName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync(
            [
                Chunk("doc-a", 0, "a0"),
                Chunk("doc-a", 1, "a1"),
                Chunk("doc-b", 0, "b0"),
                Chunk("doc-b", 1, "b1"),
            ], ct);

        var found = await store.GetChunksAsync(
            [new ChunkKey("doc-a", 1), new ChunkKey("doc-b", 0)], ct);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, "doc-a", StringComparison.Ordinal) && c.ChunkIndex == 1);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, "doc-b", StringComparison.Ordinal) && c.ChunkIndex == 0);
    }

    /// <summary>
    /// <b>The trap #318 names, and the only test that has caught an unsigned implementation on
    /// every backend so far.</b> <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to
    /// synthetic entity and relationship chunks, so negative indices are exactly the records
    /// GraphRAG asks for.
    /// </summary>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        using var store = CreateStore(UniqueCollectionName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync(
            [
                Chunk("graph", -1, "entity one"),
                Chunk("graph", -2, "relationship one"),
                Chunk("graph", 0, "ordinary"),
            ], ct);

        var found = await store.GetChunksAsync(
            [new ChunkKey("graph", -1), new ChunkKey("graph", -2)], ct);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c => string.Equals(c.Text, "entity one", StringComparison.Ordinal));
        Assert.Contains(found, c => string.Equals(c.Text, "relationship one", StringComparison.Ordinal));
        Assert.DoesNotContain(found, c => string.Equals(c.Text, "ordinary", StringComparison.Ordinal));
    }

    /// <summary>
    /// A key with no stored chunk is absent, not an error — which for Chroma means the new
    /// <c>/get</c> endpoint omits unknown ids rather than failing the request.
    /// </summary>
    [Fact]
    public async Task AKeyWithNoStoredChunkIsAbsentRatherThanAnError()
    {
        using var store = CreateStore(UniqueCollectionName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync([Chunk("doc-a", 0, "a0")], ct);

        var found = await store.GetChunksAsync(
            [new ChunkKey("doc-a", 0), new ChunkKey("doc-a", 99), new ChunkKey("missing", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("a0", only.Text);
    }

    /// <summary>No keys means no round trip and no records.</summary>
    [Fact]
    public async Task NoKeysReturnsNothing()
    {
        using var store = CreateStore(UniqueCollectionName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync([Chunk("doc-a", 0, "a0")], ct);

        var found = await store.GetChunksAsync([], ct);

        Assert.Empty(found);
    }

    /// <summary>
    /// A document id containing the record-id separator still round-trips.
    /// </summary>
    /// <remarks>
    /// <c>BuildChunk</c> refuses to recover identity from a record id precisely because a document
    /// id may contain <c>:</c>. Constructing one has no such ambiguity — a chunk index cannot
    /// contain a colon — and the identity still comes back out of metadata, which is what makes
    /// this safe in one direction and not the other.
    /// </remarks>
    [Fact]
    public async Task ADocumentIdContainingTheSeparatorRoundTrips()
    {
        using var store = CreateStore(UniqueCollectionName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync([Chunk("sep:colon:id", 3, "separated")], ct);

        var found = await store.GetChunksAsync([new ChunkKey("sep:colon:id", 3)], ct);

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
        using var store = CreateStore(UniqueCollectionName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync(
            [
                Chunk("doc-m", 0, "with metadata",
                    new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "unit-test" }),
            ], ct);

        var found = await store.GetChunksAsync([new ChunkKey("doc-m", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal<MetadataValue>("unit-test", only.Metadata["source"]);
    }
}
