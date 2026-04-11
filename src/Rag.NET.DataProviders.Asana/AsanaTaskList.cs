using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaTaskList
{
    [JsonPropertyName("data")]
    public IReadOnlyList<AsanaTask> Data { get; init; } = [];

    [JsonPropertyName("next_page")]
    public AsanaNextPage? NextPage { get; init; }
}
