using System.Diagnostics;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
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
        var span = activities.FirstOrDefault(a =>
            string.Equals(a.OperationName, "ragnet.ingest", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("document.id") as string, "test-doc", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal("text/plain", span.GetTagItem("content.type"));
    }

    [Fact]
    public async Task IngestAsync_EmitsEmbedSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, RagTelemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(new float[] { 0.1f, 0.2f }),
            ]));

        var sut = new EmbeddingBehavior { Embedder = embedder };

        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("embed-doc"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            GetNextBm25DocId = () => 1,
        };
        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = new DocumentId("embed-doc"), ChunkIndex = 0 });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 1 }));

        Assert.Contains(activities, a => string.Equals(a.OperationName, "ragnet.embed", StringComparison.Ordinal));
    }
}
