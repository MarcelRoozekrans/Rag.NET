using System.Reflection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// Drives <see cref="BookChunkingStrategy"/> over a real book parsed by the real parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The document is the first three chapters of <i>Alice's Adventures in Wonderland</i>,
/// verbatim.</b> Public domain, fetched from Project Gutenberg, stored unmodified. It is a real book
/// with the structure this template exists for: chapter headings, long prose paragraphs, and
/// dialogue — none of which a fixture author would invent the same way.
/// </para>
/// <para>
/// <b>Three chapters rather than the whole book.</b> The full text is 174 KB, and a test fixture
/// that large is paid for on every clone and every build for no additional coverage: three chapters
/// exercise chapter boundaries, intra-chapter splitting and the tail, which is what the strategy
/// claims to do. The excerpt is a contiguous verbatim range — chapter bodies, not the table of
/// contents, which is a distinction this extraction got wrong once and only caught by reading the
/// output.
/// </para>
/// <para>
/// <b>What this does not claim.</b> No retrieval figure, and nothing about books in general. One
/// real book, parsed and chunked end to end.
/// </para>
/// </remarks>
public sealed class RealBookExerciseTests
{
    [Fact]
    public async Task BookTemplate_OverARealBook_SplitsOnItsChapters()
    {
        var chunks = await ChunkAsync(new BookChunkingStrategy(new BookChunkingOptions()));

        // Three chapters of real prose. A strategy that ignored chapter structure would return one
        // chunk for the whole excerpt, or one per paragraph — both outside this band.
        Assert.InRange(chunks.Count, 3, 120);

        // The book's own words, one from the first chapter and one from the third, so a strategy
        // that stopped after the first boundary fails here rather than passing on a prefix.
        Assert.Contains(chunks, c => c.Text.Contains("Rabbit-Hole", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("Caucus-Race", StringComparison.Ordinal));

        // Real prose, not just headings: a strategy that emitted chapter titles and dropped their
        // bodies would satisfy every assertion above.
        Assert.Contains(chunks, c => c.Text.Length > 400);

        Assert.DoesNotContain(chunks, c => string.IsNullOrWhiteSpace(c.Text));
    }

    private static async Task<List<TextChunk>> ChunkAsync(IDocumentChunkingStrategy strategy)
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("real-book"),
            FileName = "real-book.txt",
        };

        await using var stream = typeof(RealBookExerciseTests).GetTypeInfo().Assembly
            .GetManifestResourceStream("Rag.NET.Chunking.Templates.Tests.Resources.real-book.txt")
            ?? throw new InvalidOperationException(
                "The embedded real-book.txt is missing. This test exists to run a real book through " +
                "the real parser; without the document it would assert nothing.");

        var sections = new TextDocumentParser().ParseAsync(stream, metadata, ct);

        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), ct))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        return chunks;
    }
}
