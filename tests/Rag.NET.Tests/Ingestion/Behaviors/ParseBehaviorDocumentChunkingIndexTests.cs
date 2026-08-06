using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

/// <summary>
/// Pins the document-level chunking branch of <see cref="ParseBehavior"/>
/// (<c>ChunkDocumentAsync</c>, taken when <see cref="IChunkingStrategy"/> also implements
/// <see cref="IDocumentChunkingStrategy"/>): all sections go into a single
/// <c>docStrategy.ChunkDocumentAsync</c> call, so a well-behaved strategy can number chunks
/// globally across the whole document instead of restarting per section. Verified for
/// <see cref="HierarchicalMergerChunkingStrategy"/> as a representative real implementer —
/// see Task 2's investigation notes for the other twelve implementers, none of which restart
/// their counter per section either.
/// </summary>
public class ParseBehaviorDocumentChunkingIndexTests
{
    private static IngestionContext MakeContext() => new()
    {
        Stream = new MemoryStream(),
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

    private static async IAsyncEnumerable<T> Yield<T>(params T[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_MultiSectionDocument_AllChunkIndicesAreDistinct()
    {
        var ct = TestContext.Current.CancellationToken;

        // Three level-1-headed sections: HierarchicalMergerChunkingStrategy flushes one chunk
        // per heading, so this document-level call yields three chunks numbered globally by
        // the strategy's own running counter (chunkIndex in ChunkDocumentAsync), not reset
        // per section the way the per-section ChunkAsync path used to be.
        var section1 = new DocumentSection
        {
            Text = "First section body.",
            DocumentId = new DocumentId("doc-1"),
            Heading = "Chapter One",
            HeadingLevel = 1,
        };
        var section2 = new DocumentSection
        {
            Text = "Second section body.",
            DocumentId = new DocumentId("doc-1"),
            Heading = "Chapter Two",
            HeadingLevel = 1,
        };
        var section3 = new DocumentSection
        {
            Text = "Third section body.",
            DocumentId = new DocumentId("doc-1"),
            Heading = "Chapter Three",
            HeadingLevel = 1,
        };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section1, section2, section3));

        var strategy = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());
        var sut = new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = strategy,
            ChunkingOptions = new ChunkingOptions(),
        };

        var ctx = MakeContext();
        await sut.HandleAsync(ctx, ct, NoopNext);

        Assert.True(ctx.Chunks.Count >= 2, $"expected at least 2 chunks, got {ctx.Chunks.Count}");
        var indices = ctx.Chunks.Select(c => c.ChunkIndex).ToList();
        Assert.Equal(indices.Count, indices.Distinct().Count());
    }
}
