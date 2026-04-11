using System.Globalization;
using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Slack;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Slack-specific benchmarks: single-day batch, multi-day batch, and messages with thread replies.
/// </summary>
[MemoryDiagnoser]
public class SlackBenchmarks
{
    private SlackDataProvider _singleDayProvider = default!;
    private SlackDataProvider _multiDayProvider = default!;
    private SlackDataProvider _withRepliesProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Single day batch: 20 messages on the same day
        _singleDayProvider = ConnectorIngestionBenchmarks.CreateSlackProvider();

        // Multi-day batch: 20 messages across 5 days (4 per day)
        _multiDayProvider = MakeMultiDayProvider(20, 5);

        // With thread replies: 10 messages, 3 replies each
        _withRepliesProvider = MakeWithRepliesProvider(10, 3);
    }

    [Benchmark]
    public async Task<int> SingleDayBatch()
    {
        int count = 0;
        await foreach (var file in _singleDayProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> MultiDayBatch()
    {
        int count = 0;
        await foreach (var file in _multiDayProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> WithThreadReplies()
    {
        int count = 0;
        await foreach (var file in _withRepliesProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static SlackDataProvider MakeMultiDayProvider(int messageCount, int days)
    {
        var msgs = new List<SlackMessage>(messageCount);
        // Spread messages across days: 1711929600 = 2024-04-01 00:00 UTC, 86400s per day
        for (int i = 0; i < messageCount; i++)
        {
            int day = i % days;
            long ts = 1711929600 + (day * 86400) + (i * 60);
            msgs.Add(new SlackMessage
            {
                Ts   = string.Create(CultureInfo.InvariantCulture, $"{ts}.000000"),
                User = "U001",
                Text = string.Create(CultureInfo.InvariantCulture, $"Multi-day message {i}")
            });
        }

        var api = new BenchSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: msgs,
            realName: "BenchUser");
        return new SlackDataProvider(api, new SlackOptions());
    }

    private static SlackDataProvider MakeWithRepliesProvider(int messageCount, int repliesPerMessage)
    {
        var msgs = new List<SlackMessage>(messageCount);
        // All messages on the same day
        for (int i = 0; i < messageCount; i++)
        {
            var ts = string.Create(CultureInfo.InvariantCulture, $"{1711929600 + i * 120}.000000");
            msgs.Add(new SlackMessage
            {
                Ts         = ts,
                User       = "U001",
                Text       = string.Create(CultureInfo.InvariantCulture, $"Parent message {i}"),
                ThreadTs   = ts,
                ReplyCount = repliesPerMessage
            });
        }

        var api = new BenchSlackApiWithReplies(
            channels: [new SlackChannel("C001", "general")],
            messages: msgs,
            repliesPerMessage: repliesPerMessage,
            realName: "BenchUser");
        return new SlackDataProvider(api, new SlackOptions());
    }
}

/// <summary>Fake ISlackApi that returns thread replies for benchmarks.</summary>
internal sealed class BenchSlackApiWithReplies(
    List<SlackChannel> channels,
    List<SlackMessage> messages,
    int repliesPerMessage,
    string realName) : ISlackApi
{
    public Task<SlackChannelList> ListChannelsAsync(
        int limit = 200, string? cursor = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });

    public Task<SlackMessageList> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new SlackMessageList
        {
            Ok       = true,
            Messages = messages,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });

    public Task<SlackMessageList> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
    {
        var replies = new List<SlackMessage> { new() { Ts = ts, User = "U001", Text = "parent" } };
        for (int r = 0; r < repliesPerMessage; r++)
        {
            var replyTs = long.Parse(ts.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) + r + 1;
            replies.Add(new SlackMessage
            {
                Ts   = string.Create(CultureInfo.InvariantCulture, $"{replyTs}.000000"),
                User = "U002",
                Text = string.Create(CultureInfo.InvariantCulture, $"Reply {r}")
            });
        }
        return Task.FromResult(new SlackMessageList
        {
            Ok       = true,
            Messages = replies,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });
    }

    public Task<SlackUserInfo> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
        => Task.FromResult(new SlackUserInfo
        {
            Ok   = true,
            User = new SlackUser { RealName = realName }
        });
}
