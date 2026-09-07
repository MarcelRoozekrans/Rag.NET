using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
        IEnumerable<IDocumentScopedStore>? documentScopedStores = null,
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
            DocumentScopedStores = documentScopedStores ?? [],
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

    /// <summary>
    /// Deleting a document tells every registered document-scoped store to forget it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes is #338, and it was silent by construction.</b>
    /// <c>IRaptorLeafStore</c> lives in a package core cannot reference, so deletion never reached
    /// it: the leaves survived, the next corpus build read their text back, and the summary landed
    /// under <c>raptor://corpus-tree</c> carrying no document id — unattributable, unremovable and
    /// searchable. <c>RemoveDocumentAsync</c> had existed since #331 with zero production callers.
    /// </para>
    /// <para>
    /// Two stores rather than one, because the collection is the point: the interface exists so
    /// packages core cannot name may register their own, and a loop that only ever ran once would
    /// not show that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_TellsEveryDocumentScopedStoreToForgetTheDocument()
    {
        var first = Substitute.For<IDocumentScopedStore>();
        var second = Substitute.For<IDocumentScopedStore>();
        var sut = CreateSut(documentScopedStores: [first, second]);
        var ct = TestContext.Current.CancellationToken;

        await sut.DeleteAsync("doc-1", ct);

        await first.Received(1).RemoveDocumentAsync("doc-1", ct);
        await second.Received(1).RemoveDocumentAsync("doc-1", ct);
    }

    /// <summary>None registered is the ordinary case, not a missing dependency.</summary>
    [Fact]
    public async Task DeleteAsync_WithNoDocumentScopedStores_DoesNotThrow()
    {
        var sut = CreateSut(documentScopedStores: []);
        await sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);
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
    public async Task IngestAsync_OpensDocumentIdScope_CoveringThePipelineExecution()
    {
        var logger = new FakeLogger<PipelineIngestor>();
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((ctx, _) =>
        {
            // A behavior logging mid-pipeline should inherit the document_id scope
            // PipelineIngestor opened around Pipeline.ExecuteAsync.
            ScopeProbeLog.Emit(logger);
            return ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 1 });
        });
        var sut = CreateSut(pipeline: pipeline);
        sut.Logger = logger;
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-42"), FileName = "f.txt", ContentType = "text/plain" };

        _ = await sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        var scope = Assert.Single(record.Scopes);
        var state = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object>>>(scope);
        var pair = Assert.Single(state);
        Assert.Equal("document_id", pair.Key);
        Assert.Equal("doc-42", pair.Value);
    }
}
