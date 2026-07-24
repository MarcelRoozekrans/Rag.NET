using Microsoft.Extensions.AI;
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

        public Task<IReadOnlyList<(string DocumentId, string ModelId, int Dimension)>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(string DocumentId, string ModelId, int Dimension)>>(
                Rows.Select(kv => (kv.Key, kv.Value.ModelId, kv.Value.Dimension)).ToList());

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
                chunks.Count == 2 &&
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
        Assert.Contains("doc-1", result.Reindexed);
        Assert.Contains("doc-3", result.Reindexed);
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
            Arg.Is<IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)>>(items => items.Count == 2),
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
}
