using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Response from the Zendesk incremental tickets cursor endpoint.</summary>
public sealed class ZendeskIncrementalTicketResult
{
    [JsonPropertyName("tickets")]
    public IReadOnlyList<ZendeskTicket> Tickets { get; set; } = [];

    [JsonPropertyName("after_cursor")]
    public string? AfterCursor { get; set; }

    [JsonPropertyName("end_of_stream")]
    public bool EndOfStream { get; set; }

    [JsonPropertyName("end_time")]
    public long EndTime { get; set; }
}
