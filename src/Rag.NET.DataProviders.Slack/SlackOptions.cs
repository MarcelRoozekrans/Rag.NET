using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Slack;

/// <summary>Configuration for <see cref="SlackDataProvider"/>.</summary>
public sealed class SlackOptions : CloudStorageOptions
{
    /// <summary>
    /// Optional Slack channel ID. When <see langword="null"/>, all channels the bot has
    /// joined are enumerated.
    /// </summary>
    public string? ChannelId    { get; set; }

    /// <summary>
    /// Maximum number of messages to request per <c>conversations.history</c> call.
    /// Defaults to 200.
    /// </summary>
    public int     MessageLimit { get; set; } = 200;
}
