using System.Diagnostics;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

public class IngestTelemetryTests
{
    private static PipelineIngestor CreateSut(
        Pipeline<IngestionContext, IngestionResult>? pipeline = null) => new()
    {
        Pipeline = pipeline ?? new Pipeline<IngestionContext, IngestionResult>(
            (ctx, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 1 })),
        VectorStore = Substitute.For<IVectorStore>(),
        Bm25Index = Substitute.For<IBm25Index>(),
        ChunkingOptions = new ChunkingOptions(),
    };

    [Fact]
    public async Task IngestAsync_EmitsIngestSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, RagTelemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var sut = CreateSut();

        var stream = new MemoryStream("hello world"u8.ToArray());
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("test-doc"),
            FileName = "test.txt",
            ContentType = "text/plain",
        };

        var result = await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains(activities, a => string.Equals(a.OperationName, "ragnet.ingest", StringComparison.Ordinal));
        var span = activities.First(a => string.Equals(a.OperationName, "ragnet.ingest", StringComparison.Ordinal));
        Assert.Equal("test-doc", span.GetTagItem("document.id"));
        Assert.Equal("text/plain", span.GetTagItem("content.type"));
    }
}
