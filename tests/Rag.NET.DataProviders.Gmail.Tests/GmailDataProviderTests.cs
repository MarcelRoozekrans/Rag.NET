using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Gmail;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.Gmail.Tests;

public sealed class GmailDataProviderTests
{
    private static GmailDataProvider MakeProvider(
        IImapClient mockClient,
        GmailOptions? options = null)
        => new(
            new StaticTokenProvider("fake-token"),
            options ?? new GmailOptions(),
            clientFactory: () => mockClient);

    private static (IImapClient client, IMailFolder inbox) MakeMocks(
        IReadOnlyList<UniqueId> uids, MimeMessage message)
    {
        var client = Substitute.For<IImapClient>();
        var inbox  = Substitute.For<IMailFolder>();

        client.Inbox.Returns(inbox);
        client.AuthenticationMechanisms
            .Returns(new HashSet<string>(StringComparer.Ordinal));

        inbox.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<UniqueId>>(uids.ToList()));
        inbox.GetMessageAsync(Arg.Any<UniqueId>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(message));

        return (client, inbox);
    }

    private static MimeMessage MakeMessage(string subject = "Test Subject")
    {
        var msg = new MimeMessage();
        msg.Subject = subject;
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        msg.Body = new TextPart("plain") { Text = "Hello world" };
        return msg;
    }

    /// <summary>
    /// <see cref="GmailOptions.Query"/> reaches the server as a Gmail raw search.
    /// <para>
    /// The property shipped documented as "reserved… currently unused" with a default of
    /// <c>"in:inbox"</c> — a stated scoping that no code performed (issue #108). A test asserting
    /// only that enumeration still works would have passed throughout that period, so this asserts
    /// the search actually handed to the mailbox.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_ConfiguredQuery_IsSentToTheServerAsAGmailRawSearch()
    {
        var (client, inbox) = MakeMocks([new UniqueId(1)], MakeMessage());
        client.Capabilities.Returns(ImapCapabilities.GMailExt1);
        var sut = MakeProvider(client, new GmailOptions { Query = "from:alice@example.com" });

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await inbox.Received(1).SearchAsync(
            Arg.Is<SearchQuery>(q => IsRawSearchFor(q, "from:alice@example.com")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_ConfiguredQueryWithDeltaToken_NarrowsByBothRatherThanDroppingOne()
    {
        var (client, inbox) = MakeMocks([new UniqueId(9)], MakeMessage());
        client.Capabilities.Returns(ImapCapabilities.GMailExt1);
        var sut = MakeProvider(client, new GmailOptions
        {
            Query = "has:attachment",
            DeltaToken = new UniqueId(4).ToString(),
        });

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The delta path used to replace the search outright; a query set alongside a delta token
        // would have been dropped on every incremental run and applied on none.
        await inbox.Received(1).SearchAsync(
            Arg.Is<SearchQuery>(q => IsDeltaNarrowedBy(q, "has:attachment")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_ConfiguredQueryWithoutTheGmailExtension_ThrowsRatherThanReturningEverything()
    {
        var (client, _) = MakeMocks([new UniqueId(1)], MakeMessage());
        // Substitutes report ImapCapabilities.None, which is the case being asserted.
        var sut = MakeProvider(client, new GmailOptions { Query = "from:alice@example.com" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.GetFilesAsync(TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("X-GM-EXT-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NoQuery_MatchesEverythingAndNeedsNoExtension()
    {
        var (client, inbox) = MakeMocks([new UniqueId(1)], MakeMessage());
        var sut = MakeProvider(client, new GmailOptions());

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await inbox.Received(1).SearchAsync(SearchQuery.All, Arg.Any<CancellationToken>());
    }

    /// <summary>The default enumerates everything, so it cannot state a scoping nothing performs.</summary>
    [Fact]
    public void Query_DefaultsToEmpty_RatherThanClaimingInboxScoping()
    {
        Assert.Equal(string.Empty, new GmailOptions().Query);
    }

    private static bool IsRawSearchFor(SearchQuery? query, string expected) =>
        query is TextSearchQuery { Term: SearchTerm.GMailRaw } text
        && string.Equals(text.Text, expected, StringComparison.Ordinal);

    /// <summary>The delta UID range AND-ed with the configured query, both surviving.</summary>
    private static bool IsDeltaNarrowedBy(SearchQuery? query, string expected) =>
        query is BinarySearchQuery { Term: SearchTerm.And } binary
        && binary.Left is UidSearchQuery
        && IsRawSearchFor(binary.Right, expected);

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsOneEntryPerMessage()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1), new UniqueId(2)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, e => Assert.EndsWith(".md", e.Value.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FileName_DerivedFromSubject()
    {
        var message = MakeMessage("Invoice Q1-2026");
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("Invoice Q1-2026.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesAllEntries()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client, new GmailOptions { Extensions = [".txt"] });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullTokenProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GmailDataProvider(null!, new GmailOptions()));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaToken_SearchesUidsAboveWatermark()
    {
        var message = MakeMessage();
        var (client, inbox) = MakeMocks([new UniqueId(101), new UniqueId(102)], message);
        var sut = MakeProvider(client, new GmailOptions { DeltaToken = "100" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        // Verify SearchAsync was called exactly once (delta path taken)
        await inbox.Received(1).SearchAsync(
            Arg.Any<SearchQuery>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_InvalidDeltaToken_FallsBackToAll()
    {
        var message = MakeMessage();
        var (client, inbox) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client, new GmailOptions { DeltaToken = "not-a-number" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        // Should have called SearchAsync (falls back to SearchQuery.All path)
        await inbox.Received(1).SearchAsync(
            Arg.Any<SearchQuery>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_MaxResultsLimitsOutput()
    {
        var message = MakeMessage();
        var uids = Enumerable.Range(1, 5).Select(i => new UniqueId((uint)i)).ToList();
        var (client, _) = MakeMocks(uids, message);
        var sut = MakeProvider(client, new GmailOptions { MaxResults = 3 });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_NullSubject_FallbackToMessageUid()
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        msg.Body = new TextPart("plain") { Text = "body" };
        // Subject left as null

        var (client, _) = MakeMocks([new UniqueId(42)], msg);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("message-42.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_SpecialCharsInSubject_Sanitized()
    {
        var message = MakeMessage("Re: File/Path\\Test");
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.DoesNotContain("/", results[0].Value.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", results[0].Value.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".md", results[0].Value.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_HtmlOnlyBody_TagsStripped()
    {
        var msg = new MimeMessage();
        msg.Subject = "Html Email";
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        msg.Body = new TextPart("html") { Text = "<p>hello</p>" };

        var (client, _) = MakeMocks([new UniqueId(1)], msg);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("hello", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("</p>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_BothBodies_PrefersTextBody()
    {
        var msg = new MimeMessage();
        msg.Subject = "Dual Body";
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var multipart = new Multipart("alternative");
        multipart.Add(new TextPart("plain") { Text = "plain text body" });
        multipart.Add(new TextPart("html") { Text = "<p>html body</p>" });
        msg.Body = multipart;

        var (client, _) = MakeMocks([new UniqueId(1)], msg);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("plain text body", content, StringComparison.Ordinal);
        Assert.DoesNotContain("html body", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NeitherBody_EmptyContent()
    {
        var msg = new MimeMessage();
        msg.Subject = "Empty";
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        // No body set — TextBody and HtmlBody both null

        var (client, _) = MakeMocks([new UniqueId(1)], msg);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var content = await ReadContentAsync(results[0].Value);
        // The markdown header is still present but body portion should be empty/whitespace
        Assert.Contains("# Empty", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_MessageMetadata_IncludedInMarkdown()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("**From:**", content, StringComparison.Ordinal);
        Assert.Contains("**Date:**", content, StringComparison.Ordinal);
        Assert.Contains("**To:**", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1), new UniqueId(2)], message);
        var sut = MakeProvider(client);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token)
                .ToListAsync(cts.Token));
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsFromDateAndHasAttachments()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("\"Alice\" <alice@example.com>", metadata["from"]);
        // ISO-8601 round-trip, not the header's RFC 822 rendering.
        Assert.Equal("2026-03-01T10:00:00.0000000+00:00", metadata["date"]);
        Assert.Equal("false", metadata["has_attachments"]);
        Assert.Equal(3, metadata.Count);

        // From and Date stay in the Markdown header — that is what gets embedded.
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("**From:**", content, StringComparison.Ordinal);
        Assert.Contains("**Date:**", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Phase 4.10 Task 5: <c>message.Date</c> also becomes the typed
    /// <see cref="FileEntry.CreatedAt"/> — the header's <c>date</c> tag (asserted separately)
    /// is kept exactly as-is. Gmail carries no distinct "last modified" concept for a message,
    /// so <c>UpdatedAt</c> stays unset.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_Date_IsTypedAsCreatedAt()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(results).Value;
        Assert.Equal(message.Date.UtcDateTime, entry.CreatedAt);
        Assert.Null(entry.UpdatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_MessageWithAttachment_HasAttachmentsIsTrue()
    {
        var message = MakeMessage();
        var multipart = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Body" },
            new MimePart("application", "octet-stream")
            {
                Content                 = new MimeContent(new MemoryStream([1, 2, 3])),
                ContentDisposition      = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName                = "data.bin",
            },
        };
        message.Body = multipart;

        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        // The literal "true", never bool.ToString()'s "True" — HasTagSpec matches ordinally.
        Assert.Equal("true", metadata["has_attachments"]);
    }

    private static async Task<string> ReadContentAsync(FileEntry file)
    {
        await using var stream = await file.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
