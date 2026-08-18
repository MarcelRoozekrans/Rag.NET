using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

/// <summary>
/// Covers <see cref="IChunkLookup"/> — reading chunks by identity rather than by similarity.
/// </summary>
/// <remarks>
/// The capability exists because GraphRAG's local search puts the source chunks that produced its
/// selected entities in front of the model, chosen by graph provenance and never by score. There is
/// no query vector that returns exactly those chunks, so without a keyed read half of local
/// search's token budget goes unspent.
/// </remarks>
public class ChunkLookupTests
{
    /// <summary>Builds an embedded chunk.</summary>
    /// <param name="docId">Owning document.</param>
    /// <param name="chunkIndex">Position within it.</param>
    /// <returns>The chunk.</returns>
    private static EmbeddedChunk MakeChunk(string docId, int chunkIndex) => new()
    {
        Chunk = new TextChunk
        {
            Text = $"{docId}-{chunkIndex}",
            DocumentId = new DocumentId(docId),
            ChunkIndex = chunkIndex,
        },
        Embedding = new float[] { 1f, 0f },
    };

    [Fact]
    public async Task ChunksComeBackByKeyRegardlessOfSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();
        await store.StoreAsync([MakeChunk("doc-a", 0), MakeChunk("doc-a", 1), MakeChunk("doc-b", 0)], ct);

        var found = await store.GetChunksAsync(
            [new ChunkKey("doc-a", 1), new ChunkKey("doc-b", 0)], ct);

        Assert.Equal(
            ["doc-a-1", "doc-b-0"],
            found.Select(c => c.Text).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <remarks>
    /// A document deleted since extraction leaves the graph naming chunks that no longer exist.
    /// That is ordinary, not an error — local search reads a missing chunk as a deleted document
    /// and carries on.
    /// </remarks>
    [Fact]
    public async Task AKeyWithNoStoredChunkIsAbsentRatherThanAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();
        await store.StoreAsync([MakeChunk("doc-a", 0)], ct);

        var found = await store.GetChunksAsync(
            [new ChunkKey("doc-a", 0), new ChunkKey("doc-gone", 7)], ct);

        Assert.Single(found);
        Assert.Equal("doc-a-0", found[0].Text, StringComparer.Ordinal);
    }

    /// <remarks>
    /// Synthetic graph chunks carry negative indices (<c>−(i+1)</c>), so the key type has to carry
    /// them through unchanged. A lookup that treated the index as unsigned would silently return
    /// nothing for exactly the chunks GraphRAG writes.
    /// </remarks>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();
        await store.StoreAsync([MakeChunk("doc-a", -1), MakeChunk("doc-a", -2)], ct);

        var found = await store.GetChunksAsync([new ChunkKey("doc-a", -2)], ct);

        Assert.Single(found);
        Assert.Equal(-2, found[0].ChunkIndex);
    }

    /// <remarks>
    /// Federation forwards this capability although it forwards no <i>search</i> capability. The
    /// stated reason for dense-only search federation is that two backends' fusion scores are
    /// incomparable scales; a keyed read has no scores, so the union is simply the answer.
    /// </remarks>
    [Fact]
    public async Task FederationUnionsTheStoresItCanRead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = new InMemoryVectorStore();
        using var second = new InMemoryVectorStore();
        await first.StoreAsync([MakeChunk("doc-a", 0)], ct);
        await second.StoreAsync([MakeChunk("doc-b", 0)], ct);

        var federated = new FederatedVectorStore([first, second], new FederatedStoreOptions());

        Assert.True(federated.SupportsChunkLookup);

        var found = await federated.GetChunksAsync(
            [new ChunkKey("doc-a", 0), new ChunkKey("doc-b", 0)], ct);

        Assert.Equal(2, found.Count);
    }

    /// <remarks>
    /// A chunk held by two federated stores is returned once, not twice — the first store in
    /// registration order wins. A caller building a context window would otherwise pay for the same
    /// text twice out of a fixed token budget.
    /// </remarks>
    [Fact]
    public async Task AChunkInTwoFederatedStoresIsReturnedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = new InMemoryVectorStore();
        using var second = new InMemoryVectorStore();
        await first.StoreAsync([MakeChunk("doc-a", 0)], ct);
        await second.StoreAsync([MakeChunk("doc-a", 0)], ct);

        var federated = new FederatedVectorStore([first, second], new FederatedStoreOptions());
        var found = await federated.GetChunksAsync([new ChunkKey("doc-a", 0)], ct);

        Assert.Single(found);
    }

    /// <remarks>
    /// <b>The reason <see cref="IChunkLookup.SupportsChunkLookup"/> exists.</b> A store with no
    /// keyed read must report so, rather than being detected by an <c>is IChunkLookup</c> test that
    /// a forwarding decorator would answer <see langword="true"/> to on its behalf. Without the
    /// probe, local search would render an empty Sources section and nothing would say why.
    /// </remarks>
    [Fact]
    public async Task AStoreWithoutTheCapabilityReportsSoRatherThanAnsweringEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        using var lookupCapable = new InMemoryVectorStore();
        await lookupCapable.StoreAsync([MakeChunk("doc-a", 0)], ct);

        var federated = new FederatedVectorStore(
            [new NoLookupStore(), new NoLookupStore()], new FederatedStoreOptions());

        Assert.False(federated.SupportsChunkLookup);
        Assert.Empty(await federated.GetChunksAsync([new ChunkKey("doc-a", 0)], ct));

        // And the capability is genuinely detectable when present, so the two are distinguishable.
        var mixed = new FederatedVectorStore([new NoLookupStore(), lookupCapable], new FederatedStoreOptions());
        Assert.True(mixed.SupportsChunkLookup);
        Assert.Single(await mixed.GetChunksAsync([new ChunkKey("doc-a", 0)], ct));
    }

    /// <summary>A store implementing only the required surface, with no keyed read.</summary>
    private sealed class NoLookupStore : IVectorStore
    {
        public Task StoreAsync(
            IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(
            string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
