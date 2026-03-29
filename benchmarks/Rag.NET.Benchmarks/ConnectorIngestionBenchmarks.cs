using System.Globalization;
using System.Net;
using System.Text;
using AirtableApiClient;
using BenchmarkDotNet.Attributes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKitSearchQuery = MailKit.Search.SearchQuery;
using Microsoft.Graph;
using MimeKit;
using NGitLab;
using NGitLab.Models;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Airtable;
using Rag.NET.DataProviders.Asana;
using Rag.NET.DataProviders.Bitbucket;
using Rag.NET.DataProviders.Confluence;
using Rag.NET.DataProviders.GitLab;
using Rag.NET.DataProviders.Gmail;
using Rag.NET.DataProviders.Jira;
using Rag.NET.DataProviders.MicrosoftTeams;
using Rag.NET.DataProviders.Notion;
using Rag.NET.DataProviders.Slack;
using Rag.NET.DataProviders.Zendesk;
using Refit;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures <see cref="IFileContentProvider.GetFilesAsync"/> enumeration throughput
/// across all 12 SaaS connectors with mocked HTTP/IMAP backends (no network I/O).
/// Each connector returns ~20 items from a single page of canned JSON.
/// </summary>
[MemoryDiagnoser]
public class ConnectorIngestionBenchmarks
{
    [Params("Confluence", "Jira", "Notion", "Asana", "Slack", "Teams", "Gmail",
            "GitLab", "Bitbucket", "ZendeskTickets", "ZendeskArticles", "Airtable")]
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
            "Gmail"           => CreateGmailProvider(),
            "GitLab"          => CreateGitLabProvider(),
            "Bitbucket"       => CreateBitbucketProvider(),
            "ZendeskTickets"  => CreateZendeskTicketsProvider(),
            "ZendeskArticles" => CreateZendeskArticlesProvider(),
            "Airtable"        => CreateAirtableProvider(),
            _                 => throw new NotSupportedException($"Unknown connector: {Connector}")
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
        inbox.SearchAsync(Arg.Any<MailKitSearchQuery>(), Arg.Any<CancellationToken>())
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

    // -----------------------------------------------------------------------
    // GitLab
    // -----------------------------------------------------------------------

    internal static GitLabDataProvider CreateGitLabProvider(int count = 20, bool delta = false)
    {
        var treeItems = new Tree[count];
        for (int i = 0; i < count; i++)
        {
            treeItems[i] = new Tree
            {
                Path = $"src/file-{i:D3}.cs",
                Name = $"file-{i:D3}.cs",
                Type = ObjectType.blob,
                Id = new Sha1($"abcdef{i:D34}"),
                Mode = "100644"
            };
        }

        var client = Substitute.For<IGitLabClient>();
        var repoClient = Substitute.For<IRepositoryClient>();
        var filesClient = Substitute.For<IFilesClient>();
        client.GetRepository(Arg.Any<ProjectId>()).Returns(repoClient);
        repoClient.Files.Returns(filesClient);

        if (!delta)
        {
            var collectionResponse = Substitute.For<GitLabCollectionResponse<Tree>>();
            collectionResponse.GetAsyncEnumerator(Arg.Any<CancellationToken>())
                .Returns(_ => new BenchArrayAsyncEnumerator<Tree>(treeItems));
            repoClient.GetTreeAsync(Arg.Any<RepositoryGetTreeOptions>()).Returns(collectionResponse);
        }
        else
        {
            var diffs = new Diff[count];
            for (int i = 0; i < count; i++)
            {
                diffs[i] = new Diff
                {
                    NewPath = $"src/file-{i:D3}.cs",
                    IsDeletedFile = false
                };
            }
            repoClient.CompareAsync(Arg.Any<CompareQuery>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new CompareResults { Diff = diffs }));
        }

