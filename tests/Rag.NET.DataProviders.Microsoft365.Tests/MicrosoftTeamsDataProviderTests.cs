using Microsoft.Graph;
using Rag.NET.DataProviders.MicrosoftTeams;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.MicrosoftTeams.Tests;

public sealed class MicrosoftTeamsDataProviderTests
{
    // -------------------------------------------------------------------------
    // Stub JSON payloads
    // -------------------------------------------------------------------------

    private const string JoinedTeamsJson = """
        {
          "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#teams",
          "value": [{ "id": "team-1", "displayName": "Engineering" }]
        }
        """;

    private const string ChannelsJson = """
        {
          "value": [{ "id": "chan-1", "displayName": "general" }]
        }
        """;

    private const string MessagesJson = """
        {
          "value": [{
            "id": "msg-1",
            "createdDateTime": "2026-03-01T10:00:00Z",
            "lastModifiedDateTime": "2026-03-01T10:00:00Z",
            "from": { "user": { "displayName": "Alice" } },
            "body": { "content": "Hello team", "contentType": "text" }
          }]
        }
        """;

    // URL substrings matched by the fake handler (Graph SDK sends /v1.0/... paths).
    // Longer keys take precedence over shorter ones (most-specific wins).
    private const string JoinedTeamsKey  = "/me/joinedTeams";
    private const string ChannelsKey     = "/team-1/channels";
    private const string MessagesKey     = "/team-1/channels/chan-1/messages";

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsOneEntry()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = ChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions();
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("general-2026-03-01.md", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_TeamIdAndChannelIdPinned_YieldsOneEntry()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("chan-1-2026-03-01.md", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_HostileChannelName_Sanitized()
    {
        // Teams channel names routinely contain '/' and ':'.
        const string hostileChannelsJson = """
            {
              "value": [{ "id": "chan-1", "displayName": "Design/Review: Q1" }]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = hostileChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var sut   = new MicrosoftTeamsDataProvider(graph, new MicrosoftTeamsOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("Design_Review_ Q1-2026-03-01.md", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = ChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { Extensions = [".txt"] };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MicrosoftTeamsDataProvider(null!, new MicrosoftTeamsOptions()));
    }

    [Fact]
    public async Task GetFilesAsync_OnlyTeamIdPinned_FetchesAllChannels()
    {
        // TeamId set but ChannelId null → channels endpoint is called
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChannelsKey] = ChannelsJson,
            [MessagesKey] = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("general-2026-03-01.md", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_NullBodyContent_MessageSkipped()
    {
        const string messagesWithNullBody = """
            {
              "value": [{
                "id": "msg-1",
                "createdDateTime": "2026-03-01T10:00:00Z",
                "from": { "user": { "displayName": "Alice" } },
                "body": { "contentType": "text" }
              }]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = messagesWithNullBody,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetFilesAsync_HtmlInBody_TagsStripped()
    {
        const string messagesWithHtml = """
            {
              "value": [{
                "id": "msg-1",
                "createdDateTime": "2026-03-01T10:00:00Z",
                "from": { "user": { "displayName": "Alice" } },
                "body": { "content": "<b>bold</b> text", "contentType": "html" }
              }]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = messagesWithHtml,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        var content = await ReadContentAsync(entries[0].Value);
        Assert.Contains("bold text", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("</b>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullAuthor_ShowsUnknown()
    {
        const string messagesWithNullAuthor = """
            {
              "value": [{
                "id": "msg-1",
                "createdDateTime": "2026-03-01T10:00:00Z",
                "from": { "user": { } },
                "body": { "content": "hello", "contentType": "text" }
              }]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = messagesWithNullAuthor,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        var content = await ReadContentAsync(entries[0].Value);
        Assert.Contains("**unknown**", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_MultiDayMessages_CreatesMultipleFiles()
    {
        const string multiDayMessages = """
            {
              "value": [
                {
                  "id": "msg-1",
                  "createdDateTime": "2026-03-01T10:00:00Z",
                  "from": { "user": { "displayName": "Alice" } },
                  "body": { "content": "Day one", "contentType": "text" }
                },
                {
                  "id": "msg-2",
                  "createdDateTime": "2026-03-02T14:00:00Z",
                  "from": { "user": { "displayName": "Bob" } },
                  "body": { "content": "Day two", "contentType": "text" }
                }
              ]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = multiDayMessages,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        var fileNames = entries.Select(e => e.Value.FileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Contains("chan-1-2026-03-01.md", fileNames, StringComparer.Ordinal);
        Assert.Contains("chan-1-2026-03-02.md", fileNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyMessageList_YieldsNothing()
    {
        const string emptyMessages = """
            {
              "value": []
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = emptyMessages,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetFilesAsync_MessagesSortedByCreatedDateTime()
    {
        // Messages in reverse chronological order → output should be ascending
        const string reversedMessages = """
            {
              "value": [
                {
                  "id": "msg-2",
                  "createdDateTime": "2026-03-01T15:00:00Z",
                  "from": { "user": { "displayName": "Bob" } },
                  "body": { "content": "Later message", "contentType": "text" }
                },
                {
                  "id": "msg-1",
                  "createdDateTime": "2026-03-01T09:00:00Z",
                  "from": { "user": { "displayName": "Alice" } },
                  "body": { "content": "Earlier message", "contentType": "text" }
                }
              ]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = reversedMessages,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        var content = await ReadContentAsync(entries[0].Value);
        var alicePos = content.IndexOf("Alice", StringComparison.Ordinal);
        var bobPos   = content.IndexOf("Bob", StringComparison.Ordinal);
        Assert.True(alicePos < bobPos, "Alice (09:00) should appear before Bob (15:00)");
    }

    [Fact]
    public async Task GetFilesAsync_NullCreatedDateTime_UsesCurrentDate()
    {
        const string messageNoDate = """
            {
              "value": [{
                "id": "msg-1",
                "from": { "user": { "displayName": "Alice" } },
                "body": { "content": "No date", "contentType": "text" }
              }]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = messageNoDate,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        var todayStr = DateTime.UtcNow.Date.ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(todayStr, entries[0].Value.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = ChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions();
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Caller cancellation must not be reclassified as a transport failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    // -------------------------------------------------------------------------
    // Transport failures (no HTTP response at all) → RagError.TransportFailed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_TransportFailure_YieldsTransportFailedResult()
    {
        // This connector had no exception handling at all: the HttpRequestException from the
        // joinedTeams call escaped GetFilesAsync entirely.
        var graph = MakeThrowingGraphClient(
            () => new HttpRequestException("No such host is known (graph.microsoft.com:443)."));
        var sut   = new MicrosoftTeamsDataProvider(graph, new MicrosoftTeamsOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<Rag.NET.Models.RagError.TransportFailed>(result.Error);
        _ = Assert.IsType<HttpRequestException>(failure.Inner);
    }

    [Fact]
    public async Task GetFilesAsync_TransportFailureOnMessages_YieldsTransportFailedResult()
    {
        // Pinning the team and channel skips the first two Graph calls, so the failure lands
        // on the message fetch — a separate helper with its own paging loop.
        var graph = MakeThrowingGraphClient(() => new HttpRequestException("connection reset by peer"));
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        _ = Assert.IsType<Rag.NET.Models.RagError.TransportFailed>(result.Error);
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsTeamChannelDateAndMessageCount()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = ChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var sut   = new MicrosoftTeamsDataProvider(graph, new MicrosoftTeamsOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(entries.Select(e => e.Value));

        var metadata = Assert.Single(entries).Value.Metadata!;
        Assert.Equal("team-1",     metadata["team_id"]);
        Assert.Equal("chan-1",     metadata["channel_id"]);
        Assert.Equal("general",    metadata["channel"]);
        Assert.Equal("2026-03-01", metadata["date"]);
        Assert.Equal("1",          metadata["message_count"]);
        Assert.Equal(5, metadata.Count);

        // The channel and date stay in the Markdown heading — that is what gets embedded.
        var content = await ReadContentAsync(entries[0].Value);
        Assert.Contains("# general — 2026-03-01", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_MultipleMessagesInADay_MessageCountReflectsThem()
    {
        const string twoMessagesJson = """
            {
              "value": [
                {
                  "id": "msg-1",
                  "createdDateTime": "2026-03-01T10:00:00Z",
                  "lastModifiedDateTime": "2026-03-01T10:00:00Z",
                  "from": { "user": { "displayName": "Alice" } },
                  "body": { "content": "First", "contentType": "text" }
                },
                {
                  "id": "msg-2",
                  "createdDateTime": "2026-03-01T11:00:00Z",
                  "lastModifiedDateTime": "2026-03-01T11:00:00Z",
                  "from": { "user": { "displayName": "Bob" } },
                  "body": { "content": "Second", "contentType": "text" }
                }
              ]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = twoMessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(entries.Select(e => e.Value));

        var metadata = Assert.Single(entries).Value.Metadata!;
        Assert.Equal("2", metadata["message_count"]);
        // With both ids pinned, the channel display name falls back to the id.
        Assert.Equal("chan-1", metadata["channel"]);
    }

    /// <summary>
    /// Phase 4.10 Task 5: the day's earliest <c>createdDateTime</c> becomes the typed
    /// <see cref="Rag.NET.DataProviders.FileEntry.CreatedAt"/> and the latest
    /// <c>lastModifiedDateTime</c> becomes <see cref="Rag.NET.DataProviders.FileEntry.UpdatedAt"/>
    /// — full precision, unlike the day-grouping <c>date</c> metadata tag (asserted unchanged
    /// separately), which stays day-granularity.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_EarliestCreatedAndLatestModified_BecomeTypedTimestamps()
    {
        const string twoMessagesJson = """
            {
              "value": [
                {
                  "id": "msg-1",
                  "createdDateTime": "2026-03-01T11:00:00Z",
                  "lastModifiedDateTime": "2026-03-01T11:30:00Z",
                  "from": { "user": { "displayName": "Alice" } },
                  "body": { "content": "First", "contentType": "text" }
                },
                {
                  "id": "msg-2",
                  "createdDateTime": "2026-03-01T09:00:00Z",
                  "lastModifiedDateTime": "2026-03-01T09:15:00Z",
                  "from": { "user": { "displayName": "Bob" } },
                  "body": { "content": "Second", "contentType": "text" }
                }
              ]
            }
            """;
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = twoMessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        // Earliest of the two createdDateTime values (09:00, not 11:00).
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), entry.CreatedAt);
        // Latest of the two lastModifiedDateTime values (11:30, not 09:15).
        Assert.Equal(new DateTime(2026, 3, 1, 11, 30, 0, DateTimeKind.Utc), entry.UpdatedAt);
        Assert.Equal("2026-03-01", entry.Metadata!["date"]);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GraphServiceClient MakeGraphClient(Dictionary<string, string> responses)
        => MakeGraphClient(new FakeGraphHandler(responses));

    private static GraphServiceClient MakeThrowingGraphClient(Func<Exception> exceptionFactory)
        => MakeGraphClient(new ThrowingGraphHandler(exceptionFactory));

    private static GraphServiceClient MakeGraphClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new GraphServiceClient(httpClient);
    }

    private static async Task<string> ReadContentAsync(Rag.NET.DataProviders.FileEntry entry)
    {
        using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Intercepts outbound Graph SDK HTTP calls and returns canned JSON payloads.
/// Full-URL match is attempted first; then the longest matching substring key wins
/// (most specific key takes precedence over shorter, more general keys).
/// </summary>
file sealed class FakeGraphHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // Exact full-URL match first (e.g. delta tokens)
        if (responses.TryGetValue(url, out var exact))
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(exact, System.Text.Encoding.UTF8, "application/json"),
            });

        // Substring match — pick the longest matching key so more-specific routes win
        string? bestKey   = null;
        int     bestLen   = -1;
        foreach (var k in responses.Keys)
        {
            if (url.Contains(k, StringComparison.Ordinal) && k.Length > bestLen)
            {
                bestKey = k;
                bestLen = k.Length;
            }
        }

        if (bestKey is null)
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responses[bestKey], System.Text.Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// Fails every outbound Graph call before any HTTP response exists — the transport-failure
/// counterpart to <see cref="FakeGraphHandler"/>, which always produces a response.
/// A fresh exception instance is created per call so stack traces do not accumulate.
/// </summary>
file sealed class ThrowingGraphHandler(Func<Exception> exceptionFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => throw exceptionFactory();
}
