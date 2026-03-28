using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraPriority(
    [property: JsonPropertyName("name")] string Name);
