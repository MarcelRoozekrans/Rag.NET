using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackOptions : CloudStorageOptions
{
    public string? ChannelId    { get; init; }  // null = all joined channels
    public int     MessageLimit { get; init; } = 200;
}
