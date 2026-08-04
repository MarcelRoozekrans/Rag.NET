using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Legal-document chunking: clause-pattern heading detection over
/// <see cref="HierarchicalMergerChunkingStrategy"/>, with clause metadata on every chunk.
/// </summary>
/// <remarks>
/// Delegates to <see cref="HierarchicalMergerChunkingStrategy"/>, which deliberately ignores
/// <see cref="ChunkingOptions"/> — a chunk is one heading subtree, unbounded above, and
/// <see cref="ChunkingOptions.MaxChunkSize"/>/<see cref="ChunkingOptions.Overlap"/> have no
/// effect here. See that strategy's remarks for the reasoning and for how to bound chunk size.
/// </remarks>
public sealed class LegalChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private readonly HierarchicalMergerChunkingStrategy _inner;

    public LegalChunkingStrategy(LegalChunkingOptions options)
    {
        // HierarchicalMergerOptions.HeadingPatterns is string[][] (one string[] per level),
        // so wrap each per-level pattern string into a single-element array.
        var headingPatterns = options.HeadingPatterns
            .Select(p => new[] { p })
            .ToArray();

        _inner = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions
        {
            MaxDepth = options.MaxDepth,
            HeadingPatterns = headingPatterns,
        });
    }

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ChunkDocumentAsync(sections, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            chunk.Metadata["template"] = "legal";
            chunk.Metadata["clause"] = chunk.Metadata.TryGetValue("heading", out var h) ? h : string.Empty;
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            chunk.Metadata["template"] = "legal";
            yield return chunk;
        }
    }
}
