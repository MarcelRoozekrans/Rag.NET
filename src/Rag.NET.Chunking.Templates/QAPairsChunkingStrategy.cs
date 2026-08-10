using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Pass-through strategy: emits one chunk per Q&amp;A section.
/// Reads the answer from <see cref="DocumentSection.Heading"/> — internal contract with <see cref="QAPairsDocumentParser"/>.
/// </summary>
public sealed class QAPairsChunkingStrategy : IDocumentChunkingStrategy
{
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["template"] = "qa_pairs",
                ["answer"] = section.Heading ?? string.Empty,
            };
            PageMetadata.Write(metadata, section.PageNumber, section.PageNumber);

            yield return new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = index++,
                Metadata = metadata,
            };
        }
    }
}
