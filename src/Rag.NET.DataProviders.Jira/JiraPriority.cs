using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraPriority(
    [property: JsonPropertyName("name")] string Name);
