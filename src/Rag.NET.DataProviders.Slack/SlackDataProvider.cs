using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Slack;

/// <summary>
/// Enumerates Slack channel messages as Markdown documents grouped by UTC calendar day.
/// <para>
/// Thread replies are expanded inline beneath their parent message. User display names
/// are resolved via the <c>users.info</c> API and cached for the lifetime of the provider.
/// When the Slack API returns <c>ok: false</c>, an <see cref="InvalidOperationException"/>
/// is thrown.
/// </para>
/// <para>
/// Delta support uses <see cref="SlackOptions.DeltaToken"/> as the <c>oldest</c> timestamp
/// parameter in the <c>conversations.history</c> call.
/// </para>
/// <para>
/// Phase 4.10 Task 5: each day's earliest message <c>ts</c> becomes
/// <see cref="FileHandle.CreatedAt"/> — full second-plus-microsecond precision, unlike the
/// day-grouping's <c>date</c> metadata tag (see <see cref="BuildMetadata"/>), which is
/// deliberately left at day granularity. <c>UpdatedAt</c> stays unset: a day's document has no
/// single "last modified" instant distinct from its latest message, and that value is already
/// exposed as the ETag.
/// </para>
/// </summary>
public sealed class SlackDataProvider : FileContentProviderBase
{
    private readonly ISlackApi    _api;
    private readonly SlackOptions _options;
    private readonly Dictionary<string, string> _userCache = new(StringComparer.Ordinal);

    internal SlackDataProvider(ISlackApi api, SlackOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (channels, channelError) = await GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        if (channelError is not null)
        {
            yield return Result<FileHandle, RagError>.Failure(channelError);
            yield break;
        }

        for (int ci = 0; ci < channels!.Count; ci++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[ci];

            var (messages, messageError) = await FetchMessagesAsync(channel.Id, cancellationToken)
                .ConfigureAwait(false);
            if (messageError is not null)
            {
                yield return Result<FileHandle, RagError>.Failure(messageError);
                yield break;
            }

            await foreach (var handle in EmitDayHandlesAsync(
                channel, messages!, cancellationToken).ConfigureAwait(false))
            {
                yield return handle;
                if (handle.IsFailure) yield break;
            }
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> EmitDayHandlesAsync(
        SlackChannel channel,
        IList<SlackMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var byDay = GroupByDay(messages);

        foreach (var kvp in byDay)
        {
            var dateStr = kvp.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dayMsgs = kvp.Value;

            string earliestTs = dayMsgs[0].Ts;
            string latestTs   = dayMsgs[0].Ts;
            for (int k = 1; k < dayMsgs.Count; k++)
            {
                if (string.CompareOrdinal(dayMsgs[k].Ts, latestTs) > 0)
                    latestTs = dayMsgs[k].Ts;
                if (string.CompareOrdinal(dayMsgs[k].Ts, earliestTs) < 0)
                    earliestTs = dayMsgs[k].Ts;
            }

            var (markdown, markdownError) = await BuildDayMarkdownAsync(
                channel.Id, channel.Name, dateStr, dayMsgs, cancellationToken)
                .ConfigureAwait(false);

            if (markdownError is not null)
            {
                yield return Result<FileHandle, RagError>.Failure(markdownError);
                yield break;
            }

            var md = markdown!;
            yield return Result<FileHandle, RagError>.Success(new FileHandle(
                Id:               $"{channel.Id}/{dateStr}",
                // Only the channel name is user-controlled; the date suffix is generated.
                FileName:         $"{FileNameSanitizer.Sanitize(
                    channel.Name, $"channel-{channel.Id}")}-{dateStr}.md",
                ETag:             latestTs,
                OpenContentAsync: _ => Task.FromResult<Stream>(
                    new MemoryStream(Encoding.UTF8.GetBytes(md))),
                Metadata:         BuildMetadata(channel, dateStr, dayMsgs.Count),
                CreatedAt:        ParseSlackTimestamp(earliestTs)));
        }
    }

    /// <summary>
    /// Converts a Slack <c>ts</c> value — seconds since the Unix epoch, with microsecond
    /// precision as a decimal string, e.g. <c>"1700000000.123456"</c> — to UTC. Unlike the
    /// ISO-8601 timestamps other connectors carry, this cannot go through
    /// <see cref="ConnectorTimestampParser"/>: it is a decimal Unix timestamp, not a datetime
    /// string. Mirrors the conversion <see cref="GroupByDay"/> and
    /// <see cref="BuildDayMarkdownAsync"/> already perform for day-bucketing and display.
    /// </summary>
    private static DateTime ParseSlackTimestamp(string ts)
        => DateTimeOffset.FromUnixTimeMilliseconds(
            (long)(double.Parse(ts, CultureInfo.InvariantCulture) * 1000)).UtcDateTime;

