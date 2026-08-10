using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseFederatedSearchTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeVectorStore : IVectorStore
    {
        public IReadOnlyList<SearchResult> Results { get; set; } = [];
        public List<EmbeddedChunk> Stored { get; } = [];

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        {
            Stored.AddRange(chunks);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            SearchOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(Results);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = "text", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score,
        };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void UseFederatedSearch_RegistersFederatedStoreAsIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFederatedSearch(f => f
                .AddStore(_ => new FakeVectorStore())
                .AddStore(_ => new FakeVectorStore())))
            .BuildServiceProvider();

        Assert.IsType<FederatedVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public async Task UseFederatedSearch_PrimaryHonored_WritesGoToPrimary()
    {
        var storeA = new FakeVectorStore();
        var storeB = new FakeVectorStore();
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFederatedSearch(f => f
                .AddStore(_ => storeA)
                .AddStore(_ => storeB)
                .WithPrimary(1)))
            .BuildServiceProvider();

        var federated = sp.GetRequiredService<IVectorStore>();
        var chunk = new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        };
        await federated.StoreAsync([chunk], TestContext.Current.CancellationToken);

        Assert.Empty(storeA.Stored);
        Assert.Single(storeB.Stored);
    }

    [Fact]
    public void UseFederatedSearch_FewerThanTwoStores_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddRagNet(rag => rag.UseFederatedSearch(f => f.AddStore(_ => new FakeVectorStore()))));

        Assert.Contains("at least 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseFederatedSearch_PrimaryOutOfBounds_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddRagNet(rag => rag.UseFederatedSearch(f => f
                .AddStore(_ => new FakeVectorStore())
                .AddStore(_ => new FakeVectorStore())
                .WithPrimary(2))));
    }

    [Fact]
    public async Task UseFederatedSearch_NamedStores_AppearInSourceStoreMetadata()
    {
        var storeA = new FakeVectorStore { Results = [MakeResult("docX", 0, 0.9)] };
        var storeB = new FakeVectorStore { Results = [MakeResult("docZ", 1, 0.8)] };
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFederatedSearch(f => f
                .AddStore(_ => storeA, "alpha")
                .AddStore(_ => storeB, "beta")))
            .BuildServiceProvider();

        var federated = sp.GetRequiredService<IVectorStore>();
        var results = await federated.SearchAsync(
            new float[] { 1f }, new SearchOptions { TopK = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal<MetadataValue>("alpha", results.Single(r => string.Equals(r.Chunk.DocumentId, "docX", StringComparison.Ordinal)).Chunk.Metadata["source.store"]);
        Assert.Equal<MetadataValue>("beta", results.Single(r => string.Equals(r.Chunk.DocumentId, "docZ", StringComparison.Ordinal)).Chunk.Metadata["source.store"]);
    }

    [Fact]
    public void UseFederatedSearch_SupersedesPriorIVectorStoreRegistration()
    {
        var services = new ServiceCollection();
        var sp = services
            .AddRagNet(rag =>
            {
                // Simulates a prior UsePgVector/UseQdrant-style registration.
                rag.Services.AddSingleton<IVectorStore>(new FakeVectorStore());
                rag.UseFederatedSearch(f => f
                    .AddStore(_ => new FakeVectorStore())
                    .AddStore(_ => new FakeVectorStore()));
            })
            .BuildServiceProvider();

        Assert.IsType<FederatedVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseFederatedSearch_InvalidRrfK_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddRagNet(rag => rag.UseFederatedSearch(f => f
                .AddStore(_ => new FakeVectorStore())
                .AddStore(_ => new FakeVectorStore())
                .WithRrfK(0))));
    }
}
