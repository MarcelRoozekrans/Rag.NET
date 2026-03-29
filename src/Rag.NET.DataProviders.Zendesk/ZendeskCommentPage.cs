using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Response from the Zendesk ticket comments endpoint.</summary>
internal sealed class ZendeskCommentPage
{
    [JsonPropertyName("comments")]
    public List<ZendeskComment> Comments { get; set; } = [];
}
