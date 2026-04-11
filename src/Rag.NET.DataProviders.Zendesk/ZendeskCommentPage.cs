using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Response from the Zendesk ticket comments endpoint.</summary>
public sealed class ZendeskCommentPage
{
    [JsonPropertyName("comments")]
    public IReadOnlyList<ZendeskComment> Comments { get; set; } = [];
}
