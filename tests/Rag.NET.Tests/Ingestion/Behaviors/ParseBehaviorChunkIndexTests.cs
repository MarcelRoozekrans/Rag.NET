using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using NSubstitute;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

/// <summary>
/// Pins the default per-section chunking path (<see cref="ParseBehavior"/> +
/// <see cref="RecursiveChunkingStrategy"/>) to produce document-wide unique
/// <see cref="TextChunk.ChunkIndex"/> values.
///
/// Each of the two sections below is long enough, relative to a deliberately small
/// <see cref="ChunkingOptions.MaxChunkSize"/>, that <see cref="RecursiveChunkingStrategy"/>
/// must split it into at least two chunks. This is required: if a section produced only
/// one chunk, per-section-restarting indices (0 for section 1, 0 for section 2) would never
/// collide with each other and the test would pass vacuously without exercising the defect.
/// </summary>
public class ParseBehaviorChunkIndexTests
{
    private const int MaxChunkSize = 50;

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
    public async Task ChunkPerSectionAsync_TwoMultiChunkSections_AllChunkIndicesAreDistinct()
    {
        var ct = TestContext.Current.CancellationToken;

        // Each section's text is far longer than MaxChunkSize and has no long runs without
        // spaces, so RecursiveChunkingStrategy's word-boundary packing is guaranteed to split
        // it into multiple chunks. Verified independently below via ChunkCountPerSection_IsAtLeastTwo.
        var section1 = new DocumentSection
        {
            Text = "Alpha bravo charlie delta echo foxtrot golf hotel india juliett kilo lima " +
                   "mike november oscar papa quebec romeo sierra tango uniform victor whiskey " +
                   "xray yankee zulu one two three four five six seven eight nine ten.",
            DocumentId = new DocumentId("doc-1"),
        };
        var section2 = new DocumentSection
        {
            Text = "Eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen " +
                   "twenty twentyone twentytwo twentythree twentyfour twentyfive twentysix " +
                   "twentyseven twentyeight twentynine thirty red green blue yellow purple orange.",
            DocumentId = new DocumentId("doc-1"),
        };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section1, section2));

        var sut = new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = new RecursiveChunkingStrategy(),
            ChunkingOptions = new ChunkingOptions { MaxChunkSize = MaxChunkSize, Overlap = 0 },
        };

        var ctx = MakeContext();
        await sut.HandleAsync(ctx, ct, NoopNext);

        var indices = ctx.Chunks.Select(c => c.ChunkIndex).ToList();
        var distinctIndices = indices.Distinct().ToList();

        Assert.Equal(
            indices.Count,
            distinctIndices.Count);
    }

    [Fact]
    public async Task ChunkCountPerSection_IsAtLeastTwo()
    {
        // Sanity check for the fixture above: confirms, independent of ParseBehavior, that
        // RecursiveChunkingStrategy alone produces >= 2 chunks per section at MaxChunkSize=50.
        // Without this, the collision test above could pass vacuously (see class remarks).
        var ct = TestContext.Current.CancellationToken;
        var strategy = new RecursiveChunkingStrategy();
        var options = new ChunkingOptions { MaxChunkSize = MaxChunkSize, Overlap = 0 };

        var section1 = new DocumentSection
        {
            Text = "Alpha bravo charlie delta echo foxtrot golf hotel india juliett kilo lima " +
                   "mike november oscar papa quebec romeo sierra tango uniform victor whiskey " +
                   "xray yankee zulu one two three four five six seven eight nine ten.",
            DocumentId = new DocumentId("doc-1"),
        };
        var section2 = new DocumentSection
        {
            Text = "Eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen " +
                   "twenty twentyone twentytwo twentythree twentyfour twentyfive twentysix " +
                   "twentyseven twentyeight twentynine thirty red green blue yellow purple orange.",
            DocumentId = new DocumentId("doc-1"),
        };

        var section1Chunks = await strategy.ChunkAsync(section1, options, ct).ToListAsync(ct);
        var section2Chunks = await strategy.ChunkAsync(section2, options, ct).ToListAsync(ct);

        Assert.True(section1Chunks.Count >= 2, $"section1 produced {section1Chunks.Count} chunk(s); need >= 2");
        Assert.True(section2Chunks.Count >= 2, $"section2 produced {section2Chunks.Count} chunk(s); need >= 2");
    }
}
