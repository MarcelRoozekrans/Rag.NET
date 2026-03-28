using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

public sealed class MicrosoftTeamsOptions : CloudStorageOptions
{
    public string? TeamId    { get; init; }
    public string? ChannelId { get; init; }
}
