using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using NSubstitute;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

/// <summary>Stands in for a package-specific model exception such as <c>VisionDescriptionException</c>.</summary>
file sealed class DerivedModelCallException()
    : ModelCallException("derived", new InvalidOperationException("provider"));

public class PipelineIngestorResultTests
{
    private static PipelineIngestor CreateSut(
        Pipeline<IngestionContext, IngestionResult>? pipeline = null) => new()
    {
        Pipeline = pipeline ?? new Pipeline<IngestionContext, IngestionResult>(
            (ctx, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
        VectorStore = Substitute.For<IVectorStore>(),
        Bm25Index = Substitute.For<IBm25Index>(),
        ChunkingOptions = new ChunkingOptions(),
    };

    /// <summary>A model failure is not a storage failure, and must not be reported as one (#504).</summary>
    /// <remarks>
    /// Until this case existed the catch-all below it mapped every non-parser exception to
    /// <see cref="RagError.StorageFailed"/>, whose own remarks scope it to
    /// <c>IVectorStore</c>/persistence. A rate-limited vision model at parse time therefore sent an
    /// operator to inspect a vector store that had not been asked to do anything.
    /// </remarks>
    [Fact]
    public async Task IngestAsync_ModelCallFails_ReturnsModelCallFailedRatherThanStorageFailed()
    {
        var provider = new InvalidOperationException("HTTP 429 (: ) Provider returned error");
        var thrown = new ModelCallException("The model could not summarise a cluster.", provider);
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((_, _) => throw thrown);
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(
            new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ModelCallFailed>(result.Error);
        Assert.Same(thrown, error.Inner);
        Assert.Same(provider, error.Inner.InnerException);
    }

    /// <summary>
    /// #930 end to end: a failing embedder inside the pipeline is reported as a model failure, not
    /// as <see cref="RagError.StorageFailed"/>. Runs the real <c>EmbeddingBehavior</c> inside the
    /// ingestor rather than throwing a <see cref="ModelCallException"/> by hand.
    /// </summary>
    [Fact]
    public async Task IngestAsync_EmbedderFails_ReturnsModelCallFailedRatherThanStorageFailed()
    {
        var provider = new HttpRequestException("HTTP 429 (Too Many Requests)");
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(_ => throw provider);
        var embedding = new EmbeddingBehavior { Embedder = embedder };
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((ctx, ct) =>
        {
            ctx.Chunks.Add(new TextChunk { Text = "a chunk", DocumentId = ctx.Metadata.DocumentId, ChunkIndex = 0 });
            return embedding.HandleAsync(ctx, ct, static (c, _) =>
                ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));
        });
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(
            new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ModelCallFailed>(result.Error);
        Assert.Same(provider, error.Inner.InnerException);
    }

    /// <summary>
    /// A refusal is not a model failure either. It stays <see cref="RagError.StorageFailed"/> with the
    /// refusal as <c>Inner</c> until #937 gives it a case; this pins the stopgap the docs describe.
    /// </summary>
    [Fact]
    public async Task IngestAsync_EmbedderRefuses_IsNotReportedAsAModelFailure()
    {
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(_ => throw refusal);
        var embedding = new EmbeddingBehavior { Embedder = embedder };
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((ctx, ct) =>
        {
            ctx.Chunks.Add(new TextChunk { Text = "a chunk", DocumentId = ctx.Metadata.DocumentId, ChunkIndex = 0 });
            return embedding.HandleAsync(ctx, ct, static (c, _) =>
                ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));
        });
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(
            new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        var error = Assert.IsType<RagError.StorageFailed>(result.Error);
        Assert.Same(refusal, error.Inner);
    }

    /// <summary>
    /// The marker is caught polymorphically, so a package-specific subclass — the shape
    /// <c>VisionDescriptionException</c> takes — maps too.
    /// </summary>
    [Fact]
    public async Task IngestAsync_ASubclassOfModelCallException_AlsoMapsToModelCallFailed()
    {
        var thrown = new DerivedModelCallException();
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((_, _) => throw thrown);
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.png" };

        var result = await sut.IngestAsync(
            new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.IsType<RagError.ModelCallFailed>(result.Error);
    }

    /// <summary>
    /// Everything else still maps to <see cref="RagError.StorageFailed"/>. Without this, a change
    /// that widened the new branch to catch all exceptions would satisfy the tests above.
    /// </summary>
    [Fact]
    public async Task IngestAsync_AnUnrelatedFailure_StillMapsToStorageFailed()
    {
        var thrown = new InvalidOperationException("the vector store rejected the batch");
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((_, _) => throw thrown);
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(
            new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.StorageFailed>(result.Error);
        Assert.Same(thrown, error.Inner);
    }

    [Fact]
    public async Task IngestAsync_NonReadableStream_ReturnsNonSeekableStream()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };
        var stream = new MemoryStream();
        stream.Close(); // closed stream is not readable

        var result = await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.IsType<RagError.NonSeekableStream>(result.Error);
    }

    [Fact]
    public async Task IngestAsync_NoParser_ReturnsNoParserFound()
    {
        var pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (_, _) => throw new NoParserFoundException("text/rtf"));
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.rtf", ContentType = "text/rtf" };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.NoParserFound>(result.Error);
        Assert.Equal("text/rtf", error.ContentType);
    }

    [Fact]
    public async Task IngestAsync_PipelineSuccess_ReturnsSuccess()
    {
        var expected = new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 3 };
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((_, _) => ValueTask.FromResult(expected));
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1, 2, 3]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.ChunksStored);
    }

    [Fact]
    public async Task IngestAsync_PipelineThrowsUnknown_ReturnsStorageFailed()
    {
        var pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (_, _) => throw new InvalidOperationException("db down"));
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.StorageFailed>(result.Error);
        Assert.Equal("db down", error.Inner.Message);
    }
}
