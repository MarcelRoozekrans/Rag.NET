using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Parsers.Vision;

public sealed class ImageChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
#pragma warning disable CS1998 // async method lacks await — intentional: sync-to-async-enumerable conversion
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return MakeChunk(section, index++);
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return MakeChunk(section, 0);
    }
#pragma warning restore CS1998

    private static TextChunk MakeChunk(DocumentSection section, int index)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["template"] = "image",
            ["source_type"] = "image",
            ["part"] = section.Heading ?? "image_description",
        };
        PageMetadata.Write(metadata, section.PageNumber, section.PageNumber);

        return new TextChunk
        {
            Text = section.Text,
            DocumentId = section.DocumentId,
            ChunkIndex = index,
            Metadata = metadata,
        };
    }
}
