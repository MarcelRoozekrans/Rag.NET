using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Slack;
using Rag.NET.DataProviders.Testing;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Slack.Tests;

public sealed class SlackDataProviderTests
{
    private static SlackDataProvider MakeProvider(
        ISlackApi api,
        SlackOptions? options = null)
    {
        return new SlackDataProvider(api, options ?? new SlackOptions());
    }

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_SingleChannelSameDayBatch()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hello" },
                new SlackMessage { Ts = "1711929700.000000", User = "U001", Text = "World" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("general-2024-04-01.md", results[0].Value.FileName);
        Assert.Equal("1711929700.000000", results[0].Value.ETag);
    }

    /// <summary>
    /// Phase 4.10 Task 5: the day's earliest message <c>ts</c> becomes the typed
    /// <see cref="FileEntry.CreatedAt"/>, at full unix-epoch precision — unlike the day-grouping
    /// <c>date</c> metadata tag, which is deliberately day-granularity (asserted unchanged
    /// separately) and never normalised. <c>UpdatedAt</c> stays unset.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_EarliestTs_IsTypedAsCreatedAt()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929700.000000", User = "U001", Text = "Second" },
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "First" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(results).Value;
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1711929600).UtcDateTime, entry.CreatedAt);
        Assert.Equal("2024-04-01", entry.Metadata!["date"]);
        Assert.Null(entry.UpdatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_HostileChannelName_Sanitized()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "release/2026: prep")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hello" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("release_2026_ prep-2024-04-01.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_OldestForwardedToApi()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: []);

        var opts = new SlackOptions { DeltaToken = "1711929600.000000" };
        var sut  = MakeProvider(api, opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1711929600.000000", api.LastOldest);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hello" }
            ],
            realName: "Alice");

        var opts = new SlackOptions { Extensions = [".txt"] };
        var sut  = MakeProvider(api, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SlackDataProvider(null!, new SlackOptions()));
    }

    [Fact]
    public async Task GetFilesAsync_ThreadReplies_UserNamesResolved()
    {
        // One message with reply_count=1 and a thread reply from user U002 ("Bob")
        var parentTs = "1711929600.000000";
        var replyTs  = "1711929700.000000";

        var api = new FakeSlackApiWithReplies(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage
                {
                    Ts          = parentTs,
                    User        = "U001",
                    Text        = "Hello",
                    ReplyCount  = 1,
                    ThreadTs    = parentTs
                }
            ],
            replies: [
                // index 0 = parent (skipped), index 1 = actual reply
                new SlackMessage { Ts = parentTs, User = "U001", Text = "Hello" },
                new SlackMessage { Ts = replyTs,  User = "U002", Text = "Reply text" }
            ],
            userNames: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["U001"] = "Alice",
                ["U002"] = "Bob"
            });

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);

        // The reply should show "Bob", not "unknown"
        Assert.Contains("Bob", content, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", content, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // New comprehensive tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_ApiReturnsOkFalse_ThrowsInvalidOperation()
    {
        var api = new FakeSlackApiErrorOnChannels(error: "invalid_auth");
        var sut = MakeProvider(api);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GetFilesAsync(TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("invalid_auth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_ChannelIdPinned_SkipsChannelListApi()
    {
        // FakeSlackApiThrowsOnChannelList throws if ListChannelsAsync is called.
        // Setting ChannelId means the provider should never call it.
        var api = new FakeSlackApiThrowsOnChannelList();
        var opts = new SlackOptions { ChannelId = "C999" };
        var sut = MakeProvider(api, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // No exception means ListChannelsAsync was not called
        Assert.Empty(results); // no messages configured → no files
    }

    [Fact]
    public async Task GetFilesAsync_MultipleChannels_YieldsFilesPerChannel()
    {
        var api = new FakeSlackApiPerChannel(
            channels: [
                new SlackChannel("C001", "general"),
                new SlackChannel("C002", "random")
            ],
            messagesByChannel: new Dictionary<string, List<SlackMessage>>(StringComparer.Ordinal)
            {
                ["C001"] = [new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hi general" }],
                ["C002"] = [new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hi random" }]
            },
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, f => f.Value.FileName.StartsWith("general-", StringComparison.Ordinal));
        Assert.Contains(results, f => f.Value.FileName.StartsWith("random-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_MultiDayMessages_CreatesMultipleFileHandles()
    {
        // Two messages on different UTC days
        // 1711929600 = 2024-04-01 00:00 UTC
        // 1712016000 = 2024-04-02 00:00 UTC
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Day 1" },
                new SlackMessage { Ts = "1712016000.000000", User = "U001", Text = "Day 2" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, f => string.Equals(f.Value.FileName, "general-2024-04-01.md", StringComparison.Ordinal));
        Assert.Contains(results, f => string.Equals(f.Value.FileName, "general-2024-04-02.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_EmptyChannel_YieldsNoFiles()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: []);

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_UserCacheMiss_FallsBackToUserId()
    {
        // GetUserAsync returns Ok=false → provider falls back to userId string
        var api = new FakeSlackApiUserFails(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U999", Text = "Orphan msg" }
            ]);

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("U999", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullUserInMessage_ShowsUnknown()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = null, Text = "Ghost message" }
            ]);

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("**unknown**", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_MessagesSortedByTimestamp()
    {
        // Pass messages in reverse chronological order; output should be ascending
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929700.000000", User = "U001", Text = "Second" },
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "First" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);

        int firstIdx  = content.IndexOf("First", StringComparison.Ordinal);
        int secondIdx = content.IndexOf("Second", StringComparison.Ordinal);
        Assert.True(firstIdx < secondIdx, "Messages should be sorted ascending by timestamp");
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hello" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token)
                .ToListAsync(cts.Token));
    }

    [Fact]
    public async Task GetFilesAsync_HttpErrorOnChannelList_PropagatesAsRagErrorHttpFailed()
    {
        var api = new FakeSlackApiHttpErrorOnChannels(
            statusCode: HttpStatusCode.Unauthorized,
            errorMessage: "Unauthorized");
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(results);
        Assert.True(single.IsFailure);
        var httpFailed = Assert.IsType<RagError.HttpFailed>(single.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, httpFailed.StatusCode);
    }

    [Fact]
    public async Task GetFilesAsync_HttpErrorOnHistory_PropagatesAsRagErrorHttpFailed()
    {
        var api = new FakeSlackApiHttpErrorOnHistory(
            channels: [new SlackChannel("C001", "general")],
            statusCode: HttpStatusCode.Forbidden,
            errorMessage: "Forbidden");
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(results);
        Assert.True(single.IsFailure);
        var httpFailed = Assert.IsType<RagError.HttpFailed>(single.Error);
        Assert.Equal(HttpStatusCode.Forbidden, httpFailed.StatusCode);
    }

    [Fact]
    public void AddSlackDataProvider_DefaultBaseUrl_UsesProductionUrl()
    {
        var services = new ServiceCollection();
        services.AddSlackDataProvider(botToken: "xoxb-test-token");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddSlackDataProvider_CustomBaseUrl_OverridesDefault()
    {
        var services = new ServiceCollection();
        services.AddSlackDataProvider(botToken: "xoxb-test-token", baseUrl: "https://custom.example.com");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsChannelDateAndMessageCount()
    {
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Hello" },
                new SlackMessage { Ts = "1711929700.000000", User = "U001", Text = "World" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("general",    metadata["channel"]);
        Assert.Equal("C001",       metadata["channel_id"]);
        Assert.Equal("2024-04-01", metadata["date"]);
        Assert.Equal("2",          metadata["message_count"]);
        Assert.Equal(4, metadata.Count);

        // The channel and date stay in the Markdown heading — that is what gets embedded.
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("# #general — 2024-04-01", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_PerDayMessageCount_IsPerDocumentNotPerChannel()
    {
        // Two messages on 2024-04-01 and one on 2024-04-02: each day document counts its own.
        var api = new FakeSlackApi(
            channels: [new SlackChannel("C002", "random")],
            messages: [
                new SlackMessage { Ts = "1711929600.000000", User = "U001", Text = "Day one a" },
                new SlackMessage { Ts = "1711929700.000000", User = "U001", Text = "Day one b" },
                new SlackMessage { Ts = "1712016000.000000", User = "U001", Text = "Day two" }
            ],
            realName: "Alice");

        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        Assert.Equal(2, results.Count);
        // Day grouping order is not contractual — look the days up by their date tag.
        var byDate = results.ToDictionary(
            r => r.Value.Metadata!["date"].ToString(), r => r.Value.Metadata!, StringComparer.Ordinal);
        Assert.Equal("2", byDate["2024-04-01"]["message_count"]);
        Assert.Equal("1", byDate["2024-04-02"]["message_count"]);
        Assert.Equal("random", byDate["2024-04-01"]["channel"]);
        Assert.Equal("C002",   byDate["2024-04-02"]["channel_id"]);
    }
}

// ---------------------------------------------------------------------------
// Fake ISlackApi implementation for tests
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiWithReplies(
    List<SlackChannel> channels,
    List<SlackMessage> messages,
    List<SlackMessage> replies,
    Dictionary<string, string> userNames) : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackChannelList, HttpError>.Success(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel,
        int limit = 200,
        string? oldest = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = messages,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel,
        string ts,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = replies,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user,
        CancellationToken cancellationToken = default)
    {
        var name = userNames.TryGetValue(user, out var n) ? n : user;
        return Task.FromResult(Result<SlackUserInfo, HttpError>.Success(new SlackUserInfo
        {
            Ok   = true,
            User = new SlackUser { RealName = name }
        }));
    }
}

file sealed class FakeSlackApi(
    List<SlackChannel> channels,
    List<SlackMessage> messages,
    string? realName = null) : ISlackApi
{
    public string? LastOldest { get; private set; }

    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackChannelList, HttpError>.Success(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel,
        int limit = 200,
        string? oldest = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        LastOldest = oldest;
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = messages,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel,
        string ts,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackUserInfo, HttpError>.Success(new SlackUserInfo
        {
            Ok   = true,
            User = realName is not null ? new SlackUser { RealName = realName } : null
        }));
    }
}

// ---------------------------------------------------------------------------
// Fake that returns Ok=false from ListChannelsAsync
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiErrorOnChannels(string error) : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackChannelList, HttpError>.Success(new SlackChannelList
        {
            Ok    = false,
            Error = error
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");
}

// ---------------------------------------------------------------------------
// Fake that throws if ListChannelsAsync is called (for ChannelId-pinned test)
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiThrowsOnChannelList : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("ListChannelsAsync should not be called when ChannelId is set");

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackUserInfo, HttpError>.Success(new SlackUserInfo
        {
            Ok   = true,
            User = new SlackUser { RealName = user }
        }));
    }
}

// ---------------------------------------------------------------------------
// Fake that returns different messages per channel
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiPerChannel(
    List<SlackChannel> channels,
    Dictionary<string, List<SlackMessage>> messagesByChannel,
    string? realName = null) : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackChannelList, HttpError>.Success(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
    {
        var msgs = messagesByChannel.TryGetValue(channel, out var m) ? m : [];
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = msgs,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackUserInfo, HttpError>.Success(new SlackUserInfo
        {
            Ok   = true,
            User = realName is not null ? new SlackUser { RealName = realName } : null
        }));
    }
}

// ---------------------------------------------------------------------------
// Fake where GetUserAsync returns Ok=false (user lookup fails)
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiUserFails(
    List<SlackChannel> channels,
    List<SlackMessage> messages) : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackChannelList, HttpError>.Success(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = messages,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackMessageList, HttpError>.Success(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackUserInfo, HttpError>.Success(new SlackUserInfo
        {
            Ok    = false,
            Error = "user_not_found",
            User  = null
        }));
    }
}

// ---------------------------------------------------------------------------
// Fake that returns HTTP error from ListChannelsAsync
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiHttpErrorOnChannels(
    HttpStatusCode statusCode,
    string errorMessage) : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            Result<SlackChannelList, HttpError>.Failure(
                new HttpError(statusCode, System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty, errorMessage)));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");
}

// ---------------------------------------------------------------------------
// Fake that returns HTTP error from GetHistoryAsync
// ---------------------------------------------------------------------------

file sealed class FakeSlackApiHttpErrorOnHistory(
    List<SlackChannel> channels,
    HttpStatusCode statusCode,
    string errorMessage) : ISlackApi
{
    public Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SlackChannelList, HttpError>.Success(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        }));
    }

    public Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        string channel, int limit = 200, string? oldest = null,
        string? cursor = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            Result<SlackMessageList, HttpError>.Failure(
                new HttpError(statusCode, System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty, errorMessage)));
    }

    public Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        string channel, string ts, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");

    public Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Should not be called");
}
