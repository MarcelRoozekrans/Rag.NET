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
using System.Runtime.CompilerServices;

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
        var (activities, listener) = CreateListener();
        using var _ = listener;

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
        var (activities, listener) = CreateListener();
        using var _ = listener;

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

    [Fact]
    public async Task IngestAsync_EmitsParseAndChunkSpans()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        // Build a stub parser that yields one section
        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(YieldSections(new DocumentSection
            {
                Text = "hello world",
                DocumentId = new DocumentId("parse-chunk-doc"),
            }, TestContext.Current.CancellationToken));

        // Build a stub chunking strategy that yields one chunk per section
        var chunkingStrategy = Substitute.For<IChunkingStrategy>();
        chunkingStrategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => YieldChunks(new TextChunk
            {
                Text = ci.Arg<DocumentSection>().Text,
                DocumentId = new DocumentId("parse-chunk-doc"),
                ChunkIndex = 0,
            }, TestContext.Current.CancellationToken));

        var parseBehavior = new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = chunkingStrategy,
            ChunkingOptions = new ChunkingOptions(),
        };

        var chunkingBehavior = new ChunkingBehavior();

        var ctx = new IngestionContext
        {
            Stream = new MemoryStream("hello world"u8.ToArray()),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("parse-chunk-doc"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            GetNextBm25DocId = () => 1,
        };

        // Run ParseBehavior (which populates ctx.Chunks), then ChunkingBehavior
        await parseBehavior.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => chunkingBehavior.HandleAsync(c, ct,
                (c2, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c2.Metadata.DocumentId, ChunksStored = c2.Chunks.Count })));

        var parseSpan = activities.FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.parse", StringComparison.Ordinal));
        Assert.NotNull(parseSpan);
        Assert.Equal("parse-chunk-doc", parseSpan.GetTagItem("document.id"));
        Assert.NotNull(parseSpan.GetTagItem("parser.type"));
        Assert.Equal("1", parseSpan.GetTagItem("chunk.count")?.ToString());

        Assert.Contains(activities, a => string.Equals(a.OperationName, "ragnet.chunk", StringComparison.Ordinal));
    }

    private static (List<Activity> activities, ActivityListener listener) CreateListener()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, RagTelemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    private static async IAsyncEnumerable<DocumentSection> YieldSections(
        DocumentSection section,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return section;
    }

    private static async IAsyncEnumerable<TextChunk> YieldChunks(
        TextChunk chunk,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return chunk;
    }
}
