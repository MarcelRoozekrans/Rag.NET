using Rag.NET.Api.Contracts;
using Rag.NET.Models;

namespace Rag.NET.Api.Mapping;

internal static class SearchResultMapper
{
    internal static SearchResultDto ToDto(SearchResult r) =>
        new(r.Chunk.Text, r.Chunk.DocumentId, r.Chunk.ChunkIndex, r.Score,
            new Dictionary<string, string>(r.Chunk.Metadata, StringComparer.Ordinal));
}
