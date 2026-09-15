using System.Reflection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// Drives <see cref="LegalChunkingStrategy"/> over a real legal document parsed by the real parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The document is the Apache License, Version 2.0, verbatim.</b> It is a real legal instrument
/// with the structure this template exists for — numbered clauses 1 through 9, a definitions block,
/// and an appendix — and it is one of the few real legal documents that can be committed to a public
/// repository without a licensing question, because its own terms permit reproduction. Fetched from
/// <c>apache.org/licenses/LICENSE-2.0.txt</c> and stored unmodified; the assertions below quote its
/// own words rather than markers a fixture planted.
/// </para>
/// <para>
/// <b>Why a real document rather than more unit tests.</b> The package sat at bare
/// <c>VerifiedBy=unit</c> on Milestone 6's allowlist for a month, owed "a real document of each
/// template's kind". The lesson the milestone keeps re-learning is that <c>unit</c> never meant
/// untested — it meant the fixtures could not produce inputs that fail. A hand-built section list
/// cannot disagree with the parser about what a clause heading is, so it cannot catch the case where
/// they do.
/// </para>
/// <para>
/// <b>What this does not claim.</b> No retrieval figure, and nothing about legal documents in
/// general. One real contract, parsed and chunked end to end, is what it establishes.
/// </para>
/// </remarks>
public sealed class RealContractExerciseTests
{
    [Fact]
    public async Task LegalTemplate_OverARealLicence_SplitsOnItsNumberedClauses()
    {
        var chunks = await ChunkAsync(new LegalChunkingStrategy(new LegalChunkingOptions()));

        // The licence has nine numbered clauses. A strategy that ignored clause structure would
        // produce either one chunk or one per paragraph, both far outside this band. The band is
        // deliberate: the exact count is a property of the merger's internals, and this test is
        // about whether clause headings drive the split at all.
        Assert.InRange(chunks.Count, 3, 40);

        // Its own words, from opposite ends of the document, so a strategy that truncated or
        // dropped a tail would fail here rather than pass on a prefix.
        Assert.Contains(
            chunks,
            c => c.Text.Contains("Definitions", StringComparison.Ordinal));

        Assert.Contains(
            chunks,
            c => c.Text.Contains("Disclaimer of Warranty", StringComparison.Ordinal));

        // No chunk may be empty: a clause boundary that fired on a blank line would produce one,
        // and nothing else in the suite would notice.
        Assert.DoesNotContain(chunks, c => string.IsNullOrWhiteSpace(c.Text));

        // CLAUSE BODIES, not just clause headings. Every assertion above quotes text that appears
        // in a heading line, so all four passed while the splitter was emitting heading and body as
        // one section and the merger was discarding the body — the chunks were heading lines alone.
        // This is the assertion that catches it, and it is here because the Book test caught the
        // same defect first and this one did not.
        Assert.Contains(
            chunks,
            c => c.Text.Contains("each Contributor hereby grants to You a perpetual", StringComparison.Ordinal));

        Assert.Contains(chunks, c => c.Text.Length > 400);
    }

    private static async Task<List<TextChunk>> ChunkAsync(IDocumentChunkingStrategy strategy)
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("real-contract"),
            FileName = "real-contract.txt",
        };

        await using var stream = typeof(RealContractExerciseTests).GetTypeInfo().Assembly
            .GetManifestResourceStream("Rag.NET.Chunking.Templates.Tests.Resources.real-contract.txt")
            ?? throw new InvalidOperationException(
                "The embedded real-contract.txt is missing. This test exists to run a real legal " +
                "document through the real parser; without the document it would assert nothing.");

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
