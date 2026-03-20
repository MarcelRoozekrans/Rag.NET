using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
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
}
