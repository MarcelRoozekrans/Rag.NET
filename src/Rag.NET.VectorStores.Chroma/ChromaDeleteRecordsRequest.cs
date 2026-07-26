using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

public sealed class ChromaDeleteRecordsRequest
{
    /// <summary>Filter selecting the records to delete, e.g. <c>{document_id: {"$eq": id}}</c>.</summary>
    [JsonPropertyName("where")]
    public required IDictionary<string, object?> Where { get; init; }
}
