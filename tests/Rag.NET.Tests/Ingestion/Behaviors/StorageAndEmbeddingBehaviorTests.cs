using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class StorageAndEmbeddingBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IngestionContext MakeContext(
        DocumentMetadata? metadata = null,
        IProgress<IngestionProgress>? progress = null)
    {
        return new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = metadata ?? new DocumentMetadata
            {
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            Progress = progress,
            GetNextBm25DocId = () => 42,
        };
    }

    private static ValueTask<IngestionResult> NeverCalledNext(IngestionContext ctx, CancellationToken _)
        => throw new InvalidOperationException("next should not have been called");

    // ── EmbeddingBehavior ────────────────────────────────────────────────────

    [Fact]
    public async Task EmbeddingBehavior_EmbedderCalledWithChunkTexts_AndEmbeddedChunksPopulated()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var chunk1 = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "world", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 };

        var vec1 = new float[] { 0.1f, 0.2f };
        var vec2 = new float[] { 0.3f, 0.4f };

        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(vec1),
                new Embedding<float>(vec2),
            ]));

        var sut = new EmbeddingBehavior { Embedder = embedder };

        var ctx = MakeContext();
        ctx.Chunks.Add(chunk1);
        ctx.Chunks.Add(chunk2);

        await sut.HandleAsync(ctx, ct, (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await embedder.Received(1).GenerateAsync(
            Arg.Is<IEnumerable<string>>(texts => texts!.SequenceEqual(new[] { "hello", "world" })),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.Equal(2, ctx.EmbeddedChunks.Count);
        Assert.Same(chunk1, ctx.EmbeddedChunks[0].Chunk);
        Assert.Same(chunk2, ctx.EmbeddedChunks[1].Chunk);
        Assert.Equal(vec1, ctx.EmbeddedChunks[0].Embedding.ToArray());
        Assert.Equal(vec2, ctx.EmbeddedChunks[1].Embedding.ToArray());
    }

    [Fact]
    public async Task EmbeddingBehavior_ReportsEmbeddingProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(new float[] { 1f }),
            ]));

        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()!));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(progress: progress);
        ctx.Chunks.Add(new TextChunk { Text = "hi", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });

        await sut.HandleAsync(ctx, ct, (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Embedding, reports[0].Stage);
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(1, reports[0].Total);
    }

    // ── StorageBehavior ───────────────────────────────────────────────────────

    [Fact]
    public async Task StorageBehavior_CallsVectorStore_Bm25_And_DataManager()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = dataManager,
        };

        var chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 };
        var embeddedChunk = new EmbeddedChunk { Chunk = chunk, Embedding = new float[] { 0.5f } };

        var ctx = MakeContext();
        ctx.Chunks.Add(chunk);
        ctx.EmbeddedChunks.Add(embeddedChunk);

        var result = await sut.HandleAsync(ctx, ct, NeverCalledNext);

        await vectorStore.Received(1).StoreAsync(ctx.EmbeddedChunks, ct);
        bm25.Received(1).Add(42, chunk);
        dataManager.Received(1).Add(ctx.Metadata, ctx.Chunks);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
    }

    /// <summary>
    /// Storing is a replace, not an append: the previous ingest's BM25 postings and
    /// data-manager rows are dropped <em>before</em> the new ones go in, with no dependence on
    /// <c>IngestionOptions.Overwrite</c>. Ordering is the whole point — removing after adding
    /// would delete the document outright.
    /// </summary>
    [Fact]
    public async Task StorageBehavior_RemovesPreviousEntriesBeforeAdding_WithoutOverwriteOption()
    {
        var ct = TestContext.Current.CancellationToken;
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();

        var sut = new StorageBehavior
        {
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = bm25,
            DataManager = dataManager,
        };

        var chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 };
        var ctx = MakeContext();
        ctx.Chunks.Add(chunk);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new float[] { 0.5f } });

        Assert.Null(ctx.Options); // the webhook path never sets options — the case the fix targets

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Received.InOrder(() =>
        {
            bm25.Remove("doc-1");
            bm25.Add(42, chunk);
        });
        Received.InOrder(() =>
        {
            dataManager.Remove("doc-1");
            dataManager.Add(ctx.Metadata, ctx.Chunks);
        });
    }

    [Fact]
    public async Task StorageBehavior_IsTerminal_NextIsNeverCalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext();
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        // NeverCalledNext will throw if called
        var result = await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Assert.Equal("doc-1", result.DocumentId);
    }

    [Fact]
    public async Task StorageBehavior_NullDataManager_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext();
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        var ex = await Record.ExceptionAsync(() =>
            sut.HandleAsync(ctx, ct, NeverCalledNext).AsTask());

        Assert.Null(ex);
    }

    [Fact]
    public async Task StorageBehavior_ReportsStoringProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()!));

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext(progress: progress);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Storing, reports[0].Stage);
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(1, reports[0].Total);
    }

    // ── StorageBehavior: embedding version stamping ──────────────────────────

    private sealed class FakeLogger : ILogger<StorageBehavior>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedderWithMetadata(EmbeddingGeneratorMetadata? metadata)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GetService(typeof(EmbeddingGeneratorMetadata), Arg.Any<object?>()).Returns(metadata);
        return embedder;
    }

    private static StorageBehavior MakeStorageSut(
        IEmbeddingVersionStore? versionStore,
        IEmbeddingGenerator<string, Embedding<float>>? embedder,
        EmbeddingVersioningOptions? options = null,
        ILogger<StorageBehavior>? logger = null) =>
        new()
        {
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = Substitute.For<IBm25Index>(),
            VersionStore = versionStore,
            Embedder = embedder,
            VersioningOptions = options,
            Logger = logger,
        };

    private static IngestionContext MakeContextWithEmbeddedChunk(string docId = "doc-1", int dimension = 3)
    {
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(docId), FileName = "test.txt", ContentType = "text/plain" },
            GetNextBm25DocId = () => 42,
        };
        var chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId(docId), ChunkIndex = 0 };
        ctx.Chunks.Add(chunk);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new float[dimension] });
        return ctx;
    }

    [Fact]
    public async Task StorageBehavior_StampsEmbeddingVersion_WhenIdentityResolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = Substitute.For<IEmbeddingVersionStore>();
        var embedder = MakeEmbedderWithMetadata(new EmbeddingGeneratorMetadata("openai", defaultModelId: "text-embedding-3-small"));
        var sut = MakeStorageSut(versionStore, embedder);
        var ctx = MakeContextWithEmbeddedChunk(dimension: 3);

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        await versionStore.Received(1).SetAsync("doc-1", "openai/text-embedding-3-small", 3, ct);
    }

    [Fact]
    public async Task StorageBehavior_StampsUsingOverrideModelId()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = Substitute.For<IEmbeddingVersionStore>();
        var sut = MakeStorageSut(versionStore, embedder: null, options: new EmbeddingVersioningOptions { ModelId = "custom-model" });
        var ctx = MakeContextWithEmbeddedChunk(dimension: 5);

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        await versionStore.Received(1).SetAsync("doc-1", "custom-model", 5, ct);
    }

    [Fact]
    public async Task StorageBehavior_IdentityUnresolvable_NoStamp_WarnsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = Substitute.For<IEmbeddingVersionStore>();
        var logger = new FakeLogger();
        var sut = MakeStorageSut(versionStore, MakeEmbedderWithMetadata(metadata: null), logger: logger);

        await sut.HandleAsync(MakeContextWithEmbeddedChunk("doc-1"), ct, NeverCalledNext);
        await sut.HandleAsync(MakeContextWithEmbeddedChunk("doc-2"), ct, NeverCalledNext);

        await versionStore.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("identity is unresolvable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StorageBehavior_StampFailure_IngestionStillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = Substitute.For<IEmbeddingVersionStore>();
        versionStore.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("stamp boom")));
        var logger = new FakeLogger();
        var embedder = MakeEmbedderWithMetadata(new EmbeddingGeneratorMetadata("openai", defaultModelId: "m1"));
        var sut = MakeStorageSut(versionStore, embedder, logger: logger);
        var ctx = MakeContextWithEmbeddedChunk();

        var result = await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Failed to stamp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StorageBehavior_ZeroChunks_DoesNotStamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionStore = Substitute.For<IEmbeddingVersionStore>();
        var embedder = MakeEmbedderWithMetadata(new EmbeddingGeneratorMetadata("openai", defaultModelId: "m1"));
        var sut = MakeStorageSut(versionStore, embedder);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        await versionStore.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
