using Rag.NET.Abstractions;
using Rag.NET.Models;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rag.NET.PgVector.Tests;

/// <summary>
/// <see cref="PgVectorStore"/>'s keyed lookup, run against a real PostgreSQL with pgvector (#318).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this capability exists at all.</b> GraphRAG's local search puts the source chunks behind
/// its selected entities in front of the model, chosen by graph provenance and never by score.
/// Without a keyed read the Sources section comes back empty and half a 12,000-token context budget
/// goes unspent — silently, because an empty section looks like a graph with no sources rather than
/// a store that cannot answer.
/// </para>
/// <para>
/// <b>Run against the container rather than a fake.</b> The interesting parts of this
/// implementation are SQL: whether <c>unnest</c> over two arrays zips the pairs, whether a signed
/// <c>chunk_index</c> round-trips a negative value, and whether a missing pair contributes nothing.
/// A substitute would assert none of that. #318 was deferred on the belief these backends could not
/// be exercised without accounts; they can, and this is what that buys.
/// </para>
/// </remarks>
public class PgVectorChunkLookupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .Build();

    private PgVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _sut = new PgVectorStore(_postgres.GetConnectionString(), vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _postgres.DisposeAsync();
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
    /// that exists at an index that does not must not drag in another document's row at that index.
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
        Assert.Contains(found, c => string.Equals(c.DocumentId.Value, "doc-a", StringComparison.Ordinal) && c.ChunkIndex == 1);
        Assert.Contains(found, c => string.Equals(c.DocumentId.Value, "doc-b", StringComparison.Ordinal) && c.ChunkIndex == 0);
    }

    /// <summary>
    /// <b>The trap #318 names.</b> <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to
    /// synthetic entity and relationship chunks, so negative indices are exactly the rows GraphRAG
    /// asks for. A backend filter that assumed unsigned would return nothing for precisely the
    /// lookups this capability exists to serve, and every other test here would still pass.
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
    /// <remarks>
    /// A document deleted since extraction leaves the graph naming chunks that no longer exist.
    /// Failing the query would make an ordinary state fatal.
    /// </remarks>
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
    /// lookup that returned text without its metadata would be a quieter kind of wrong.
    /// </summary>
    [Fact]
    public async Task MetadataComesBackWithTheChunk()
    {
        var embedded = new EmbeddedChunk
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
        };

        await StoreAsync(embedded);

        var found = await _sut.GetChunksAsync(
            [new ChunkKey("doc-m", 0)], TestContext.Current.CancellationToken);

        var only = Assert.Single(found);
        Assert.True(only.Metadata.TryGetValue("source", out var source));
        Assert.Equal("unit-test", source.ToString());
    }
}
