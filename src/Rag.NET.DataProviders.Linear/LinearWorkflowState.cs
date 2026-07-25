using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>An issue's workflow state: display name plus its state-type category.</summary>
public sealed class LinearWorkflowState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>State-type category; see <see cref="LinearOptions.ValidStateTypes"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}
