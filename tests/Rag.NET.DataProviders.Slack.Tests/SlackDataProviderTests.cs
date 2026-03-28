using System.Text.Json;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Slack;
using Xunit;

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

        Assert.Single(results);
        Assert.Equal("general-2024-04-01.md", results[0].FileName);
        Assert.Equal("1711929700.000000", results[0].ETag);
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
}

// ---------------------------------------------------------------------------
// Fake ISlackApi implementation for tests
// ---------------------------------------------------------------------------

file sealed class FakeSlackApi(
    List<SlackChannel> channels,
    List<SlackMessage> messages,
    string? realName = null) : ISlackApi
{
    public string? LastOldest { get; private set; }

    public Task<SlackChannelList> ListChannelsAsync(
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SlackChannelList
        {
            Ok       = true,
            Channels = channels,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });
    }

    public Task<SlackMessageList> GetHistoryAsync(
        string channel,
        int limit = 200,
        string? oldest = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        LastOldest = oldest;
        return Task.FromResult(new SlackMessageList
        {
            Ok       = true,
            Messages = messages,
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });
    }

    public Task<SlackMessageList> GetRepliesAsync(
        string channel,
        string ts,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });
    }

    public Task<SlackUserInfo> GetUserAsync(
        string user,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SlackUserInfo
        {
            Ok   = true,
            User = realName is not null ? new SlackUser { RealName = realName } : null
        });
    }
}
