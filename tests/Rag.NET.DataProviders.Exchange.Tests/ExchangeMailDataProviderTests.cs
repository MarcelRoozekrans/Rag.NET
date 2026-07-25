using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.DataProviders.Exchange.Tests;

public sealed class ExchangeMailDataProviderTests
{
    // -------------------------------------------------------------------------
    // Stub JSON payloads
    // -------------------------------------------------------------------------

    private const string InboxMessagesJson = """
        {
          "value": [{
            "id": "msg-1",
            "subject": "Quarterly Report",
            "receivedDateTime": "2026-03-01T10:00:00Z",
            "lastModifiedDateTime": "2026-03-01T10:05:00Z",
            "hasAttachments": true
          }]
        }
        """;

    // URL substrings matched by the fake handler (Graph SDK sends /v1.0/... paths).
    private const string InboxKey   = "/mailFolders/inbox/messages";
    private const string ArchiveKey = "/mailFolders/archive/messages";

    // -------------------------------------------------------------------------
    // 1. Inbox default enumeration — ids, .eml filenames, ETag, metadata
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_InboxDefault_EmitsEmlHandles()
    {
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, InboxMessagesJson));
        var sut     = MakeProvider(handler);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.Equal("inbox/msg-1", entry.Id.Value);
        Assert.Equal("Quarterly Report.eml", entry.FileName);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 10, 5, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture),
            entry.ETag);
        Assert.NotNull(entry.Metadata);
        Assert.Equal("inbox", entry.Metadata["folder"]);
        Assert.Equal("true", entry.Metadata["has_attachments"]);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture),
            entry.Metadata["received_at"]);
    }

    // -------------------------------------------------------------------------
    // 2. Multiple folders
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_MultipleFolders_AllListed()
    {
        const string archiveMessagesJson = """
            {
              "value": [{
                "id": "msg-2",
                "subject": "Old Mail",
                "receivedDateTime": "2026-01-01T08:00:00Z",
                "lastModifiedDateTime": "2026-01-01T08:00:00Z",
                "hasAttachments": false
              }]
            }
            """;
        var handler = MakeHandler(
            (InboxKey, HttpStatusCode.OK, InboxMessagesJson),
            (ArchiveKey, HttpStatusCode.OK, archiveMessagesJson));
        var sut = MakeProvider(handler, o => o.FolderIds = ["inbox", "archive"]);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("inbox/msg-1", entries[0].Value.Id.Value);
        Assert.Equal("archive/msg-2", entries[1].Value.Id.Value);
        Assert.Equal("false", entries[1].Value.Metadata!["has_attachments"]);
    }

    // -------------------------------------------------------------------------
    // 3. OdataNextLink paging
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_Paging_FollowsNextLink()
    {
        const string page1 = """
            {
              "value": [{
                "id": "msg-1",
                "subject": "First",
                "receivedDateTime": "2026-03-01T10:00:00Z",
                "lastModifiedDateTime": "2026-03-01T10:00:00Z",
                "hasAttachments": false
              }],
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/users/user1/mailFolders/inbox/messages?page=2"
            }
            """;
        const string page2 = """
            {
              "value": [{
                "id": "msg-2",
                "subject": "Second",
                "receivedDateTime": "2026-03-01T11:00:00Z",
                "lastModifiedDateTime": "2026-03-01T11:00:00Z",
                "hasAttachments": false
              }]
            }
            """;
        var handler = MakeHandler(
            (InboxKey, HttpStatusCode.OK, page1),
            ("/mailFolders/inbox/messages?page=2", HttpStatusCode.OK, page2));
        var sut = MakeProvider(handler);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("inbox/msg-1", entries[0].Value.Id.Value);
        Assert.Equal("inbox/msg-2", entries[1].Value.Id.Value);
    }

    // -------------------------------------------------------------------------
    // 4. DeltaToken → receivedDateTime ge filter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_DeltaToken_AppendsReceivedDateTimeFilter()
    {
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, InboxMessagesJson));
        var sut     = MakeProvider(handler, o => o.DeltaToken = "2026-02-15T08:30:00Z");

        _ = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var listRequest = Assert.Single(handler.Requests, u => u.Contains(InboxKey, StringComparison.Ordinal));
        Assert.Contains(
            "receivedDateTime ge 2026-02-15T08:30:00Z",
            Uri.UnescapeDataString(listRequest),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // 5. Invalid DeltaToken → failure naming the token
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_InvalidDeltaToken_YieldsFailure()
    {
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, InboxMessagesJson));
        var sut     = MakeProvider(handler, o => o.DeltaToken = "not-a-timestamp");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<RagError.ValidationFailed>(result.Error);
        var detail  = Assert.Single(failure.Failures);
        Assert.Equal("DeltaToken", detail.PropertyName);
        Assert.Contains("not-a-timestamp", detail.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(handler.Requests); // nothing fetched
    }

    // -------------------------------------------------------------------------
    // 6. Content is lazy — $value fetched only on OpenContentAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Content_IsLazy()
    {
        const string mime = "From: alice@contoso.com\r\nSubject: Quarterly Report\r\n\r\nBody text.";
        var handler = MakeHandler(
            (InboxKey, HttpStatusCode.OK, InboxMessagesJson),
            ("/messages/msg-1/$value", HttpStatusCode.OK, mime));
        var sut = MakeProvider(handler);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Requests, u => u.Contains("$value", StringComparison.Ordinal));

        using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        Assert.Equal(mime, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests, u => u.Contains("$value", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------
    // 7. MaxResults caps enumeration
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_MaxResults_Caps()
    {
        const string twoMessages = """
            {
              "value": [
                {
                  "id": "msg-1",
                  "subject": "First",
                  "receivedDateTime": "2026-03-01T10:00:00Z",
                  "hasAttachments": false
                },
                {
                  "id": "msg-2",
                  "subject": "Second",
                  "receivedDateTime": "2026-03-01T11:00:00Z",
                  "hasAttachments": false
                }
              ]
            }
            """;
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, twoMessages));
        var sut     = MakeProvider(handler, o => o.MaxResults = 1);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.Equal("inbox/msg-1", entry.Value.Id.Value);
    }

    // -------------------------------------------------------------------------
    // 8. Graph error → Result failure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GraphError_MapsToResultFailure()
    {
        const string errorJson = """
            { "error": { "code": "InternalServerError", "message": "mailbox unavailable" } }
            """;
        var handler = MakeHandler((InboxKey, HttpStatusCode.InternalServerError, errorJson));
        var sut     = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<RagError.HttpFailed>(result.Error);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Contains("mailbox unavailable", failure.Content, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // 9. Watermark advances to max receivedDateTime
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Watermark_AdvancesToMaxReceived()
    {
        const string unorderedMessages = """
            {
              "value": [
                {
                  "id": "msg-2",
                  "subject": "Later",
                  "receivedDateTime": "2026-03-02T09:00:00Z",
                  "hasAttachments": false
                },
                {
                  "id": "msg-1",
                  "subject": "Earlier",
                  "receivedDateTime": "2026-03-01T10:00:00Z",
                  "hasAttachments": false
                }
              ]
            }
            """;
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, unorderedMessages));
        var graph   = MakeGraphClient(handler);
        var sut     = new ExchangeMailDataProvider(graph, new ExchangeMailOptions { Mailbox = "user1" });

        Assert.Null(sut.GetDeltaToken());

        _ = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture),
            sut.GetDeltaToken());
    }

    // -------------------------------------------------------------------------
    // 10. Filename sanitization + empty-subject fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Filename_SanitizedAndFallback()
    {
        const string messages = """
            {
              "value": [
                {
                  "id": "msg-1",
                  "subject": "Budget/Plans 2026",
                  "receivedDateTime": "2026-03-01T10:00:00Z",
                  "hasAttachments": false
                },
                {
                  "id": "msg-2",
                  "subject": "   ",
                  "receivedDateTime": "2026-03-01T11:00:00Z",
                  "hasAttachments": false
                }
              ]
            }
            """;
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, messages));
        var sut     = MakeProvider(handler);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Budget_Plans 2026.eml", entries[0].Value.FileName);
        Assert.Equal("message-msg-2.eml", entries[1].Value.FileName);
    }

    // -------------------------------------------------------------------------
    // Constructor guard
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExchangeMailDataProvider(null!, new ExchangeMailOptions { Mailbox = "user1" }));
    }

    // -------------------------------------------------------------------------
    // DI registration
    // -------------------------------------------------------------------------

    [Fact]
    public void AddExchangeMailDataProvider_ResolvesProvider()
    {
        var services = new ServiceCollection();

        services.AddExchangeMailDataProvider(
            tenantId: "tenant", clientId: "client", clientSecret: "secret",
            configure: o => o.Mailbox = "ingest@contoso.com");

        var provider = services.BuildServiceProvider()
            .GetRequiredService<Rag.NET.DataProviders.IFileContentProvider>();
        _ = Assert.IsType<ExchangeMailDataProvider>(provider);
    }

    [Fact]
    public void AddExchangeMailDataProvider_EmptyMailbox_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddExchangeMailDataProvider("tenant", "client", "secret"));
        Assert.Contains("Mailbox", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static FakeGraphHandler MakeHandler(
        params (string Key, HttpStatusCode Status, string Body)[] responses)
        => new(responses.ToDictionary(
            r => r.Key,
            r => (r.Status, r.Body),
            StringComparer.Ordinal));

    private static GraphServiceClient MakeGraphClient(FakeGraphHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new GraphServiceClient(httpClient);
    }

    private static ExchangeMailDataProvider MakeProvider(
        FakeGraphHandler handler, Action<ExchangeMailOptions>? configure = null)
    {
        var opts = new ExchangeMailOptions { Mailbox = "user1" };
        configure?.Invoke(opts);
        return new ExchangeMailDataProvider(MakeGraphClient(handler), opts);
    }
}

