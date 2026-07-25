using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>The <c>data</c> member of the issues query response.</summary>
public sealed class LinearIssuesData
{
    [JsonPropertyName("issues")]
    public LinearIssueConnection Issues { get; init; } = new();
}
