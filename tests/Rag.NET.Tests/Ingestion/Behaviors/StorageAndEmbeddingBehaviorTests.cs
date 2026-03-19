using Microsoft.Extensions.AI;
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
                DocumentId = "doc-1",
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

        var chunk1 = new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "world", DocumentId = "doc-1", ChunkIndex = 1 };

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
            Arg.Is<IEnumerable<string>>(texts => texts.SequenceEqual(new[] { "hello", "world" })),
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
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(progress: progress);
        ctx.Chunks.Add(new TextChunk { Text = "hi", DocumentId = "doc-1", ChunkIndex = 0 });

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

        var chunk = new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 };
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
            Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 },
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
            Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 },
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
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()));

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext(progress: progress);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Storing, reports[0].Stage);
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(1, reports[0].Total);
    }
}
