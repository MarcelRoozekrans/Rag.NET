using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Rag.NET.DataProviders.Testing;
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

    /// <summary>
    /// Phase 4.10 Task 5: <c>receivedDateTime</c> becomes
    /// <see cref="Rag.NET.DataProviders.FileEntry.CreatedAt"/> — a deliberate modelling choice,
    /// a received mail's "creation" for our purposes — and <c>lastModifiedDateTime</c> becomes
    /// <see cref="Rag.NET.DataProviders.FileEntry.UpdatedAt"/>. The
    /// <c>received_at</c> metadata tag (asserted separately above) is kept exactly as-is.
    /// </summary>
    [Fact]
    public async Task Enumerate_ReceivedAndLastModifiedDateTime_BecomeTypedTimestamps()
    {
        var handler = MakeHandler((InboxKey, HttpStatusCode.OK, InboxMessagesJson));
        var sut     = MakeProvider(handler);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), entry.CreatedAt);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 5, 0, DateTimeKind.Utc), entry.UpdatedAt);
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
        // Truncated in the only (= last) folder: watermark advances to the truncation
        // point so the backlog drains at MaxResults per run; the run is still incomplete.
        Assert.False(sut.CompletedFullTraversal);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture),
            sut.GetDeltaToken());
    }

    // -------------------------------------------------------------------------
    // 7a-bis. Capped backlog drains: truncation token feeds the next run's filter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Watermark_SingleFolderCap_AdvancesAndNextRunResumesFromTruncationPoint()
    {
        const string twoMessages = """
            {
              "value": [
                { "id": "msg-1", "subject": "First", "receivedDateTime": "2026-03-01T10:00:00Z", "hasAttachments": false },
                { "id": "msg-2", "subject": "Second", "receivedDateTime": "2026-03-01T11:00:00Z", "hasAttachments": false }
              ]
            }
            """;
        var handler1 = MakeHandler((InboxKey, HttpStatusCode.OK, twoMessages));
        var logger   = new CapturingLogger<ExchangeMailDataProvider>();
        var run1     = MakeProvider(handler1, o => o.MaxResults = 1, logger);

        _ = await run1.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var token = run1.GetDeltaToken();
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture),
            token);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("advanced", warning.Message, StringComparison.Ordinal);

        // Second run with the returned token requests the NEXT batch via the ge filter.
        var handler2 = MakeHandler((InboxKey, HttpStatusCode.OK, twoMessages));
        var run2     = MakeProvider(handler2, o =>
        {
            o.MaxResults = 1;
            o.DeltaToken = token;
        });

        _ = await run2.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var listRequest = Assert.Single(handler2.Requests, u => u.Contains(InboxKey, StringComparison.Ordinal));
        Assert.Contains(
            "receivedDateTime ge 2026-03-01T10:00:00Z",
            Uri.UnescapeDataString(listRequest),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // 7b. MaxResults cap spans multiple folders ("across all folders")
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enumerate_MaxResults_SpansFolders()
    {
        const string inboxTwo = """
            {
              "value": [
                { "id": "msg-1", "subject": "One", "receivedDateTime": "2026-03-01T10:00:00Z", "hasAttachments": false },
                { "id": "msg-2", "subject": "Two", "receivedDateTime": "2026-03-01T11:00:00Z", "hasAttachments": false }
              ]
            }
            """;
        const string archiveTwo = """
            {
              "value": [
                { "id": "msg-a1", "subject": "Old One", "receivedDateTime": "2026-01-01T08:00:00Z", "hasAttachments": false },
                { "id": "msg-a2", "subject": "Old Two", "receivedDateTime": "2026-01-02T08:00:00Z", "hasAttachments": false }
              ]
            }
            """;
        var handler = MakeHandler(
            (InboxKey, HttpStatusCode.OK, inboxTwo),
            (ArchiveKey, HttpStatusCode.OK, archiveTwo));
        var sut = MakeProvider(handler, o =>
        {
            o.FolderIds  = ["inbox", "archive"];
            o.MaxResults = 3;
        });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, entries.Count);
        string[] expectedIds = ["inbox/msg-1", "inbox/msg-2", "archive/msg-a1"];
        Assert.Equal(expectedIds, entries.Select(e => e.Value.Id.Value).ToList(), StringComparer.Ordinal);
        Assert.False(sut.CompletedFullTraversal);
        // Cap fired in the LAST folder: the token is that folder's last-seen
        // receivedDateTime (2026-01-01), NOT the global max (inbox, 2026-03-01) —
        // using the global max would skip archive/msg-a2 forever.
        Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture),
            sut.GetDeltaToken());
    }

    // -------------------------------------------------------------------------
    // 7c. Truncated multi-folder run withholds the watermark and warns
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Watermark_TruncatedMultiFolderRun_WithholdsTokenAndWarns()
    {
        const string inboxTwo = """
            {
              "value": [
                { "id": "msg-1", "subject": "One", "receivedDateTime": "2026-03-01T10:00:00Z", "hasAttachments": false },
                { "id": "msg-2", "subject": "Two", "receivedDateTime": "2026-03-01T11:00:00Z", "hasAttachments": false }
              ]
            }
            """;
        const string archiveOne = """
            {
              "value": [
                { "id": "msg-a1", "subject": "Old", "receivedDateTime": "2026-01-01T08:00:00Z", "hasAttachments": false }
              ]
            }
            """;
        var handler = MakeHandler(
            (InboxKey, HttpStatusCode.OK, inboxTwo),
            (ArchiveKey, HttpStatusCode.OK, archiveOne));
        var logger = new CapturingLogger<ExchangeMailDataProvider>();
        var sut = MakeProvider(handler, o =>
        {
            o.FolderIds  = ["inbox", "archive"];
            o.MaxResults = 1; // cap hits mid-inbox; archive is never visited
        }, logger);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.DoesNotContain(handler.Requests, u => u.Contains(ArchiveKey, StringComparison.Ordinal));
        Assert.False(sut.CompletedFullTraversal);
        // Cap fired in a NON-last folder: archive was never visited, so no safe
        // watermark exists — the token is withheld entirely.
        Assert.Null(sut.GetDeltaToken());
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("MaxResults=1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("withheld", warning.Message, StringComparison.Ordinal);
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
        // Failed run: the watermark must not advance.
        Assert.False(sut.CompletedFullTraversal);
        Assert.Null(sut.GetDeltaToken());
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

        Assert.True(sut.CompletedFullTraversal);
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
    // Transport failures (no HTTP response at all) → RagError.TransportFailed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TransportFailure_MapsToResultFailure_AndDoesNotAdvanceWatermark()
    {
        // DNS/TLS/socket failure: HttpRequestException escaped the Result channel before.
        var handler = new ThrowingGraphHandler(
            () => new HttpRequestException("No such host is known (graph.microsoft.com:443)."));
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<RagError.TransportFailed>(result.Error);
        _ = Assert.IsType<HttpRequestException>(failure.Inner);
        // Failed run: the watermark must not advance.
        Assert.False(sut.CompletedFullTraversal);
        Assert.Null(sut.GetDeltaToken());
    }

    [Fact]
    public async Task ApiExceptionWithoutResponse_MapsToTransportFailedNotHttpFailed()
    {
        // Kiota leaves ResponseStatusCode at 0 when no response was received;
        // (HttpStatusCode)0 is out of range, so this must not become HttpFailed.
        var handler = new ThrowingGraphHandler(
            () => new Microsoft.Kiota.Abstractions.ApiException("The request timed out.")
            {
                ResponseStatusCode = 0,
            });
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<RagError.TransportFailed>(result.Error);
        Assert.Equal(0, Assert.IsType<Microsoft.Kiota.Abstractions.ApiException>(failure.Inner)
            .ResponseStatusCode);
        Assert.Null(sut.GetDeltaToken());
    }

    [Fact]
    public async Task AuthenticationFailure_MapsToTransportFailure()
    {
        var handler = new ThrowingGraphHandler(
            () => new Azure.Identity.AuthenticationFailedException("token endpoint unreachable"));
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        var failure = Assert.IsType<RagError.TransportFailed>(result.Error);
        _ = Assert.IsType<Azure.Identity.AuthenticationFailedException>(failure.Inner);
    }

    [Fact]
    public async Task HttpClientTimeout_MapsToTransportFailure()
    {
        // An HttpClient timeout surfaces as a TaskCanceledException while the caller's token
        // is NOT signalled. That is a transport failure, not a cancellation.
        var handler = new ThrowingGraphHandler(
            () => new TaskCanceledException("The request was canceled due to HttpClient.Timeout."));
        var sut = MakeProvider(handler);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<RagError.TransportFailed>(result.Error);
        _ = Assert.IsType<TaskCanceledException>(failure.Inner);
    }

    [Fact]
    public async Task CallerCancellation_Throws_AndIsNotMappedToAFailure()
    {
        // Cancellation is driven from INSIDE the HTTP call. Cancelling before enumeration
        // would be vacuous: EnumerateFolderAsync's ThrowIfCancellationRequested fires at the
        // top of the paging loop and the exception never reaches FetchPageAsync's catch.
        // Signalling mid-send makes GraphErrorMapping.IsMappable's cancellation guard — and
        // nothing else — the thing that decides between throwing and mapping. Removing that
        // guard turns this test red.
        using var cts = new CancellationTokenSource();
        var sut = MakeProvider(new CancellingGraphHandler(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    // -------------------------------------------------------------------------
    // 11. Shared connector-metadata contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_AllEntriesSatisfyMetadataContract()
    {
        // Two folders off the same fixture: 'folder' varies per entry, so the check sees
        // every key this connector emits rather than a single folder's worth.
        var handler = MakeHandler(
            (InboxKey, HttpStatusCode.OK, InboxMessagesJson),
            (ArchiveKey, HttpStatusCode.OK, InboxMessagesJson));
        var sut = MakeProvider(handler, o => o.FolderIds = ["inbox", "archive"]);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        MetadataContract.AssertAll(entries.Select(e => e.Value));
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

    [Fact]
    public void AddExchangeMailDataProvider_NonPositiveMaxResults_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddExchangeMailDataProvider("tenant", "client", "secret", o =>
            {
                o.Mailbox    = "ingest@contoso.com";
                o.MaxResults = 0;
            }));
        Assert.Contains("MaxResults", ex.Message, StringComparison.Ordinal);
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

    private static GraphServiceClient MakeGraphClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new GraphServiceClient(httpClient);
    }

    private static ExchangeMailDataProvider MakeProvider(
        HttpMessageHandler handler,
        Action<ExchangeMailOptions>? configure = null,
        ILogger<ExchangeMailDataProvider>? logger = null)
    {
        var opts = new ExchangeMailOptions { Mailbox = "user1" };
        configure?.Invoke(opts);
        return new ExchangeMailDataProvider(MakeGraphClient(handler), opts, logger);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

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

/// <summary>
/// Signals the caller's <see cref="CancellationTokenSource"/> from inside the HTTP call, then
/// fails the way <see cref="HttpClient"/> does when a token trips mid-flight.
/// <para>
/// This is what keeps the caller-cancellation test honest. Cancelling before enumeration
/// starts is vacuous — the provider's own <c>ThrowIfCancellationRequested</c> fires at the top
/// of the paging loop and the exception never reaches the mapping code at all. Cancelling from
/// here means the token is only signalled once execution is already inside the fetch, so the
/// cancellation guard in <c>GraphErrorMapping.IsMappable</c> is the only thing standing
/// between a propagated <see cref="OperationCanceledException"/> and a mapped failure.
/// </para>
/// </summary>
file sealed class CancellingGraphHandler(CancellationTokenSource cancelOnSend) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancelOnSend.Cancel();
        throw new TaskCanceledException("A task was canceled.");
    }
}

