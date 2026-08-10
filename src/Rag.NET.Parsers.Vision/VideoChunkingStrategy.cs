using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Rag.NET.Parsers.Vision;

public sealed class VideoChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
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

    // The reserved page/page_end metadata stays absent deliberately: VideoDocumentParser
    // overloads DocumentSection.PageNumber as a scene timestamp in seconds (surfaced below as
    // timestamp_seconds), so writing it under a key documented as a 1-based page would present
    // "page 42" for a 42-second scene.
    private static TextChunk MakeChunk(DocumentSection section, int index) =>
        new()
        {
            Text = section.Text,
            DocumentId = section.DocumentId,
            ChunkIndex = index,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["template"] = "video",
                ["source_type"] = "video",
                ["part"] = section.Heading ?? "video_scene",
                ["timestamp_seconds"] = (section.PageNumber ?? 0)
                    .ToString(CultureInfo.InvariantCulture),
            },
        };
}
