using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using NSubstitute;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorValidationTests
{
    private static PipelineIngestor CreateSut() => new()
    {
        Pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (ctx, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
        VectorStore = Substitute.For<IVectorStore>(),
        Bm25Index = Substitute.For<IBm25Index>(),
        ChunkingOptions = new ChunkingOptions(),
    };

    [Fact]
    public async Task IngestAsync_EmptyFileName_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "" };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("FileName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestAsync_ValidMetadata_Succeeds()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "file.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1, 2, 3]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task IngestAsync_EmbedBatchSizeZero_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "file.txt" };
        var options = new IngestionOptions { EmbedBatchSize = 0 };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("EmbedBatchSize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestAsync_MaxConcurrentEmbeddingBatchesZero_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "file.txt" };
        var options = new IngestionOptions { MaxConcurrentEmbeddingBatches = 0 };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MaxConcurrentEmbeddingBatches", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestAsync_ValidBatchOptions_Succeeds()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "file.txt" };
        var options = new IngestionOptions { EmbedBatchSize = 10, MaxConcurrentEmbeddingBatches = 4 };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }
}
