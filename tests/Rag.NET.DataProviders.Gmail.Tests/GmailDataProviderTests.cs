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
}
