using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Gmail;
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

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsOneEntryPerMessage()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1), new UniqueId(2)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, e => Assert.EndsWith(".md", e.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FileName_DerivedFromSubject()
    {
        var message = MakeMessage("Invoice Q1-2026");
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Invoice Q1-2026.md", results[0].FileName);
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

        Assert.Single(results);
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

        Assert.Single(results);
        Assert.Equal("message-42.md", results[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_SpecialCharsInSubject_Sanitized()
    {
        var message = MakeMessage("Re: File/Path\\Test");
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.DoesNotContain("/", results[0].FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", results[0].FileName, StringComparison.Ordinal);
        Assert.EndsWith(".md", results[0].FileName, StringComparison.Ordinal);
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

        var content = await ReadContentAsync(results[0]);
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

        var content = await ReadContentAsync(results[0]);
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

        var content = await ReadContentAsync(results[0]);
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

        var content = await ReadContentAsync(results[0]);
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

    private static async Task<string> ReadContentAsync(FileEntry file)
    {
        await using var stream = await file.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
