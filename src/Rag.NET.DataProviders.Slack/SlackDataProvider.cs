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
        var channels = await GetChannelsAsync(cancellationToken).ConfigureAwait(false);

        for (int ci = 0; ci < channels.Count; ci++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[ci];

            var messages = await FetchMessagesAsync(channel.Id, cancellationToken)
                .ConfigureAwait(false);

            // Group messages by UTC calendar day
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
                    list       = [];
                    byDay[date] = list;
                }
                list.Add(msg);
            }

            foreach (var kvp in byDay)
            {
                var dateStr = kvp.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var dayMsgs = kvp.Value;

                // ETag = latest ts in the batch
                string latestTs = dayMsgs[0].Ts;
                for (int k = 1; k < dayMsgs.Count; k++)
                    if (string.CompareOrdinal(dayMsgs[k].Ts, latestTs) > 0)
                        latestTs = dayMsgs[k].Ts;

                var markdown = await BuildDayMarkdownAsync(
                    channel.Id, channel.Name, dateStr, dayMsgs, cancellationToken)
                    .ConfigureAwait(false);

                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id:               $"{channel.Id}/{dateStr}",
                    FileName:         $"{channel.Name}-{dateStr}.md",
                    ETag:             latestTs,
                    OpenContentAsync: _ => Task.FromResult<Stream>(
                        new MemoryStream(Encoding.UTF8.GetBytes(markdown)))));
            }
        }
    }

    private async Task<List<SlackChannel>> GetChannelsAsync(CancellationToken ct)
    {
        if (_options.ChannelId is not null)
            return [new SlackChannel(_options.ChannelId, _options.ChannelId)];

        var channels = new List<SlackChannel>();
        string? cursor = null;
        do
        {
            var result = await _api.ListChannelsAsync(cursor: cursor, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!result.Ok)
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Slack API error: {result.Error}"));
            channels.AddRange(result.Channels);
            cursor = string.IsNullOrEmpty(result.ResponseMetadata?.NextCursor)
                ? null : result.ResponseMetadata.NextCursor;
        }
        while (cursor is not null);
        return channels;
    }

    private async Task<List<SlackMessage>> FetchMessagesAsync(string channelId, CancellationToken ct)
    {
        var messages = new List<SlackMessage>();
        string? cursor = null;
        var oldest = _options.DeltaToken;

        do
        {
            var result = await _api.GetHistoryAsync(
                channelId, _options.MessageLimit, oldest, cursor, ct)
                .ConfigureAwait(false);
            if (!result.Ok)
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Slack API error: {result.Error}"));
            messages.AddRange(result.Messages);
            cursor = string.IsNullOrEmpty(result.ResponseMetadata?.NextCursor)
                ? null : result.ResponseMetadata.NextCursor;
        }
        while (cursor is not null);

        return messages;
    }

    private async Task<string> BuildDayMarkdownAsync(
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
                var replies = await _api.GetRepliesAsync(channelId, msg.ThreadTs, ct)
                    .ConfigureAwait(false);
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

        return sb.ToString().TrimEnd();
    }

    private async Task<string> GetUserNameAsync(string userId, CancellationToken ct)
    {
        if (_userCache.TryGetValue(userId, out var name)) return name;
        var info = await _api.GetUserAsync(userId, ct).ConfigureAwait(false);
        // ok: false for user lookups is treated as a soft-fail; fall back to userId rather
        // than throwing — an unresolvable display name should not abort document processing.
        name = info.User?.RealName ?? userId;
        _userCache[userId] = name;
        return name;
    }
}
