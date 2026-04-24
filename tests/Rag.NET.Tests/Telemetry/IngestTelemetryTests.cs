using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Runtime.CompilerServices;
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

[Collection("Telemetry")]
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
        // document.id tag is already unique enough to disambiguate this test's span from
        // concurrently-emitted activities on the same global ActivitySource.
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

        var cutoff = DateTime.UtcNow.AddMilliseconds(-50);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 1 }));

        Assert.Contains(activities.Where(a => a.StartTimeUtc >= cutoff),
            a => string.Equals(a.OperationName, "ragnet.embed", StringComparison.Ordinal));
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

        // Filter by the unique document.id we set, to disambiguate from concurrently-emitted parse spans.
        var parseSpan = activities.FirstOrDefault(a =>
            string.Equals(a.OperationName, "ragnet.parse", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("document.id") as string, "parse-chunk-doc", StringComparison.Ordinal));
        Assert.NotNull(parseSpan);
        Assert.Equal("parse-chunk-doc", parseSpan.GetTagItem("document.id"));
        Assert.NotNull(parseSpan.GetTagItem("parser.type"));
        Assert.Equal("1", parseSpan.GetTagItem("chunk.count")?.ToString());

        Assert.Contains(activities, a => string.Equals(a.OperationName, "ragnet.chunk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IngestAsync_EmitsStoreSpan()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var vectorStore = Substitute.For<IVectorStore>();
        var bm25Index = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };

        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("store-doc"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            GetNextBm25DocId = () => 1,
        };
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("store-doc"), ChunkIndex = 0 },
            Embedding = new float[] { 0.1f, 0.2f },
        });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var storeSpan = activities.FirstOrDefault(a =>
            string.Equals(a.OperationName, "ragnet.store", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("document.id") as string, "store-doc", StringComparison.Ordinal));
        Assert.NotNull(storeSpan);
        Assert.Equal("store-doc", storeSpan.GetTagItem("document.id"));
        Assert.Equal("1", storeSpan.GetTagItem("chunk.count")?.ToString());
        Assert.NotNull(storeSpan.GetTagItem("vector_store")); // proxy type name, not stable to assert exactly
    }

    [Fact]
    public async Task IngestAsync_OnError_SetsSpanStatusAndIncrementsCounter()
    {
        using var errorsCollector = new MetricCollector<long>(RagTelemetry.Meter, "ragnet.ingest.errors");
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var throwingPipeline = new Pipeline<IngestionContext, IngestionResult>(
            (_, _) => throw new NoParserFoundException("text/rtf"));
        var sut = CreateSut(throwingPipeline);

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("err-doc"),
            FileName = "err.rtf",
            ContentType = "text/rtf",
        };

        // Baseline measurements accumulated before the act (may be non-zero if a parallel test
        // class triggered an ingest error through the shared global Meter).
        var baseline = errorsCollector.GetMeasurementSnapshot().Sum(m => m.Value);

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);

        // Filter by document.id tag — unique to this test.
        var span = activities.FirstOrDefault(a =>
            string.Equals(a.OperationName, "ragnet.ingest", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("document.id") as string, "err-doc", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal(ActivityStatusCode.Error, span.Status);

        Assert.Equal(1, errorsCollector.GetMeasurementSnapshot().Sum(m => m.Value) - baseline);
    }

    [Fact]
    public async Task IngestAsync_RecordsIngestDuration()
    {
        using var durationCollector = new MetricCollector<double>(RagTelemetry.Meter, "ragnet.ingest.duration");

        var sut = CreateSut();
        var stream = new MemoryStream("hello"u8.ToArray());
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("dur-doc"),
            FileName = "dur.txt",
            ContentType = "text/plain",
        };

        // Baseline measurement count before the act (the global Meter may emit from other tests).
        var baselineCount = durationCollector.GetMeasurementSnapshot().Count;

        var _ = await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        var measurements = durationCollector.GetMeasurementSnapshot();
        Assert.True(measurements.Count > baselineCount,
            $"expected new measurement after IngestAsync (baseline={baselineCount}, actual={measurements.Count})");
        Assert.All(measurements, m => Assert.True(m.Value >= 0));
    }

    [Fact]
    public async Task IngestAsync_EmitsStoreSpan_RecordsChunksStored()
    {
        using var chunksCollector = new MetricCollector<long>(RagTelemetry.Meter, "ragnet.chunks.stored");

        var vectorStore = Substitute.For<IVectorStore>();
        var bm25Index = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };

        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("chunks-doc"),
                FileName = "chunks.txt",
                ContentType = "text/plain",
            },
            GetNextBm25DocId = () => 1,
        };
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("chunks-doc"), ChunkIndex = 0 },
            Embedding = new float[] { 0.1f, 0.2f },
        });

        var baseline = chunksCollector.GetMeasurementSnapshot().Sum(m => m.Value);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var measurements = chunksCollector.GetMeasurementSnapshot();
        Assert.Equal(1, measurements.Sum(m => m.Value) - baseline);
    }

    private static (ConcurrentBag<Activity> activities, ActivityListener listener) CreateListener()
    {
        var activities = new ConcurrentBag<Activity>();
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
