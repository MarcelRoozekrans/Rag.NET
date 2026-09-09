using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Weaviate.Tests;

/// <summary>
/// <see cref="WeaviateVectorStore"/>'s keyed lookup, run against a real Weaviate (#318).
/// </summary>
/// <remarks>
/// <para>
/// <b>A fourth mechanism.</b> PgVector zips the pairs in SQL, Qdrant filters on payload because its
/// ids are random, Redis reads the key directly because the key is the identity. Weaviate has
/// object UUIDs this store never derives from the chunk, so the identity lives in properties and
/// the lookup is a GraphQL <c>where</c> — with no vector, no hybrid argument and no
/// <c>_additional</c> to select.
/// </para>
/// <para>
/// <b>Against the container, because the interesting parts are Weaviate's.</b> Whether an
/// Or-of-Ands <c>where</c> matches pairs, whether a <c>field</c>-tokenized <c>document_id</c>
/// matches the whole id rather than its word tokens, and whether <c>valueInt</c> accepts a negative
/// — none of that is assertable against a fake.
/// </para>
/// </remarks>
[Collection("Weaviate")]
public class WeaviateChunkLookupTests
{
    private readonly WeaviateContainerFixture _fixture;

    public WeaviateChunkLookupTests(WeaviateContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private WeaviateVectorStore CreateStore(string className) =>
        new(new WeaviateOptions
        {
            Endpoint = _fixture.Endpoint,
            ClassName = className,
            VectorDimensions = 3,
        });

    private static string UniqueClassName() => $"Lookup{Guid.CreateVersion7():N}";

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
        using var store = CreateStore(UniqueClassName());

        var lookup = Assert.IsAssignableFrom<IChunkLookup>(store);
        Assert.True(lookup.SupportsChunkLookup);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Chunks come back by identity, and the pairs are matched as pairs — a key naming a document
    /// that exists at an index that does not must not return another document's chunk at that
    /// index. Filtering the two properties independently would do exactly that.
    /// </summary>
    [Fact]
    public async Task ChunksComeBackByKey_AndThePairsAreMatchedAsPairs()
    {
        using var store = CreateStore(UniqueClassName());
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
    /// <b>The trap #318 names, and the one that has been load-bearing on every backend so far.</b>
    /// <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to synthetic entity and
    /// relationship chunks, so negative indices are exactly the rows GraphRAG asks for. An
    /// implementation that could not express a negative <c>valueInt</c> would return nothing for
    /// precisely those lookups while passing every other test here.
    /// </summary>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        using var store = CreateStore(UniqueClassName());
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

    /// <summary>A key with no stored chunk is absent, not an error.</summary>
    [Fact]
    public async Task AKeyWithNoStoredChunkIsAbsentRatherThanAnError()
    {
        using var store = CreateStore(UniqueClassName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync([Chunk("doc-a", 0, "a0")], ct);

        var found = await store.GetChunksAsync(
            [new ChunkKey("doc-a", 0), new ChunkKey("doc-a", 99), new ChunkKey("missing", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("a0", only.Text);
    }

    /// <summary>No keys means no round trip and no rows.</summary>
    [Fact]
    public async Task NoKeysReturnsNothing()
    {
        using var store = CreateStore(UniqueClassName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync([Chunk("doc-a", 0, "a0")], ct);

        var found = await store.GetChunksAsync([], ct);

        Assert.Empty(found);
    }

    /// <summary>
    /// A document id that shares a word token with another must not match it. The class schema
    /// declares <c>document_id</c> with <c>field</c> tokenization for this reason — under word
    /// tokenization <c>doc-1</c> and <c>doc-2</c> share the <c>doc</c> token and an
    /// <c>Equal</c> filter matches both.
    /// </summary>
    [Fact]
    public async Task DocumentIdsSharingAWordTokenDoNotMatchEachOther()
    {
        using var store = CreateStore(UniqueClassName());
        var ct = TestContext.Current.CancellationToken;

        await store.StoreAsync(
            [
                Chunk("doc-1", 0, "first"),
                Chunk("doc-2", 0, "second"),
            ], ct);

        var found = await store.GetChunksAsync([new ChunkKey("doc-1", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("first", only.Text);
    }

    /// <summary>
    /// A document id containing GraphQL string syntax round-trips rather than breaking the query.
    /// </summary>
    /// <remarks>
    /// The lookup builds a GraphQL document by string concatenation, so an id containing a quote or
    /// a backslash has to be escaped — <c>GraphQlString</c> does it. Interpolating the id raw
    /// produces a malformed query at best and an injected one at worst, and **every other test here
    /// passes either way**, because none of their ids contain anything that needs escaping. This is
    /// the test that fails.
    /// </remarks>
    [Fact]
    public async Task ADocumentIdContainingGraphQlSyntaxIsFoundAnyway()
    {
        using var store = CreateStore(UniqueClassName());
        var ct = TestContext.Current.CancellationToken;

        const string awkward = """doc"with\quote""";

        await store.StoreAsync([Chunk(awkward, 0, "escaped")], ct);

        var found = await store.GetChunksAsync([new ChunkKey(awkward, 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("escaped", only.Text);
        Assert.Equal(awkward, only.DocumentId.Value);
    }

    /// <summary>
    /// Metadata survives the keyed read. Local search puts these chunks in front of a model, so a
    /// lookup returning text without its metadata would be a quieter kind of wrong.
    /// </summary>
    [Fact]
    public async Task MetadataComesBackWithTheChunk()
    {
        using var store = CreateStore(UniqueClassName());
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
