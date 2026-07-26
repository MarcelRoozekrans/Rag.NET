using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
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
/// <para>
/// Graph failures reach the caller through the <see cref="Result{TValue,TError}"/> channel
/// rather than as thrown exceptions: a response carrying a status becomes
/// <see cref="RagError.HttpFailed"/>, and a failure with no response at all — DNS, TLS, socket
/// reset, client-side timeout, token acquisition — becomes
/// <see cref="RagError.TransportFailed"/>. Caller cancellation always propagates.
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

    /// <summary>
    /// C# does not permit <c>yield</c> inside a <c>catch</c> clause, so every Graph call is
    /// made eagerly by a helper that returns a <see cref="Result{TValue,TError}"/>; this
    /// iterator only yields. The first failure ends the enumeration.
    /// </summary>
    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var teamsResult = await GetTeamsAsync(cancellationToken).ConfigureAwait(false);
        if (teamsResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(teamsResult.Error);
            yield break;
        }

        var teams = teamsResult.Value;
        for (int ti = 0; ti < teams.Count; ti++)
        {
            var (teamId, _) = teams[ti];
            var channelsResult = await GetChannelsAsync(teamId, cancellationToken)
                .ConfigureAwait(false);
            if (channelsResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(channelsResult.Error);
                yield break;
            }

            var channels = channelsResult.Value;
            for (int ci = 0; ci < channels.Count; ci++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (channelId, channelName) = channels[ci];

                var messagesResult = await FetchMessagesAsync(teamId, channelId, cancellationToken)
                    .ConfigureAwait(false);
                if (messagesResult.IsFailure)
                {
                    yield return Result<FileHandle, RagError>.Failure(messagesResult.Error);
                    yield break;
                }

                var handles = GroupByDay(teamId, channelId, channelName, messagesResult.Value);
                for (int hi = 0; hi < handles.Count; hi++)
                    yield return Result<FileHandle, RagError>.Success(handles[hi]);
            }
        }
    }

    private async Task<Result<List<(string Id, string Name)>, RagError>> GetTeamsAsync(
        CancellationToken ct)
    {
        if (_options.TeamId is not null)
            return Result<List<(string, string)>, RagError>.Success([(_options.TeamId, _options.TeamId)]);

        TeamCollectionResponse? result;
        try
        {
            result = await _graph.Me.JoinedTeams.GetAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMappable(ex, ct))
        {
            return Result<List<(string, string)>, RagError>.Failure(Map(ex));
        }

        var teams = new List<(string, string)>();
        var values = result?.Value ?? [];
        for (int i = 0; i < values.Count; i++)
        {
            var t = values[i];
            if (!string.IsNullOrEmpty(t.Id))
                teams.Add((t.Id, t.DisplayName ?? t.Id));
        }
        return Result<List<(string, string)>, RagError>.Success(teams);
    }

    private async Task<Result<List<(string Id, string Name)>, RagError>> GetChannelsAsync(
        string teamId, CancellationToken ct)
    {
        if (_options.ChannelId is not null)
            return Result<List<(string, string)>, RagError>.Success([(_options.ChannelId, _options.ChannelId)]);

        ChannelCollectionResponse? result;
        try
        {
            result = await _graph.Teams[teamId].Channels.GetAsync(cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMappable(ex, ct))
        {
            return Result<List<(string, string)>, RagError>.Failure(Map(ex));
        }

        var channels = new List<(string, string)>();
        var values = result?.Value ?? [];
        for (int i = 0; i < values.Count; i++)
        {
            var c = values[i];
            if (!string.IsNullOrEmpty(c.Id))
                channels.Add((c.Id, c.DisplayName ?? c.Id));
        }
        return Result<List<(string, string)>, RagError>.Success(channels);
    }

    private async Task<Result<List<ChatMessage>, RagError>> FetchMessagesAsync(
        string teamId, string channelId, CancellationToken ct)
    {
        var all      = new List<ChatMessage>();
        string? next = null;
        do
        {
            var pageResult = await FetchMessagePageAsync(teamId, channelId, next, ct)
                .ConfigureAwait(false);
            if (pageResult.IsFailure)
                return Result<List<ChatMessage>, RagError>.Failure(pageResult.Error);

            var page = pageResult.Value;
            if (page is null)
                break;

            var values = page.Value ?? [];
            for (int i = 0; i < values.Count; i++)
                all.Add(values[i]);

            next = page.OdataNextLink;
        } while (next is not null);

        return Result<List<ChatMessage>, RagError>.Success(all);
    }

    private async Task<Result<ChatMessageCollectionResponse?, RagError>> FetchMessagePageAsync(
        string teamId, string channelId, string? nextLink, CancellationToken ct)
    {
        var builder = _graph.Teams[teamId].Channels[channelId].Messages;
        try
        {
            var page = nextLink is not null
                ? await builder.WithUrl(nextLink).GetAsync(cancellationToken: ct).ConfigureAwait(false)
                : await builder.GetAsync(cancellationToken: ct).ConfigureAwait(false);
            return Result<ChatMessageCollectionResponse?, RagError>.Success(page);
        }
        catch (Exception ex) when (IsMappable(ex, ct))
        {
            return Result<ChatMessageCollectionResponse?, RagError>.Failure(Map(ex));
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is a Graph failure this provider converts into a
    /// <see cref="Result{TValue,TError}"/> failure rather than letting it escape.
    /// <para>
    /// The cancellation test comes first and wins: an HttpClient timeout surfaces as a
    /// <see cref="TaskCanceledException"/>, which derives from
    /// <see cref="OperationCanceledException"/>, so only the caller's token separates a
    /// timeout (a transport failure) from a caller cancellation (which must propagate).
    /// </para>
    /// </summary>
    private static bool IsMappable(Exception ex, CancellationToken cancellationToken)
        => !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        && ex is ApiException or HttpRequestException or TaskCanceledException
              or AuthenticationFailedException;

    /// <summary>
    /// Classifies a mappable Graph exception. Kiota leaves
    /// <see cref="ApiException.ResponseStatusCode"/> at <c>0</c> when no HTTP response was
    /// received at all, and <c>(HttpStatusCode)0</c> is not a valid status — that case is a
    /// transport failure, not an HTTP failure.
    /// </summary>
    private static RagError Map(Exception ex) => ex switch
    {
        ApiException { ResponseStatusCode: 0 } => new RagError.TransportFailed(ex),
        ODataError odata => new RagError.HttpFailed(
            (HttpStatusCode)odata.ResponseStatusCode, odata.Error?.Message ?? odata.Message),
        ApiException api => new RagError.HttpFailed(
            (HttpStatusCode)api.ResponseStatusCode, api.Message),
        _ => new RagError.TransportFailed(ex),
    };

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
                // Only the channel name is user-controlled; the date suffix is generated.
                // Teams channel names routinely contain '/' and ':'.
                FileName:         $"{FileNameSanitizer.Sanitize(
                    channelName, $"channel-{channelId}")}-{dateStr}.md",
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
