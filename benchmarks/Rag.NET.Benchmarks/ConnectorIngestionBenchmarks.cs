using System.Globalization;
using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Graph;
using MimeKit;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Asana;
using Rag.NET.DataProviders.Confluence;
using Rag.NET.DataProviders.Gmail;
using Rag.NET.DataProviders.Jira;
using Rag.NET.DataProviders.MicrosoftTeams;
using Rag.NET.DataProviders.Notion;
using Rag.NET.DataProviders.Slack;
using Refit;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures <see cref="IFileContentProvider.GetFilesAsync"/> enumeration throughput
/// across all 7 SaaS connectors with mocked HTTP/IMAP backends (no network I/O).
/// Each connector returns ~20 items from a single page of canned JSON.
/// </summary>
[MemoryDiagnoser]
public class ConnectorIngestionBenchmarks
{
    [Params("Confluence", "Jira", "Notion", "Asana", "Slack", "Teams", "Gmail")]
    public string Connector { get; set; } = default!;

    private IFileContentProvider _provider = default!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = Connector switch
        {
            "Confluence" => CreateConfluenceProvider(),
            "Jira"       => CreateJiraProvider(),
            "Notion"     => CreateNotionProvider(),
            "Asana"      => CreateAsanaProvider(),
            "Slack"      => CreateSlackProvider(),
            "Teams"      => CreateTeamsProvider(),
            "Gmail"      => CreateGmailProvider(),
            _            => throw new NotSupportedException($"Unknown connector: {Connector}")
        };
    }

    [Benchmark]
    public async Task<int> EnumerateFiles()
    {
        int count = 0;
        await foreach (var file in _provider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    // -----------------------------------------------------------------------
    // Factory methods
    // -----------------------------------------------------------------------

    internal static ConfluenceDataProvider CreateConfluenceProvider()
    {
        var json = BuildConfluenceJson(20);
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["/wiki/rest/api/content"] = json });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bench.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        return new ConfluenceDataProvider(api, new ConfluenceOptions
        {
            BaseUrl = "https://bench.atlassian.net",
            Email   = "bench@test.com"
        });
    }

    internal static JiraDataProvider CreateJiraProvider()
    {
        var json = BuildJiraJson(20);
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["/rest/api/3/search"] = json });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bench.atlassian.net") };
        var api = RestService.For<IJiraApi>(http);
        return new JiraDataProvider(api, new JiraOptions
        {
            BaseUrl = "https://bench.atlassian.net",
            Email   = "bench@test.com"
        });
    }

    internal static NotionDataProvider CreateNotionProvider()
    {
        var searchJson = BuildNotionSearchJson(20);
        const string emptyBlocks = """{ "results": [], "has_more": false }""";
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson
        };
        // Add empty blocks for each page ID
        for (int i = 0; i < 20; i++)
            responses[$"page-{i:D3}"] = emptyBlocks;

        var handler = new BenchFakeHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = RestService.For<INotionApi>(http);
        return new NotionDataProvider(api, new NotionOptions());
    }

    internal static AsanaDataProvider CreateAsanaProvider()
    {
        var tasksJson = BuildAsanaTasksJson(20);
        const string emptySubtasks = """{ "data": [] }""";
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"] = tasksJson,
            ["/subtasks"]      = emptySubtasks
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        return new AsanaDataProvider(http, new StaticTokenProvider("bench-token"),
            new AsanaOptions { WorkspaceGid = "ws-bench" });
    }

    internal static SlackDataProvider CreateSlackProvider()
    {
        var api = new BenchSlackApi(
            channels: [new SlackChannel("C001", "general")],
            messages: BuildSlackMessages(20),
            realName: "BenchUser");
        return new SlackDataProvider(api, new SlackOptions());
    }

    internal static MicrosoftTeamsDataProvider CreateTeamsProvider()
    {
        var messagesJson = BuildTeamsMessagesJson(20);
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/me/joinedTeams"]                  = """{ "value": [{ "id": "team-1", "displayName": "Bench" }] }""",
            ["/team-1/channels"]                 = """{ "value": [{ "id": "chan-1", "displayName": "general" }] }""",
            ["/team-1/channels/chan-1/messages"]  = messagesJson,
        };
        var handler = new BenchFakeHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new MicrosoftTeamsDataProvider(new GraphServiceClient(httpClient), new MicrosoftTeamsOptions());
    }

    internal static GmailDataProvider CreateGmailProvider()
    {
        var uids = Enumerable.Range(1, 20).Select(i => new UniqueId((uint)i)).ToList();
        var msg = new MimeMessage();
        msg.Subject = "Bench Subject";
        msg.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        msg.To.Add(new MailboxAddress("Receiver", "receiver@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        msg.Body = new TextPart("plain") { Text = "Benchmark message body content for throughput testing." };

        var client = Substitute.For<IImapClient>();
        var inbox  = Substitute.For<IMailFolder>();
        client.Inbox.Returns(inbox);
        client.AuthenticationMechanisms.Returns(new HashSet<string>(StringComparer.Ordinal));
        inbox.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<UniqueId>>(uids));
        inbox.GetMessageAsync(Arg.Any<UniqueId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(msg));

        return new GmailDataProvider(new StaticTokenProvider("bench-token"),
            new GmailOptions(), clientFactory: () => client);
    }

    // -----------------------------------------------------------------------
    // JSON builders
    // -----------------------------------------------------------------------

    internal static string BuildConfluenceJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "results": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "{{i}}", "title": "Page {{i}}", "body": { "storage": { "value": "<p>Content of page {{i}}</p>" } }, "version": { "number": {{i + 1}} } }
                """);
        }
        sb.Append("""], "_links": {} }""");
        return sb.ToString();
    }

    internal static string BuildJiraJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append($$"""{ "issues": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "{{10000 + i}}", "key": "BENCH-{{i + 1}}", "fields": { "summary": "Issue {{i}}", "description": "Description for issue {{i}}", "status": { "name": "Open" }, "priority": { "name": "Medium" }, "assignee": { "displayName": "Dev {{i}}" }, "comment": { "comments": [] }, "updated": "2026-03-01T10:00:00Z" } }
                """);
        }
        sb.Append(CultureInfo.InvariantCulture, $$"""], "total": {{count}} }""");
        return sb.ToString();
    }

    internal static string BuildNotionSearchJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "results": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "page-{{i:D3}}", "last_edited_time": "2026-03-01T10:00:00.000Z", "properties": { "title": { "title": [{ "plain_text": "Notion Page {{i}}" }] } } }
                """);
        }
        sb.Append("""], "has_more": false }""");
        return sb.ToString();
    }

    internal static string BuildAsanaTasksJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "data": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "gid": "task-{{i:D3}}", "name": "Task {{i}}", "notes": "Notes for task {{i}}", "due_on": null, "completed": false, "assignee": null, "modified_at": "2026-03-01T10:00:00Z" }
                """);
        }
        sb.Append("] }");
        return sb.ToString();
    }

    internal static List<SlackMessage> BuildSlackMessages(int count)
    {
        var msgs = new List<SlackMessage>(count);
        // All messages on the same day: 1711929600 = 2024-04-01 00:00 UTC
        for (int i = 0; i < count; i++)
        {
            msgs.Add(new SlackMessage
            {
                Ts   = string.Create(CultureInfo.InvariantCulture, $"{1711929600 + i * 60}.000000"),
                User = "U001",
                Text = string.Create(CultureInfo.InvariantCulture, $"Benchmark message {i}")
            });
        }
        return msgs;
    }

    internal static string BuildTeamsMessagesJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "value": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "msg-{{i}}", "createdDateTime": "2026-03-01T{{10 + (i / 60):D2}}:{{i % 60:D2}}:00Z", "lastModifiedDateTime": "2026-03-01T{{10 + (i / 60):D2}}:{{i % 60:D2}}:00Z", "from": { "user": { "displayName": "User {{i}}" } }, "body": { "content": "Team message {{i}}", "contentType": "text" } }
                """);
        }
        sb.Append("] }");
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Shared fake infrastructure for benchmarks
// ---------------------------------------------------------------------------

/// <summary>
/// Returns canned JSON responses keyed by URL substring.
/// Picks the longest matching key so more-specific routes win.
/// </summary>
internal sealed class BenchFakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        string? bestKey = null;
        int bestLen = -1;
        foreach (var k in responses.Keys)
        {
            if (url.Contains(k, StringComparison.Ordinal) && k.Length > bestLen)
            {
                bestKey = k;
                bestLen = k.Length;
            }
        }
        if (bestKey is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[bestKey], Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>Fake ISlackApi for benchmarks. Returns one channel with N messages.</summary>
internal sealed class BenchSlackApi(
    List<SlackChannel> channels,
    List<SlackMessage> messages,
    string? realName = null) : ISlackApi
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
        => Task.FromResult(new SlackMessageList
        {
            Ok       = true,
            Messages = [],
            ResponseMetadata = new SlackCursor { NextCursor = string.Empty }
        });

    public Task<SlackUserInfo> GetUserAsync(
        string user, CancellationToken cancellationToken = default)
        => Task.FromResult(new SlackUserInfo
        {
            Ok   = true,
            User = realName is not null ? new SlackUser { RealName = realName } : null
        });
}
