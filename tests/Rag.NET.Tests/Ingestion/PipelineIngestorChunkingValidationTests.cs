using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using NSubstitute;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorChunkingValidationTests
{
    private static PipelineIngestor CreateSut(ChunkingOptions chunkingOptions) => new()
    {
        Pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (ctx, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
        VectorStore = Substitute.For<IVectorStore>(),
        Bm25Index = Substitute.For<IBm25Index>(),
        ChunkingOptions = chunkingOptions,
    };

    private static DocumentMetadata ValidMetadata() =>
        new() { DocumentId = new DocumentId("doc-1"), FileName = "file.txt" };

    [Fact]
    public async Task ChunkingOptions_InvalidMaxChunkSize_ReturnsFailed()
    {
        var sut = CreateSut(new ChunkingOptions { MaxChunkSize = 0, Overlap = 32 });

        var result = await sut.IngestAsync(new MemoryStream([1]), ValidMetadata(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.IsType<RagError.ValidationFailed>(result.Error);
    }

    [Fact]
    public async Task ChunkingOptions_InvalidOverlap_ReturnsFailed()
    {
        var sut = CreateSut(new ChunkingOptions { MaxChunkSize = 256, Overlap = -1 });

        var result = await sut.IngestAsync(new MemoryStream([1]), ValidMetadata(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.IsType<RagError.ValidationFailed>(result.Error);
    }

    [Fact]
    public async Task ChunkingOptions_Valid_Succeeds()
    {
        var sut = CreateSut(new ChunkingOptions { MaxChunkSize = 256, Overlap = 32 });

        var result = await sut.IngestAsync(new MemoryStream([1, 2, 3]), ValidMetadata(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }
}