        filesClient.GetRawAsync(
            Arg.Any<string>(),
            Arg.Any<Func<Stream, Task>>(),
            Arg.Any<GetRawFileRequest>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var options = delta
            ? new GitLabOptions { BaseUrl = "https://gitlab.bench", ProjectIdOrPath = "bench/repo", LastIngestedCommitSha = "abc123" }
            : new GitLabOptions { BaseUrl = "https://gitlab.bench", ProjectIdOrPath = "bench/repo" };
        return new GitLabDataProvider(client, options);
    }

    // -----------------------------------------------------------------------
    // Bitbucket
    // -----------------------------------------------------------------------

    internal static BitbucketDataProvider CreateBitbucketProvider(int count = 20, bool delta = false)
    {
        if (!delta)
        {
            var sourceJson = BuildBitbucketSourceJson(count);
            var rawContent = "// benchmark file content";
            var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/repositories/bench-ws/bench-repo/src/main/"] = sourceJson,
                ["/repositories/bench-ws/bench-repo/src/main/src/"] = rawContent,
            });
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.bitbucket.org/2.0") };
            var api = RestService.For<IBitbucketApi>(http);
            return new BitbucketDataProvider(api, new BitbucketOptions
            {
                Workspace = "bench-ws",
                RepoSlug  = "bench-repo",
            });
        }
        else
        {
            var diffstatJson = BuildBitbucketDiffstatJson(count);
            var rawContent = "// benchmark file content";
            var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/diffstat/"] = diffstatJson,
                ["/src/main/"]  = rawContent,
            });
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.bitbucket.org/2.0") };
            var api = RestService.For<IBitbucketApi>(http);
            return new BitbucketDataProvider(api, new BitbucketOptions
            {
                Workspace             = "bench-ws",
                RepoSlug              = "bench-repo",
                LastIngestedCommitHash = "abc123",
            });
        }
    }

    internal static string BuildBitbucketSourceJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "values": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "path": "src/file-{{i:D3}}.cs", "type": "commit_file", "size": 1024, "commit": { "hash": "deadbeef{{i:D32}}" } }
                """);
        }
        sb.Append("""], "next": null }""");
        return sb.ToString();
    }

    internal static string BuildBitbucketDiffstatJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "values": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "status": "modified", "new": { "path": "src/file-{{i:D3}}.cs" } }
                """);
        }
        sb.Append("""], "next": null }""");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Zendesk Tickets
    // -----------------------------------------------------------------------

    internal static ZendeskTicketsDataProvider CreateZendeskTicketsProvider(int count = 20, int commentsPerTicket = 2)
    {
        var ticketsJson = BuildZendeskTicketsJson(count);
        var commentsJson = BuildZendeskCommentsJson(commentsPerTicket);
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/incremental/tickets/cursor.json"] = ticketsJson,
            ["/comments"]                                = commentsJson,
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bench.zendesk.com") };
        var api = RestService.For<IZendeskApi>(http);
        return new ZendeskTicketsDataProvider(api, new ZendeskTicketsOptions
        {
            Subdomain = "bench",
            Email     = "bench@test.com",
        });
    }

    internal static string BuildZendeskTicketsJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "tickets": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": {{1000 + i}}, "subject": "Ticket {{i}}", "description": "Description for ticket {{i}}", "status": "open", "priority": "normal", "updated_at": "2026-03-01T10:00:00Z" }
                """);
        }
        sb.Append(CultureInfo.InvariantCulture, $$"""], "end_of_stream": true, "after_cursor": null, "end_time": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}} }""");
        return sb.ToString();
    }

    internal static string BuildZendeskCommentsJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "comments": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": {{5000 + i}}, "body": "Comment body {{i}}", "author_id": {{100 + i}}, "created_at": "2026-03-01T10:00:00Z" }
                """);
        }
        sb.Append("] }");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Zendesk Articles
    // -----------------------------------------------------------------------

    internal static ZendeskArticlesDataProvider CreateZendeskArticlesProvider(int count = 20, bool largeHtml = false)
    {
        var articlesJson = BuildZendeskArticlesJson(count, largeHtml);
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/help_center/incremental/articles.json"] = articlesJson,
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bench.zendesk.com") };
        var api = RestService.For<IZendeskApi>(http);
        return new ZendeskArticlesDataProvider(api, new ZendeskArticlesOptions
        {
            Subdomain = "bench",
            Email     = "bench@test.com",
        });
    }

    internal static string BuildZendeskArticlesJson(int count, bool largeHtml = false)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "articles": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var body = largeHtml
                ? BuildLargeHtmlBody(i, 10_000)
                : $"<p>Article body {i}</p>";
            var escapedBody = body.Replace("\"", "\\\"", StringComparison.Ordinal);
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": {{2000 + i}}, "title": "Article {{i}}", "body": "{{escapedBody}}", "updated_at": "2026-03-01T10:00:00Z", "section_id": 1 }
                """);
        }
        sb.Append(CultureInfo.InvariantCulture, $$"""], "end_time": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}, "count": {{count}} }""");
        return sb.ToString();
    }

    internal static string BuildLargeHtmlBody(int pageIndex, int targetSize)
    {
        var filler = new string('x', targetSize / 20);
        var htmlSb = new StringBuilder();
        for (int p = 0; p < 20; p++)
            htmlSb.Append(CultureInfo.InvariantCulture, $"<p>Paragraph {p} of article {pageIndex}: {filler}</p>");
        return htmlSb.ToString();
    }

    // -----------------------------------------------------------------------
    // Airtable
    // -----------------------------------------------------------------------

    internal static AirtableDataProvider CreateAirtableProvider(
        int count = 20, int attachmentsPerRecord = 0, bool withDeltaFilter = false)
    {
        var records = BuildAirtableRecords(count, attachmentsPerRecord);
        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AirtableListRecordsResponse(
                new AirtableRecordList { Records = records.ToArray() })));

        // HttpClient for attachment downloads (not actually called in no-attachment case)
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://dl.airtable.com/"] = "attachment-content"
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://dl.airtable.com") };

        var options = withDeltaFilter
            ? new AirtableOptions
            {
                BaseId = "appBENCH",
                TableName = "Tasks",
                LastModifiedFieldName = "Modified",
                DeltaToken = "2026-01-01T00:00:00.000Z",
            }
            : new AirtableOptions
            {
                BaseId    = "appBENCH",
                TableName = "Tasks",
            };

        return new AirtableDataProvider(client, http, options);
    }

    internal static List<AirtableRecord> BuildAirtableRecords(int count, int attachmentsPerRecord = 0)
    {
        var records = new List<AirtableRecord>(count);
        for (int i = 0; i < count; i++)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Name"]   = $"Record {i}",
                ["Status"] = "Active",
                ["Notes"]  = $"Notes for record {i}",
            };

            if (attachmentsPerRecord > 0)
            {
                var attachmentsJson = new StringBuilder("[");
                for (int a = 0; a < attachmentsPerRecord; a++)
                {
                    if (a > 0) attachmentsJson.Append(',');
                    attachmentsJson.Append(CultureInfo.InvariantCulture,
                        $$"""{"id":"att{{i}}_{{a}}","url":"https://dl.airtable.com/file{{i}}_{{a}}.pdf","filename":"file{{i}}_{{a}}.pdf"}""");
                }
                attachmentsJson.Append(']');

                var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    attachmentsJson.ToString());
                fields["Attachments"] = element;
            }

            var record = new AirtableRecord { Id = $"rec{i:D5}" };
            foreach (var kvp in fields)
                record.Fields[kvp.Key] = kvp.Value;
            records.Add(record);
        }
        return records;
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

/// <summary>Simple async enumerator over an array for GitLab benchmark mocking.</summary>
internal sealed class BenchArrayAsyncEnumerator<T>(T[] items) : IAsyncEnumerator<T>
{
    private int _index = -1;

    public T Current => items[_index];

    public ValueTask<bool> MoveNextAsync()
    {
        _index++;
        return ValueTask.FromResult(_index < items.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
