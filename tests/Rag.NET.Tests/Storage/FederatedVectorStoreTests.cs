using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class FederatedVectorStoreTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic inline fake — returns a scripted result list, records writes/deletes,
    /// and optionally throws on search.
    /// </summary>
    private sealed class FakeVectorStore : IVectorStore
    {
        public IReadOnlyList<SearchResult> Results { get; set; } = [];
        public Exception? SearchException { get; set; }
        public List<EmbeddedChunk> Stored { get; } = [];
        public List<string> Deleted { get; } = [];
        public SearchOptions? LastSearchOptions { get; private set; }

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        {
            Stored.AddRange(chunks);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            SearchOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSearchOptions = options;
            if (SearchException is not null) throw SearchException;
            return Task.FromResult(Results);
        }

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Deleted.Add(documentId);
            return Task.CompletedTask;
        }
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score, string text = "chunk text") =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score,
        };

    private static EmbeddedChunk MakeEmbedded(string docId, int chunkIndex) =>
        new()
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Embedding = new float[] { 1f, 0f },
        };

    private static readonly ReadOnlyMemory<float> QueryVector = new float[] { 1f, 0f };

    // ── 1. RRF merge order (hand-computed) ───────────────────────────────────

    [Fact]
    public async Task Search_MergesAcrossStores_RrfOrder()
    {
        // Store A: [X, Y]; Store B: [Y, Z]. With k=60 (0-based rank r contributes 1/(60+r+1)):
        //   Y = 1/62 (A, rank 1) + 1/61 (B, rank 0) = 0.0325224...
        //   X = 1/61                                 = 0.0163934...
        //   Z = 1/62                                 = 0.0161290...
        var storeA = new FakeVectorStore { Results = [MakeResult("docX", 0, 0.9), MakeResult("docY", 1, 0.8)] };
        var storeB = new FakeVectorStore { Results = [MakeResult("docY", 1, 0.7), MakeResult("docZ", 2, 0.6)] };
        var federated = new FederatedVectorStore([storeA, storeB], new FederatedStoreOptions());

        var results = await federated.SearchAsync(QueryVector, new SearchOptions { TopK = 10 }, TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal("docY", (string)results[0].Chunk.DocumentId);
        Assert.Equal("docX", (string)results[1].Chunk.DocumentId);
        Assert.Equal("docZ", (string)results[2].Chunk.DocumentId);
        Assert.Equal(1.0 / 62 + 1.0 / 61, results[0].Score, precision: 10);
        Assert.Equal(1.0 / 61, results[1].Score, precision: 10);
        Assert.Equal(1.0 / 62, results[2].Score, precision: 10);
    }

    // ── 2. Per-store failure is skipped ──────────────────────────────────────

    [Fact]
    public async Task Search_StoreFails_SkippedWithOthersServed()
    {
        var failing = new FakeVectorStore { SearchException = new InvalidOperationException("store down") };
        var healthy = new FakeVectorStore { Results = [MakeResult("docA", 0, 0.9)] };
        var federated = new FederatedVectorStore([failing, healthy], new FederatedStoreOptions());

        var results = await federated.SearchAsync(QueryVector, new SearchOptions { TopK = 5 }, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("docA", (string)results[0].Chunk.DocumentId);
    }

    // ── 3. All stores failing throws, naming the stores ──────────────────────

    [Fact]
    public async Task Search_AllFail_ThrowsNamingStores()
    {
        var a = new FakeVectorStore { SearchException = new InvalidOperationException("a down") };
        var b = new FakeVectorStore { SearchException = new TimeoutException("b down") };
        var options = new FederatedStoreOptions { StoreNames = ["alpha", "beta"] };
        var federated = new FederatedVectorStore([a, b], options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => federated.SearchAsync(QueryVector, new SearchOptions { TopK = 5 }, TestContext.Current.CancellationToken));

        Assert.Contains("alpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("beta", ex.Message, StringComparison.Ordinal);
    }

    // ── 4. TopK applied after the merge ──────────────────────────────────────

    [Fact]
    public async Task Search_TopKAppliedPostMerge()
    {
        var storeA = new FakeVectorStore { Results = [MakeResult("docX", 0, 0.9), MakeResult("docY", 1, 0.8)] };
        var storeB = new FakeVectorStore { Results = [MakeResult("docY", 1, 0.7), MakeResult("docZ", 2, 0.6)] };
        var federated = new FederatedVectorStore([storeA, storeB], new FederatedStoreOptions());

        var results = await federated.SearchAsync(QueryVector, new SearchOptions { TopK = 2 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("docY", (string)results[0].Chunk.DocumentId);
        Assert.Equal("docX", (string)results[1].Chunk.DocumentId);
        // Each store still received the caller's options (full per-store rankings feed the RRF merge).
        Assert.Equal(2, storeA.LastSearchOptions!.TopK);
        Assert.Equal(2, storeB.LastSearchOptions!.TopK);
    }

    // ── 5. source.store metadata without mutating the original chunk ─────────

    [Fact]
    public async Task SourceStoreMetadata_Tagged_WithoutMutatingOriginal()
    {
        var original = MakeResult("docX", 0, 0.9);
        original.Chunk.Metadata["existing"] = "value";
        var storeA = new FakeVectorStore { Results = [original] };
        var storeB = new FakeVectorStore { Results = [MakeResult("docZ", 1, 0.5)] };
        var options = new FederatedStoreOptions { StoreNames = ["alpha", "beta"] };
        var federated = new FederatedVectorStore([storeA, storeB], options);

        var results = await federated.SearchAsync(QueryVector, new SearchOptions { TopK = 5 }, TestContext.Current.CancellationToken);

        var tagged = results.Single(r => string.Equals(r.Chunk.DocumentId, "docX", StringComparison.Ordinal));
        Assert.Equal("alpha", tagged.Chunk.Metadata["source.store"]);
        Assert.Equal("value", tagged.Chunk.Metadata["existing"]);

        var fromB = results.Single(r => string.Equals(r.Chunk.DocumentId, "docZ", StringComparison.Ordinal));
        Assert.Equal("beta", fromB.Chunk.Metadata["source.store"]);

        // The source store's chunk instance must not be mutated.
        Assert.False(original.Chunk.Metadata.ContainsKey("source.store"));
    }

    [Fact]
    public async Task SourceStoreMetadata_NoNames_UsesStoreIndex()
    {
        var storeA = new FakeVectorStore { Results = [MakeResult("docX", 0, 0.9)] };
        var storeB = new FakeVectorStore { Results = [MakeResult("docZ", 1, 0.5)] };
        var federated = new FederatedVectorStore([storeA, storeB], new FederatedStoreOptions());

        var results = await federated.SearchAsync(QueryVector, new SearchOptions { TopK = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal("0", results.Single(r => string.Equals(r.Chunk.DocumentId, "docX", StringComparison.Ordinal)).Chunk.Metadata["source.store"]);
        Assert.Equal("1", results.Single(r => string.Equals(r.Chunk.DocumentId, "docZ", StringComparison.Ordinal)).Chunk.Metadata["source.store"]);
    }

    // ── 6. Writes and deletes go to the primary store only ───────────────────

    [Fact]
    public async Task Store_And_Delete_GoToPrimaryOnly()
    {
        var storeA = new FakeVectorStore();
        var storeB = new FakeVectorStore();
        var federated = new FederatedVectorStore([storeA, storeB], new FederatedStoreOptions { PrimaryIndex = 1 });

        await federated.StoreAsync([MakeEmbedded("doc1", 0)], TestContext.Current.CancellationToken);
        await federated.DeleteByDocumentIdAsync("doc1", TestContext.Current.CancellationToken);

        Assert.Empty(storeA.Stored);
        Assert.Empty(storeA.Deleted);
        Assert.Single(storeB.Stored);
        Assert.Equal(["doc1"], storeB.Deleted);
    }

    // ── 7. Constructor validation ────────────────────────────────────────────

    [Fact]
    public void Ctor_FewerThanTwoStores_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FederatedVectorStore([new FakeVectorStore()], new FederatedStoreOptions()));
    }

    [Fact]
    public void Ctor_PrimaryIndexOutOfBounds_Throws()
    {
        IVectorStore[] stores = [new FakeVectorStore(), new FakeVectorStore()];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FederatedVectorStore(stores, new FederatedStoreOptions { PrimaryIndex = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FederatedVectorStore(stores, new FederatedStoreOptions { PrimaryIndex = -1 }));
    }

    [Fact]
    public void Ctor_StoreNamesCountMismatch_Throws()
    {
        IVectorStore[] stores = [new FakeVectorStore(), new FakeVectorStore()];
        Assert.Throws<ArgumentException>(() =>
            new FederatedVectorStore(stores, new FederatedStoreOptions { StoreNames = ["only-one"] }));
    }

    // ── 8. Cancellation propagates ───────────────────────────────────────────

    [Fact]
    public async Task Search_Cancellation_Propagates()
    {
        var storeA = new FakeVectorStore { Results = [MakeResult("docX", 0, 0.9)] };
        var storeB = new FakeVectorStore { Results = [MakeResult("docY", 1, 0.8)] };
        var federated = new FederatedVectorStore([storeA, storeB], new FederatedStoreOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => federated.SearchAsync(QueryVector, new SearchOptions { TopK = 5 }, cts.Token));
    }
}
