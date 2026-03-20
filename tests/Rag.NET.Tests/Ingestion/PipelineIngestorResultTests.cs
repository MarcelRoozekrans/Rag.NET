using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using NSubstitute;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

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
