using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Academic-paper chunking: abstract and references handling over
/// <see cref="HierarchicalMergerChunkingStrategy"/>, with section metadata on every chunk.
/// </summary>
/// <remarks>
/// Delegates to <see cref="HierarchicalMergerChunkingStrategy"/>, which deliberately ignores
/// <see cref="ChunkingOptions"/> — a chunk is one heading subtree, unbounded above, and
/// <see cref="ChunkingOptions.MaxChunkSize"/>/<see cref="ChunkingOptions.Overlap"/> have no
/// effect here. See that strategy's remarks for the reasoning and for how to bound chunk size.
/// </remarks>
public sealed class AcademicPaperChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private static readonly string[] AbstractHeadings = ["abstract"];
    private static readonly string[] ReferencesHeadings = ["references", "bibliography", "works cited"];

    private readonly HierarchicalMergerChunkingStrategy _inner;
    private readonly AcademicPaperChunkingOptions _options;

    public AcademicPaperChunkingStrategy(AcademicPaperChunkingOptions options)
    {
        _options = options;
        _inner = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());
    }

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var allSections = new List<DocumentSection>();
        await foreach (var s in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
            allSections.Add(s);

        var abstractIndex = allSections.FindIndex(s =>
            s.Heading is { } h &&
            AbstractHeadings.Any(a => h.Trim().Equals(a, StringComparison.OrdinalIgnoreCase)));

        var startIndex = abstractIndex >= 0 ? abstractIndex : 0;

        if (abstractIndex >= 0 && _options.IncludeAbstract)
        {
            var abstractSection = allSections[abstractIndex];
            var abstractMetadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["template"] = "academic_paper",
                ["section_type"] = "abstract",
            };
            PageMetadata.Write(abstractMetadata, abstractSection.PageNumber, abstractSection.PageNumber);

            yield return new TextChunk
            {
                Text = abstractSection.Text,
                DocumentId = abstractSection.DocumentId,
                ChunkIndex = 0,
                Metadata = abstractMetadata,
            };
            startIndex = abstractIndex + 1;
        }

        var bodySections = allSections
            .Skip(startIndex)
            .Where(s => _options.IncludeReferences ||
                        s.Heading is null ||
                        !ReferencesHeadings.Any(r =>
                            s.Heading.Trim().Equals(r, StringComparison.OrdinalIgnoreCase)));

        var chunkIndex = _options.IncludeAbstract && abstractIndex >= 0 ? 1 : 0;
        await foreach (var chunk in _inner.ChunkDocumentAsync(ToAsync(bodySections), chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk with
            {
                ChunkIndex = chunkIndex++,
                Metadata = new Dictionary<string, MetadataValue>(chunk.Metadata, StringComparer.Ordinal)
                {
                    ["template"] = "academic_paper",
                    ["section_type"] = "body",
                },
            };
        }
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk with
            {
                Metadata = new Dictionary<string, MetadataValue>(chunk.Metadata, StringComparer.Ordinal)
                {
                    ["template"] = "academic_paper",
                    ["section_type"] = "body",
                },
            };
        }
    }

#pragma warning disable CS1998 // async method lacks await — intentional: converts sync enumerable to async stream
    private static async IAsyncEnumerable<DocumentSection> ToAsync(IEnumerable<DocumentSection> source)
    {
        foreach (var item in source)
            yield return item;
    }
#pragma warning restore CS1998
}
