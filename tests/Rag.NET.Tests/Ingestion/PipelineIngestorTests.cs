using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorTests
{
    private static PipelineIngestor CreateSut(
        IVectorStore? vectorStore = null,
        IBm25Index? bm25 = null,
        IParentChunkStore? parentStore = null,
        IRagDataManager? dataManager = null,
        IEmbeddingVersionStore? versionStore = null,
        Pipeline<IngestionContext, IngestionResult>? pipeline = null) =>
        new()
        {
            Pipeline = pipeline ?? new Pipeline<IngestionContext, IngestionResult>(
                (ctx, _) => ValueTask.FromResult(
                    new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
            VectorStore = vectorStore ?? Substitute.For<IVectorStore>(),
            Bm25Index = bm25 ?? Substitute.For<IBm25Index>(),
            ChunkingOptions = new ChunkingOptions(),
            ParentStore = parentStore,
            DataManager = dataManager,
            VersionStore = versionStore,
        };

    [Fact]
    public async Task IngestAsync_CreatesContextAndExecutesPipeline()
    {
        IngestionContext? capturedCtx = null;
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((ctx, _) =>
        {
            capturedCtx = ctx;
            return ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 3 });
        });
        var sut = CreateSut(pipeline: pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt", ContentType = "text/plain" };
        using var stream = new MemoryStream("hello"u8.ToArray());
        var ct = TestContext.Current.CancellationToken;

        var result = await sut.IngestAsync(stream, metadata, cancellationToken: ct);

        Assert.NotNull(capturedCtx);
        Assert.Same(stream, capturedCtx!.Stream);
        Assert.Same(metadata, capturedCtx.Metadata);
        Assert.True(result.IsSuccess);
        Assert.Equal(new DocumentId("doc-1"), result.Value.DocumentId);
        Assert.Equal(3, result.Value.ChunksStored);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFromAllRegisteredStores()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var parentStore = Substitute.For<IParentChunkStore>();
        var dataManager = Substitute.For<IRagDataManager>();
        var sut = CreateSut(vectorStore: vectorStore, bm25: bm25, parentStore: parentStore, dataManager: dataManager);
        var ct = TestContext.Current.CancellationToken;

        await sut.DeleteAsync("doc-1", ct);

        await vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", ct);
        bm25.Received(1).Remove("doc-1");
        parentStore.Received(1).Remove("doc-1");
        dataManager.Received(1).Remove("doc-1");
    }

    [Fact]
    public async Task DeleteAsync_RemovesEmbeddingVersionStamp()
    {
        var versionStore = Substitute.For<IEmbeddingVersionStore>();
        var sut = CreateSut(versionStore: versionStore);
        var ct = TestContext.Current.CancellationToken;

        await sut.DeleteAsync("doc-1", ct);

        await versionStore.Received(1).RemoveAsync("doc-1", ct);
    }

    [Fact]
    public async Task DeleteAsync_WhenOptionalStoresNull_DoesNotThrow()
    {
        var sut = CreateSut();
        await sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);
        // Should not throw
    }

    [Fact]
    public async Task IngestAsync_GetNextBm25DocId_IncrementsPerCall()
    {
        var capturedIds = new List<int>();
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((ctx, _) =>
        {
            capturedIds.Add(ctx.GetNextBm25DocId());
            capturedIds.Add(ctx.GetNextBm25DocId());
            return ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });
        });
        var sut = CreateSut(pipeline: pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };
        var ct = TestContext.Current.CancellationToken;

        _ = await sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: ct);
        _ = await sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: ct);

        // Across two calls, the IDs should monotonically increase (thread-safe Interlocked.Increment)
        Assert.Equal(4, capturedIds.Count);
        Assert.True(capturedIds[0] < capturedIds[1]);
        Assert.True(capturedIds[1] < capturedIds[2]);
        Assert.True(capturedIds[2] < capturedIds[3]);
    }
}