    /// <summary>
    /// The day document's filterable fields. Built synchronously — a dictionary assembled inside
    /// the async iterator above would trip HLQ012.
    /// <para>
    /// Channel and date are <i>also</i> rendered into the Markdown heading: the body drives
    /// semantic recall, the tags drive filtering.
    /// </para>
    /// </summary>
    private static Dictionary<string, MetadataValue> BuildMetadata(
        SlackChannel channel, string date, int messageCount)
    {
        // message_count is always present, so the dictionary is never empty and never null.
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["message_count"] = messageCount.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(channel.Name)) metadata["channel"]    = channel.Name;
        if (!string.IsNullOrEmpty(channel.Id))   metadata["channel_id"] = channel.Id;
        if (!string.IsNullOrEmpty(date))         metadata["date"]       = date;
        return metadata;
    }

    private static Dictionary<DateTime, List<SlackMessage>> GroupByDay(IList<SlackMessage> messages)
    {
        var byDay = new Dictionary<DateTime, List<SlackMessage>>();
        for (int mi = 0; mi < messages.Count; mi++)
        {
            var msg  = messages[mi];
            var date = DateTimeOffset
                .FromUnixTimeMilliseconds(
                    (long)(double.Parse(msg.Ts, CultureInfo.InvariantCulture) * 1000))
                .UtcDateTime.Date;
            if (!byDay.TryGetValue(date, out var list))
            {
                list        = [];
                byDay[date] = list;
            }
            list.Add(msg);
        }
        return byDay;
    }

    private async Task<(List<SlackChannel>? Channels, RagError? Error)> GetChannelsAsync(
        CancellationToken ct)
    {
        if (_options.ChannelId is not null)
            return ([new SlackChannel(_options.ChannelId, _options.ChannelId)], null);

        var channels = new List<SlackChannel>();
        string? cursor = null;
        do
        {
            var result = await _api.ListChannelsAsync(cursor: cursor, cancellationToken: ct)
                .ConfigureAwait(false);
            if (result.IsFailure)
                return (null, new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message ?? string.Empty));
            var data = result.Value;
            if (!data.Ok)
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Slack API error: {data.Error}"));
            channels.AddRange(data.Channels);
            cursor = string.IsNullOrEmpty(data.ResponseMetadata?.NextCursor)
                ? null : data.ResponseMetadata.NextCursor;
        }
        while (cursor is not null);
        return (channels, null);
    }

    private async Task<(List<SlackMessage>? Messages, RagError? Error)> FetchMessagesAsync(
        string channelId, CancellationToken ct)
    {
        var messages = new List<SlackMessage>();
        string? cursor = null;
        var oldest = _options.DeltaToken;

        do
        {
            var result = await _api.GetHistoryAsync(
                channelId, _options.MessageLimit, oldest, cursor, ct)
                .ConfigureAwait(false);
            if (result.IsFailure)
                return (null, new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message ?? string.Empty));
            var data = result.Value;
            if (!data.Ok)
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Slack API error: {data.Error}"));
            messages.AddRange(data.Messages);
            cursor = string.IsNullOrEmpty(data.ResponseMetadata?.NextCursor)
                ? null : data.ResponseMetadata.NextCursor;
        }
        while (cursor is not null);

        return (messages, null);
    }

    private async Task<(string? Markdown, RagError? Error)> BuildDayMarkdownAsync(
        string channelId, string channelName, string date,
        List<SlackMessage> messages, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# #{channelName} — {date}");
        sb.AppendLine();

        // Sort by ts ascending
        messages.Sort((a, b) => string.CompareOrdinal(a.Ts, b.Ts));

        for (int i = 0; i < messages.Count; i++)
        {
            var msg  = messages[i];
            var time = DateTimeOffset
                .FromUnixTimeMilliseconds(
                    (long)(double.Parse(msg.Ts, CultureInfo.InvariantCulture) * 1000))
                .ToString("HH:mm", CultureInfo.InvariantCulture);

            var userName = msg.User is not null
                ? await GetUserNameAsync(msg.User, ct).ConfigureAwait(false)
                : "unknown";

            sb.AppendLine($"**{userName}** ({time}): {msg.Text}");

            if (msg.ReplyCount is > 0 && msg.ThreadTs is not null)
            {
                var repliesResult = await _api.GetRepliesAsync(channelId, msg.ThreadTs, ct)
                    .ConfigureAwait(false);
                if (repliesResult.IsFailure)
                    return (null, new RagError.HttpFailed(repliesResult.Error.StatusCode, repliesResult.Error.Message ?? string.Empty));

                var replies = repliesResult.Value;
                if (!replies.Ok)
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"Slack API error: {replies.Error}"));

                for (int ri = 1; ri < replies.Messages.Count; ri++)  // skip parent (index 0)
                {
                    var reply     = replies.Messages[ri];
                    var replyTime = DateTimeOffset
                        .FromUnixTimeMilliseconds(
                            (long)(double.Parse(reply.Ts, CultureInfo.InvariantCulture) * 1000))
                        .ToString("HH:mm", CultureInfo.InvariantCulture);
                    var replyUserName = reply.User is not null
                        ? await GetUserNameAsync(reply.User, ct).ConfigureAwait(false)
                        : "unknown";
                    sb.AppendLine($"> **{replyUserName}** ({replyTime}): {reply.Text}");
                }
            }
        }

        return (sb.ToString().TrimEnd(), null);
    }

    private async Task<string> GetUserNameAsync(string userId, CancellationToken ct)
    {
        if (_userCache.TryGetValue(userId, out var name)) return name;
        var result = await _api.GetUserAsync(userId, ct).ConfigureAwait(false);
        // HTTP failures and ok: false for user lookups are treated as a soft-fail;
        // fall back to userId rather than throwing — an unresolvable display name
        // should not abort document processing.
        if (result.IsFailure)
        {
            _userCache[userId] = userId;
            return userId;
        }
        name = result.Value.User?.RealName ?? userId;
        _userCache[userId] = name;
        return name;
    }
}
