using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraUser(
    [property: JsonPropertyName("displayName")] string DisplayName);
