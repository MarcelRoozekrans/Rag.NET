using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class ParseBehaviorDocumentChunkingTests
{
    private static IngestionContext MakeContext(Stream stream) => new()
    {
        Stream = stream,
        Metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc-1"),
            FileName = "test.txt",
            ContentType = "text/plain",
        },
        GetNextBm25DocId = () => 0,
    };

    private static ValueTask<IngestionResult> NoopNext(IngestionContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task HandleAsync_WithDocumentChunkingStrategy_CallsChunkDocumentAsync()
    {
        // Arrange: a strategy that implements both interfaces
        var strategy = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        // Plain-text parser produces a single section
        var sut = BuildParseBehavior(strategy);

        var ctx = MakeContext(new MemoryStream("hello world"u8.ToArray()));

        // Act
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NoopNext);

        // Assert: chunks were produced (one per section from the plain-text parser)
        Assert.NotEmpty(ctx.Chunks);
        Assert.NotEmpty(ctx.Sections);
        // HierarchicalMergerChunkingStrategy uses the document-level path, which never
        // adds heading_breadcrumb metadata (only the per-section path does).
        Assert.All(ctx.Chunks, c => Assert.False(c.Metadata.ContainsKey("heading_breadcrumb")));
    }

    [Fact]
    public async Task HandleAsync_WithPerSectionStrategy_PopulatesChunksAndSections()
    {
        var strategy = new RecursiveChunkingStrategy();
        var sut = BuildParseBehavior(strategy);

        var ctx = MakeContext(new MemoryStream("hello world"u8.ToArray()));
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NoopNext);

        Assert.NotEmpty(ctx.Chunks);
        Assert.NotEmpty(ctx.Sections);
    }

    private static ParseBehavior BuildParseBehavior(IChunkingStrategy strategy)
    {
        // ParseBehavior uses [Inject] property injection from ZeroAlloc.Inject.
        // Construct directly and set properties for testing.
        var parser = new Rag.NET.Parsers.TextDocumentParser();
        var behavior = new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = strategy,
            ChunkingOptions = new ChunkingOptions(),
        };
        return behavior;
    }
}
