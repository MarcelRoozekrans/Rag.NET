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

    /// <summary>The same patterns, compiled, used to split text the parser left whole (#636).</summary>
    private readonly System.Text.RegularExpressions.Regex[] _splitPatterns;

    public LegalChunkingStrategy(LegalChunkingOptions options)
    {
        // HierarchicalMergerOptions.HeadingPatterns is string[][] (one string[] per level),
        // so wrap each per-level pattern string into a single-element array.
        var headingPatterns = options.HeadingPatterns
            .Select(p => new[] { p })
            .ToArray();

        _splitPatterns = [.. options.HeadingPatterns.Select(pattern =>
            new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(1)))];

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
        // Split first: the merger classifies sections and never cuts one, so a parser that
        // yielded the whole document as a single section left this template inert (#636).
        var structured = HeadingTextSplitter.SplitAsync(sections, _splitPatterns, cancellationToken);

        await foreach (var chunk in _inner.ChunkDocumentAsync(structured, chunkingOptions, cancellationToken).ConfigureAwait(false))
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
