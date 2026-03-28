using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraStatus(
    [property: JsonPropertyName("name")] string Name);
