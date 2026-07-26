using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>Record-delete response, e.g. <c>{"deleted": 2}</c> (verified against Chroma 1.0.0).</summary>
public sealed class ChromaDeleteRecordsResponse
{
    [JsonPropertyName("deleted")]
    public long? Deleted { get; init; }
}
