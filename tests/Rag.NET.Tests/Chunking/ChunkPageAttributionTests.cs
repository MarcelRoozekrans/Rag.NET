using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

/// <summary>
/// Page attribution (issue #82): strategies copy <see cref="DocumentSection.PageNumber"/> into
/// the reserved <see cref="ReservedMetadataKeys.Page"/>/<see cref="ReservedMetadataKeys.PageEnd"/>
/// metadata pair as <see cref="MetadataValueKind.Number"/> values. Per-section strategies stamp
/// both with the section's page; merging strategies report min/max across the contributing
/// sections, ignoring sections without a page; unpaginated sources write neither key.
/// </summary>
public class ChunkPageAttributionTests
{
    private static readonly DocumentId DocId = new("doc-1");

    private static DocumentSection Section(
        string text, int? page = null, string? heading = null, int? level = null) =>
        new() { Text = text, DocumentId = DocId, PageNumber = page, Heading = heading, HeadingLevel = level };

    private static async IAsyncEnumerable<DocumentSection> Sections(params DocumentSection[] sections)
    {
        foreach (var s in sections)
            yield return s;
        await Task.CompletedTask;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(params float[][] vectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.Arg<IEnumerable<string>>()!.ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                for (int i = 0; i < inputs.Count; i++)
                    result.Add(new Embedding<float>(i < vectors.Length ? vectors[i] : vectors[^1]));
                return Task.FromResult(result);
            });
        return embedder;
    }

    /// <summary>
    /// Both keys present, both carrying <em>numbers</em> — a page that reads back as the string
    /// <c>"3"</c> is the flattening bug this design removes, so kind-sensitive
    /// <see cref="MetadataValue"/> equality is the assertion, not textual comparison.
    /// </summary>
    private static void AssertPages(TextChunk chunk, int page, int pageEnd)
    {
        Assert.Equal((MetadataValue)page, chunk.Metadata[ReservedMetadataKeys.Page]);
        Assert.Equal((MetadataValue)pageEnd, chunk.Metadata[ReservedMetadataKeys.PageEnd]);
    }

    private static void AssertNoPages(TextChunk chunk)
    {
        Assert.False(chunk.Metadata.ContainsKey(ReservedMetadataKeys.Page));
        Assert.False(chunk.Metadata.ContainsKey(ReservedMetadataKeys.PageEnd));
    }

    // ── Per-section strategies: page = page_end = the section's page ──────────

    [Fact]
    public async Task Recursive_PaginatedSection_StampsEveryChunkWithItsPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new RecursiveChunkingStrategy();
        var section = Section(string.Join(" ", Enumerable.Repeat("word", 100)), page: 3);

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions { MaxChunkSize = 100 }, ct).ToListAsync(ct);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => AssertPages(c, 3, 3));
    }

    [Fact]
    public async Task Recursive_UnpaginatedSection_WritesNeitherKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new RecursiveChunkingStrategy();

        var chunks = await sut.ChunkAsync(Section("Plain text."), new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertNoPages(chunk);
    }

    [Fact]
    public async Task FixedSize_PaginatedSection_StampsBothKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new FixedSizeChunkingStrategy();

        var chunks = await sut.ChunkAsync(Section("Some page text.", page: 2), new ChunkingOptions(), ct)
            .ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 2, 2);
    }

    [Fact]
    public async Task TokenAware_PaginatedSection_StampsBothKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new TokenAwareChunkingStrategy();

        var chunks = await sut.ChunkAsync(Section("Token aware text.", page: 5), new ChunkingOptions(), ct)
            .ToListAsync(ct);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => AssertPages(c, 5, 5));
    }

    [Fact]
    public async Task Code_PaginatedSection_StampsBothKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new CodeChunkingStrategy(new CodeChunkingOptions());

        var chunks = await sut.ChunkAsync(Section("def foo():\n    pass", page: 4), new ChunkingOptions(), ct)
            .ToListAsync(ct);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => AssertPages(c, 4, 4));
    }

    // ── HierarchicalMerger: min/max across the merged run ─────────────────────

    [Fact]
    public async Task HierarchicalMerger_ChunkSpanningPages_ReportsMinAndMax()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 1 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Section("Chapter A", page: 3, heading: "Chapter A", level: 1),
                Section("Body on page 3", page: 3),
                Section("Body continuing on page 4", page: 4),
                Section("Chapter B", page: 5, heading: "Chapter B", level: 1),
                Section("Body on page 5", page: 5)),
            new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        AssertPages(chunks[0], 3, 4);
        AssertPages(chunks[1], 5, 5);
    }

    [Fact]
    public async Task HierarchicalMerger_MixedPaginatedAndUnpaginatedRun_UsesPagesPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 1 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Section("Heading", page: 3, heading: "Heading", level: 1),
                Section("Unpaginated body"),
                Section("Body on page 4", page: 4)),
            new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 3, 4);
    }

    [Fact]
    public async Task HierarchicalMerger_AllUnpaginated_WritesNeitherKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 1 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Section("Heading", heading: "Heading", level: 1),
                Section("Body")),
            new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertNoPages(chunk);
    }

    [Fact]
    public async Task HierarchicalMerger_PerSectionFallback_StampsBothKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkAsync(Section("A lone section.", page: 7), new ChunkingOptions(), ct)
            .ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 7, 7);
    }

    // ── Semantic: per-section, merged-document, and refinement paths ──────────

    [Fact]
    public async Task Semantic_PerSection_StampsBothKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SemanticChunkingStrategy(MockEmbedder([1f, 0f]), new SemanticChunkingOptions());

        // Single sentence takes the short-circuit path — no embedding round-trip involved.
        var chunks = await sut.ChunkAsync(Section("One lonely sentence", page: 7), new ChunkingOptions(), ct)
            .ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 7, 7);
    }

    [Fact]
    public async Task Semantic_DocumentMode_MergedSectionsReportMinAndMax()
    {
        var ct = TestContext.Current.CancellationToken;
        // Identical embeddings → similarity 1.0 → no breakpoint → one merged chunk.
        var sut = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 1 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Section("Alpha text here.", page: 3),
                Section("Beta text there.", page: 4)),
            new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 3, 4);
    }

    [Fact]
    public async Task Semantic_Refinement_SubChunksKeepParentPageRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [-1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 1, MaxChunkSize = 30 });

        var oversized = new TextChunk
        {
            Text = "First sentence about apples. Second sentence about airplanes.",
            DocumentId = DocId,
            ChunkIndex = 0,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                [ReservedMetadataKeys.Page] = 3,
                [ReservedMetadataKeys.PageEnd] = 4,
            },
        };

        var refined = await sut.RefineAsync(new[] { oversized }.ToAsyncEnumerable(), ct).ToListAsync(ct);

        Assert.True(refined.Count > 1);
        Assert.All(refined, c => AssertPages(c, 3, 4));
    }

    // ── Proposition: passages carry the pages of the sections they overlap ────

    [Fact]
    public async Task Proposition_FallbackPassage_SpansContributingSectionPages()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new InvalidOperationException("LLM unavailable"));
        var sut = new PropositionChunkingStrategy(chat, new PropositionChunkingOptions());

        // Both sections fit one passage (default MaxPassageTokens = 1000), so the fallback
        // passage chunk spans pages 2–3.
        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Section("Facts from the second page.", page: 2),
                Section("Facts from the third page.", page: 3)),
            new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 2, 3);
    }
}
