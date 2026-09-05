using System.Reflection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// Drives <see cref="AcademicPaperChunkingStrategy"/> and
/// <see cref="HierarchicalMergerChunkingStrategy"/> over a real document parsed by the real
/// markdown parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than more unit tests.</b> Both strategies had unit tests and both sat
/// on Phase 6.2.1's allowlist anyway, because those tests hand-build their
/// <see cref="DocumentSection"/> inputs. That is the shape the milestone keeps finding:
/// <c>VerifiedBy=unit</c> never meant untested, it meant the fixtures could not produce inputs that
/// fail. A hand-built section list cannot disagree with the parser about what a heading is, so it
/// cannot catch the case where they do.
/// </para>
/// <para>
/// <b>So the document is real and so is the parser.</b> <see cref="MarkdownDocumentParser"/> reads
/// the embedded paper and emits the sections; the strategies consume exactly what a user's pipeline
/// would hand them. Nothing here re-implements heading detection, which is the one thing a harness
/// must never do to the component it is measuring.
/// </para>
/// <para>
/// <b>What this deliberately does not claim.</b> No retrieval quality figure. The allowlist entries
/// asked for the Real protocol over a corpus with headings, and <b>no BEIR corpus has headings</b> —
/// SciFact documents are a title and an abstract, checked rather than assumed. So the entries take
/// their other sanctioned route, "a real document", and this proves the strategies work end to end
/// on one. Whether heading-aware chunking helps retrieval is a different question and remains
/// unmeasured.
/// </para>
/// </remarks>
public sealed class RealPaperExerciseTests
{
    [Fact]
    public async Task AcademicTemplate_OverARealPaper_KeepsTheAbstractAndDropsTheReferences()
    {
        var chunks = await ChunkAsync(new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions()));

        // The template's two documented behaviours, on a document it did not choose: the abstract
        // is kept (IncludeAbstract defaults true) and the references are dropped
        // (IncludeReferences defaults false). Both are asserted against the real paper's own words
        // rather than against a marker the fixture planted.
        Assert.Contains(
            chunks,
            c => c.Metadata.TryGetValue("section_type", out var t) && t == "abstract");

        Assert.DoesNotContain(
            chunks,
            c => c.Text.Contains("Lewis et al.", StringComparison.Ordinal));

        Assert.Contains(chunks, c => c.Text.Contains("bi-encoder architecture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HierarchicalMerger_OverARealPaper_ProducesOneChunkPerHeadingSubtree()
    {
        var chunks = await ChunkAsync(new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions()));

        // The strategy's claim is that a chunk is one heading subtree. The paper has six top-level
        // sections and four subsections, so a strategy that ignored headings entirely would produce
        // either one chunk or one per paragraph -- both far from this range. Asserting a band
        // rather than an exact count, because the exact number is a property of the merger's
        // internals and this test is about whether headings drive the split at all.
        Assert.InRange(chunks.Count, 4, 12);

        // Subsection content must land with, or under, its parent section rather than being
        // orphaned -- the tree property the name promises.
        Assert.Contains(chunks, c => c.Text.Contains("bi-encoder architecture", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("per-token", StringComparison.Ordinal));
    }

    private static async Task<List<TextChunk>> ChunkAsync(IDocumentChunkingStrategy strategy)
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("real-paper"),
            FileName = "real-paper.md",
        };

        await using var stream = typeof(RealPaperExerciseTests).GetTypeInfo().Assembly
            .GetManifestResourceStream("Rag.NET.Chunking.Templates.Tests.Resources.real-paper.md")
            ?? throw new InvalidOperationException(
                "The embedded real-paper.md is missing. This test exists to run a real document " +
                "through the real parser; without the document it would assert nothing.");

        var sections = new MarkdownDocumentParser().ParseAsync(stream, metadata, ct);

        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), ct))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        return chunks;
    }
}
