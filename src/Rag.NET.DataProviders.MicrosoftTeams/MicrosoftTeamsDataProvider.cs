using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.MicrosoftTeams;

/// <summary>
/// Enumerates Microsoft Teams channel messages as Markdown documents grouped by UTC
/// calendar day via the Microsoft Graph SDK.
/// <para>
/// HTML message bodies are stripped to plain text. Delta synchronisation is not yet
/// supported; every run performs a full traversal of all messages.
/// </para>
/// </summary>
public sealed partial class MicrosoftTeamsDataProvider : FileContentProviderBase
{
    private readonly GraphServiceClient    _graph;
    private readonly MicrosoftTeamsOptions _options;

    [GeneratedRegex("<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTagRegex();

    public MicrosoftTeamsDataProvider(GraphServiceClient graph, MicrosoftTeamsOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph   = graph;
        _options = options;
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var teams = await GetTeamsAsync(cancellationToken).ConfigureAwait(false);

        for (int ti = 0; ti < teams.Count; ti++)
        {
            var (teamId, _) = teams[ti];
            var channels = await GetChannelsAsync(teamId, cancellationToken).ConfigureAwait(false);

            for (int ci = 0; ci < channels.Count; ci++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (channelId, channelName) = channels[ci];

                var messages = await FetchMessagesAsync(teamId, channelId, cancellationToken)
                    .ConfigureAwait(false);

                var handles = GroupByDay(teamId, channelId, channelName, messages);
                for (int hi = 0; hi < handles.Count; hi++)
                    yield return Result<FileHandle, RagError>.Success(handles[hi]);
            }
        }
    }

    private async Task<List<(string Id, string Name)>> GetTeamsAsync(CancellationToken ct)
    {
        if (_options.TeamId is not null)
            return [(_options.TeamId, _options.TeamId)];

        var result = await _graph.Me.JoinedTeams.GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var teams = new List<(string, string)>();
        var values = result?.Value ?? [];
        for (int i = 0; i < values.Count; i++)
        {
            var t = values[i];
            if (!string.IsNullOrEmpty(t.Id))
                teams.Add((t.Id, t.DisplayName ?? t.Id));
        }
        return teams;
    }

    private async Task<List<(string Id, string Name)>> GetChannelsAsync(
        string teamId, CancellationToken ct)
    {
        if (_options.ChannelId is not null)
            return [(_options.ChannelId, _options.ChannelId)];

        var result = await _graph.Teams[teamId].Channels.GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var channels = new List<(string, string)>();
        var values = result?.Value ?? [];
        for (int i = 0; i < values.Count; i++)
        {
            var c = values[i];
            if (!string.IsNullOrEmpty(c.Id))
                channels.Add((c.Id, c.DisplayName ?? c.Id));
        }
        return channels;
    }

    private async Task<List<ChatMessage>> FetchMessagesAsync(
        string teamId, string channelId, CancellationToken ct)
    {
        var all  = new List<ChatMessage>();
        var page = await _graph.Teams[teamId].Channels[channelId].Messages
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);

        while (page is not null)
        {
            var values = page.Value ?? [];
            for (int i = 0; i < values.Count; i++)
                all.Add(values[i]);

            page = page.OdataNextLink is not null
                ? await _graph.Teams[teamId].Channels[channelId].Messages
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                : null;
        }
        return all;
    }

    private static List<FileHandle> GroupByDay(
        string teamId, string channelId, string channelName,
        List<ChatMessage> messages)
    {
        var byDay = new Dictionary<DateTime, List<ChatMessage>>();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Body?.Content is null) continue;
            var date = msg.CreatedDateTime?.UtcDateTime.Date ?? DateTime.UtcNow.Date;
            if (!byDay.TryGetValue(date, out var list))
            {
                list = [];
                byDay[date] = list;
            }
            list.Add(msg);
        }

        var handles = new List<FileHandle>(byDay.Count);
        foreach (var kvp in byDay)
        {
            var dateStr = kvp.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var dayMsgs = kvp.Value;

            // ETag = lastModifiedDateTime of latest message
            string? lastMod = null;
            for (int i = 0; i < dayMsgs.Count; i++)
            {
                var lm = dayMsgs[i].LastModifiedDateTime?.ToString("o",
                    System.Globalization.CultureInfo.InvariantCulture);
                if (lm is not null && (lastMod is null || string.CompareOrdinal(lm, lastMod) > 0))
                    lastMod = lm;
            }

            var markdown = BuildDayMarkdown(channelName, dateStr, dayMsgs);
            handles.Add(new FileHandle(
                Id:               $"{teamId}/{channelId}/{dateStr}",
                FileName:         $"{channelName}-{dateStr}.md",
                ETag:             lastMod,
                OpenContentAsync: _ => Task.FromResult<Stream>(
                    new MemoryStream(Encoding.UTF8.GetBytes(markdown)))));
        }
        return handles;
    }

    private static string BuildDayMarkdown(
        string channelName, string date, List<ChatMessage> messages)
    {
        // Sort by CreatedDateTime ascending
        messages.Sort((a, b) =>
            DateTimeOffset.Compare(
                a.CreatedDateTime ?? DateTimeOffset.MinValue,
                b.CreatedDateTime ?? DateTimeOffset.MinValue));

        var sb = new StringBuilder();
        sb.AppendLine($"# {channelName} — {date}");
        sb.AppendLine();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg    = messages[i];
            var time   = msg.CreatedDateTime?.ToString("HH:mm",
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var author = msg.From?.User?.DisplayName ?? "unknown";
            var body   = HtmlTagRegex().Replace(msg.Body?.Content ?? string.Empty, string.Empty).Trim();
            sb.AppendLine($"**{author}** ({time}): {body}");
        }
        return sb.ToString().TrimEnd();
    }
}
