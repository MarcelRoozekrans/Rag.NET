using BenchmarkDotNet.Attributes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Gmail;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Gmail-specific benchmarks: full traversal, text-only body, and HTML-only body (with stripping).
/// </summary>
[MemoryDiagnoser]
public class GmailBenchmarks
{
    private GmailDataProvider _fullProvider = default!;
    private GmailDataProvider _textOnlyProvider = default!;
    private GmailDataProvider _htmlOnlyProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Full traversal: 20 messages with plain text body
        _fullProvider = ConnectorIngestionBenchmarks.CreateGmailProvider();

        // Text body only: 5 messages
        _textOnlyProvider = MakeGmailProvider(5, bodyType: "plain",
            bodyText: "This is a plain text email body used for benchmark throughput testing.");

        // HTML body only: 5 messages with ~5KB HTML
        var htmlBody = "<html><body>" +
            string.Concat(Enumerable.Range(0, 50)
                .Select(i => $"<p>Paragraph {i}: Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor.</p>")) +
            "</body></html>";
        _htmlOnlyProvider = MakeGmailProvider(5, bodyType: "html", bodyText: htmlBody);
    }

    [Benchmark]
    public async Task<int> FullTraversal()
    {
        int count = 0;
        await foreach (var file in _fullProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> TextBodyOnly()
    {
        int count = 0;
        await foreach (var file in _textOnlyProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> HtmlBodyOnly()
    {
        int count = 0;
        await foreach (var file in _htmlOnlyProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static GmailDataProvider MakeGmailProvider(int count, string bodyType, string bodyText)
    {
        var uids = Enumerable.Range(1, count).Select(i => new UniqueId((uint)i)).ToList();
        var msg = new MimeMessage();
        msg.Subject = "Bench Subject";
        msg.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        msg.To.Add(new MailboxAddress("Receiver", "receiver@example.com"));
        msg.Date = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        msg.Body = new TextPart(bodyType) { Text = bodyText };

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
}
