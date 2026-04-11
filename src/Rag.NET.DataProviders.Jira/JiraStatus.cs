using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraStatus(
    [property: JsonPropertyName("name")] string Name);
