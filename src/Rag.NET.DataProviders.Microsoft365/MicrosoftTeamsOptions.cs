using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

/// <summary>Configuration for <see cref="MicrosoftTeamsDataProvider"/>.</summary>
public sealed class MicrosoftTeamsOptions : CloudStorageOptions
{
    /// <summary>
    /// Optional Team ID. When <see langword="null"/>, all teams the authenticated user
    /// has joined are enumerated.
    /// </summary>
    public string? TeamId    { get; set; }

    /// <summary>
    /// Optional Channel ID within the team. When <see langword="null"/>, all channels
    /// in each team are enumerated.
    /// </summary>
    public string? ChannelId { get; set; }
}
