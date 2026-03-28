using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

internal sealed class AsanaTaskList
{
    [JsonPropertyName("data")]
    public List<AsanaTask> Data { get; init; } = [];

    [JsonPropertyName("next_page")]
    public AsanaNextPage? NextPage { get; init; }
}
