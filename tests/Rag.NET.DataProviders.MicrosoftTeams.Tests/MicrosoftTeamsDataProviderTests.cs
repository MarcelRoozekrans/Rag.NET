using Microsoft.Graph;
using Rag.NET.DataProviders.MicrosoftTeams;
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

        Assert.Single(entries);
        Assert.Equal("general-2026-03-01.md", entries[0].FileName);
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

        Assert.Single(entries);
        Assert.Equal("chan-1-2026-03-01.md", entries[0].FileName);
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

        Assert.Single(entries);
        Assert.Equal("general-2026-03-01.md", entries[0].FileName);
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

        Assert.Single(entries);
        var content = await ReadContentAsync(entries[0]);
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

        Assert.Single(entries);
        var content = await ReadContentAsync(entries[0]);
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
        var fileNames = entries.Select(e => e.FileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Contains("chan-1-2026-03-01.md", fileNames);
        Assert.Contains("chan-1-2026-03-02.md", fileNames);
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

        Assert.Single(entries);
        var content = await ReadContentAsync(entries[0]);
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

        Assert.Single(entries);
        var todayStr = DateTime.UtcNow.Date.ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(todayStr, entries[0].FileName, StringComparison.Ordinal);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GraphServiceClient MakeGraphClient(Dictionary<string, string> responses)
    {
        var handler    = new FakeGraphHandler(responses);
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
