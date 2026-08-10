using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class ReindexStaleTests
{
    private const string CurrentModel = "openai/text-embedding-3-small";

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeVersionStore : IEmbeddingVersionStore
    {
        public Dictionary<string, (string ModelId, int Dimension)> Rows { get; } = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetAsync(string documentId, string modelId, int dimension, CancellationToken cancellationToken = default)
        {
            Rows[documentId] = (modelId, dimension);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EmbeddingVersionStamp>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmbeddingVersionStamp>>(
                Rows.Select(kv => new EmbeddingVersionStamp
                {
                    DocumentId = kv.Key,
                    ModelId = kv.Value.ModelId,
                    Dimension = kv.Value.Dimension,
                    EmbeddedAt = DateTimeOffset.UtcNow,
                }).ToList());

        public Task RemoveAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Rows.Remove(documentId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmbedder(string? providerName, string? modelId, int dimension)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Calls.Add(list);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                list.Select(_ => new Embedding<float>(new float[dimension])).ToList()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) && modelId is not null
                ? new EmbeddingGeneratorMetadata(providerName, defaultModelId: modelId)
                : null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Hand-written fake: substituting <see cref="ISparseEmbeddingGenerator.GenerateAsync"/>
    /// (a <see cref="ValueTask{T}"/> member) via NSubstitute trips EPS06 (hidden struct copy).
    /// </summary>
    private sealed class FakeSparseGenerator(Func<string, SparseVector> generate) : ISparseEmbeddingGenerator
    {
        public ValueTask<SparseVector> GenerateAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(generate(text));
    }

    private static FakeEmbedder CurrentEmbedder(int dimension = 3) =>
        new("openai", "text-embedding-3-small", dimension);

    private static IRagDataManager MakeDataManager(params (string DocId, string[] Texts)[] docs)
    {
        var dataManager = Substitute.For<IRagDataManager>();
        foreach (var (docId, texts) in docs)
        {
            var chunks = texts
                .Select((t, i) => new TextChunk { Text = t, DocumentId = new DocumentId(docId), ChunkIndex = i })
                .ToList();
            dataManager.GetChunksAsync(docId, Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<TextChunk>)chunks);
        }

        return dataManager;
    }

    private static IRagPipeline Pipeline() => Substitute.For<IRagPipeline>();

    /// <summary>
    /// Hand-rolled store that records the operation order and keeps chunks keyed by
    /// (DocumentId, ChunkIndex) — lets tests assert the delete-before-store contract and
    /// that surplus stale chunks do not survive a re-index.
    /// </summary>
    private sealed class RecordingVectorStore : IVectorStore
    {
        public List<string> Operations { get; } = [];
        public Dictionary<(string DocId, int ChunkIndex), EmbeddedChunk> Stored { get; } = [];

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        {
            Operations.Add($"store:{chunks.Count}");
            foreach (var chunk in chunks)
                Stored[(chunk.Chunk.DocumentId.Value, chunk.Chunk.ChunkIndex)] = chunk;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Operations.Add($"delete:{documentId}");
            var toRemove = new List<(string DocId, int ChunkIndex)>();
            foreach (var key in Stored.Keys)
            {
                if (string.Equals(key.DocId, documentId, StringComparison.Ordinal))
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
                Stored.Remove(key);
            return Task.CompletedTask;
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModelChanged_ReindexesStaleDocument_AndRestamps()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-old"] = ("legacy-model", 3);
        versionStore.Rows["doc-fresh"] = (CurrentModel, 3);
        var embedder = CurrentEmbedder(dimension: 3);
        var vectorStore = Substitute.For<IVectorStore>();
        var dataManager = MakeDataManager(("doc-old", ["chunk a", "chunk b"]));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager, cancellationToken: ct);

        Assert.Equal(["doc-old"], result.Reindexed);
        Assert.Empty(result.ReportedStale);
        Assert.Empty(result.Failed);
        await vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(chunks =>
                chunks!.Count == 2 &&
                chunks[0].Chunk.Text == "chunk a" &&
                chunks[1].Chunk.Text == "chunk b" &&
                chunks[0].Embedding.Length == 3),
            Arg.Any<CancellationToken>());
        Assert.Equal((CurrentModel, 3), versionStore.Rows["doc-old"]);
        // Fresh document untouched
        await dataManager.DidNotReceive().GetChunksAsync("doc-fresh", Arg.Any<CancellationToken>());
        Assert.Equal((CurrentModel, 3), versionStore.Rows["doc-fresh"]);
    }

    [Fact]
    public async Task DimensionChanged_SameModel_IsStale()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = (CurrentModel, 3);
        var embedder = CurrentEmbedder(dimension: 5); // model unchanged, new dimension
        var vectorStore = Substitute.For<IVectorStore>();
        var dataManager = MakeDataManager(("doc-1", ["text"]));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager, cancellationToken: ct);

        Assert.Equal(["doc-1"], result.Reindexed);
        Assert.Equal((CurrentModel, 5), versionStore.Rows["doc-1"]);
    }

    [Fact]
    public async Task AllFresh_NoWorkDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = (CurrentModel, 3);
        versionStore.Rows["doc-2"] = (CurrentModel, 3);
        var embedder = CurrentEmbedder(dimension: 3);
        var vectorStore = Substitute.For<IVectorStore>();
        var dataManager = Substitute.For<IRagDataManager>();

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager, cancellationToken: ct);

        Assert.Empty(result.Reindexed);
        Assert.Empty(result.ReportedStale);
        Assert.Empty(result.Failed);
        await vectorStore.DidNotReceiveWithAnyArgs().StoreAsync(default!, Arg.Any<CancellationToken>());
        // Only the dimension probe hit the embedder (once, not per document)
        Assert.Single(embedder.Calls);
    }

    [Fact]
    public async Task WithoutDataManager_StaleDocsAreReportedOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var embedder = CurrentEmbedder();
        var vectorStore = Substitute.For<IVectorStore>();

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager: null, cancellationToken: ct);

        Assert.Empty(result.Reindexed);
        Assert.Equal(["doc-1"], result.ReportedStale);
        await vectorStore.DidNotReceiveWithAnyArgs().StoreAsync(default!, Arg.Any<CancellationToken>());
        Assert.Equal(("legacy-model", 3), versionStore.Rows["doc-1"]); // stamp untouched
    }

    [Fact]
    public async Task PerDocumentFailure_IsCollected_LoopContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        versionStore.Rows["doc-2"] = ("legacy-model", 3);
        versionStore.Rows["doc-3"] = ("legacy-model", 3);
        var embedder = CurrentEmbedder();
        var vectorStore = Substitute.For<IVectorStore>();
        var dataManager = MakeDataManager(("doc-1", ["a"]), ("doc-3", ["c"]));
        dataManager.GetChunksAsync("doc-2", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TextChunk>>(_ => throw new InvalidOperationException("chunks boom"));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager, cancellationToken: ct);

        Assert.Equal(2, result.Reindexed.Count);
        Assert.Contains("doc-1", result.Reindexed, StringComparer.Ordinal);
        Assert.Contains("doc-3", result.Reindexed, StringComparer.Ordinal);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("doc-2", failure.DocumentId);
        Assert.Contains("chunks boom", failure.Error, StringComparison.Ordinal);
        Assert.Equal(("legacy-model", 3), versionStore.Rows["doc-2"]); // failed doc keeps old stamp
    }

    [Fact]
    public async Task UnresolvableIdentity_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new FakeEmbedder(providerName: null, modelId: null, dimension: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pipeline().ReindexStaleAsync(
                new FakeVersionStore(), embedder, Substitute.For<IVectorStore>(), cancellationToken: ct));

        Assert.Contains("EmbeddingVersioningOptions.ModelId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitModelIdOverride_UsedForStaleness()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("override-model", 3);
        var embedder = new FakeEmbedder(providerName: null, modelId: null, dimension: 3);
        var options = new EmbeddingVersioningOptions { ModelId = "override-model" };

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, Substitute.For<IVectorStore>(),
            dataManager: null, options: options, cancellationToken: ct);

        Assert.Empty(result.ReportedStale); // same identity + same dimension → fresh
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Pipeline().ReindexStaleAsync(
                versionStore, CurrentEmbedder(), Substitute.For<IVectorStore>(),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task SparseGeneratorAndSparseStore_RegeneratesSparseVectors()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var embedder = CurrentEmbedder();
        var vectorStore = Substitute.For<IVectorStore, ISparseSearchable>();
        var sparseGenerator = new FakeSparseGenerator(
            _ => new SparseVector { Indices = new[] { 1 }, Values = new[] { 0.5f } });
        var dataManager = MakeDataManager(("doc-1", ["a", "b"]));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager,
            sparseGenerator: sparseGenerator, cancellationToken: ct);

        Assert.Equal(["doc-1"], result.Reindexed);
        await ((ISparseSearchable)vectorStore).Received(1).StoreSparseAsync(
            Arg.Is<IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)>>(items => items!.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SparseFailure_DenseReindexStillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var embedder = CurrentEmbedder();
        var vectorStore = Substitute.For<IVectorStore, ISparseSearchable>();
        var sparseGenerator = new FakeSparseGenerator(
            _ => throw new InvalidOperationException("sparse boom"));
        var dataManager = MakeDataManager(("doc-1", ["a"]));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, vectorStore, dataManager,
            sparseGenerator: sparseGenerator, cancellationToken: ct);

        Assert.Equal(["doc-1"], result.Reindexed);
        Assert.Empty(result.Failed);
        Assert.Equal((CurrentModel, 3), versionStore.Rows["doc-1"]);
    }

    [Fact]
    public async Task EmbedBatchSize_Honored_ForLargeDocuments()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var embedder = CurrentEmbedder();
        var dataManager = MakeDataManager(("doc-1", ["c0", "c1", "c2", "c3", "c4"]));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, embedder, Substitute.For<IVectorStore>(), dataManager,
            ingestionOptions: new IngestionOptions { EmbedBatchSize = 2 }, cancellationToken: ct);

        Assert.Equal(["doc-1"], result.Reindexed);
        // No probe needed (model differs) → exactly the three batches
        Assert.Equal(3, embedder.Calls.Count);
        Assert.Equal(["c0", "c1"], embedder.Calls[0]);
        Assert.Equal(["c2", "c3"], embedder.Calls[1]);
        Assert.Equal(["c4"], embedder.Calls[2]);
    }

    [Fact]
    public async Task Reindex_DeletesBeforeStoring_SurplusStaleChunksDoNotSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var vectorStore = new RecordingVectorStore();

        // Pre-seed: 3 stale chunks under the document; the current source has only 2.
        await vectorStore.StoreAsync(
            [.. Enumerable.Range(0, 3).Select(i => new EmbeddedChunk
            {
                Chunk = new TextChunk { Text = $"old-{i}", DocumentId = new DocumentId("doc-1"), ChunkIndex = i },
                Embedding = new float[3],
            })],
            ct);
        var dataManager = MakeDataManager(("doc-1", ["new a", "new b"]));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, CurrentEmbedder(), vectorStore, dataManager, cancellationToken: ct);

        Assert.Equal(["doc-1"], result.Reindexed);
        // Delete happened after pre-seeding and before the re-store.
        Assert.Equal(["store:3", "delete:doc-1", "store:2"], vectorStore.Operations);
        // The surplus stale chunk (index 2) did not survive.
        Assert.Equal(2, vectorStore.Stored.Count);
        Assert.True(vectorStore.Stored.ContainsKey(("doc-1", 0)));
        Assert.True(vectorStore.Stored.ContainsKey(("doc-1", 1)));
    }

    [Fact]
    public async Task StaleDocWithNoStoredChunks_LandsInFailed_WithActionableMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var vectorStore = new RecordingVectorStore();
        var dataManager = MakeDataManager(("doc-1", []));

        var result = await Pipeline().ReindexStaleAsync(
            versionStore, CurrentEmbedder(), vectorStore, dataManager, cancellationToken: ct);

        Assert.Empty(result.Reindexed);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("doc-1", failure.DocumentId);
        Assert.Contains("no stored chunks", failure.Error, StringComparison.Ordinal);
        Assert.Contains("re-ingest", failure.Error, StringComparison.Ordinal);
        // Nothing was deleted or stored, and the stale stamp was kept.
        Assert.Empty(vectorStore.Operations);
        Assert.Equal(("legacy-model", 3), versionStore.Rows["doc-1"]);
    }

    [Fact]
    public async Task ServiceProviderOverload_ResolvesDependencies_AndHonoursIngestionOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = new FakeVersionStore();
        versionStore.Rows["doc-1"] = ("legacy-model", 3);
        var embedder = CurrentEmbedder();
        var vectorStore = new RecordingVectorStore();
        var dataManager = MakeDataManager(("doc-1", ["c0", "c1", "c2"]));

        var services = new ServiceCollection()
            .AddSingleton<IEmbeddingVersionStore>(versionStore)
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embedder)
            .AddSingleton<IVectorStore>(vectorStore)
            .AddSingleton(dataManager)
            .BuildServiceProvider();

        var result = await Pipeline().ReindexStaleAsync(
            services, new IngestionOptions { EmbedBatchSize = 2 }, ct);

        Assert.Equal(["doc-1"], result.Reindexed);
        Assert.Equal(["delete:doc-1", "store:3"], vectorStore.Operations);
        Assert.Equal((CurrentModel, 3), versionStore.Rows["doc-1"]);
        // EmbedBatchSize=2 honoured: batches of 2 + 1 (no probe — model differs).
        Assert.Equal(2, embedder.Calls.Count);
        Assert.Equal(["c0", "c1"], embedder.Calls[0]);
        Assert.Equal(["c2"], embedder.Calls[1]);
    }

    [Fact]
    public async Task ServiceProviderOverload_NoVersionStore_ThrowsActionable()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pipeline().ReindexStaleAsync(services, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("UseEmbeddingVersioning", ex.Message, StringComparison.Ordinal);
    }
}
