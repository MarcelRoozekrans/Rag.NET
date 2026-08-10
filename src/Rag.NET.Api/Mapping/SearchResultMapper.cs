using Rag.NET.Api.Contracts;
using Rag.NET.Models;

namespace Rag.NET.Api.Mapping;

internal static class SearchResultMapper
{
    internal static SearchResultDto ToDto(SearchResult r) =>
        new(r.Chunk.Text, r.Chunk.DocumentId, r.Chunk.ChunkIndex, r.Score,
            ToStringMetadata(r.Chunk.Metadata));

    /// <summary>
    /// The REST wire format still carries metadata as strings (ToString is lossless as text but
    /// drops the kind); a typed DTO is follow-up work tracked with the typed-metadata change.
    /// </summary>
    private static Dictionary<string, string> ToStringMetadata(IDictionary<string, MetadataValue> metadata)
    {
        var result = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
            result[key] = value.ToString();
        return result;
    }
}
