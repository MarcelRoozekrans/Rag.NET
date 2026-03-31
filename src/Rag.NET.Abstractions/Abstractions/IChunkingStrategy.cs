using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IChunkingStrategy
{
    IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
