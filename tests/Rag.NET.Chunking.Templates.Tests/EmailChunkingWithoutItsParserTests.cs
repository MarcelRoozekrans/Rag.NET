using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Xunit;
using EmailPackageParser = Rag.NET.Parsers.Email.EmailDocumentParser;
using TemplatesEmailParser = Rag.NET.Chunking.Templates.EmailDocumentParser;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// Both packages in one container, which Phase 3.11 made possible again after making it illegal.
/// </summary>
/// <remarks>
/// <para>
/// The phase's original end-to-end test registered <c>UseEmailChunking()</c> alongside
/// <c>AddEmailParser()</c> and asserted that an unknown-extension attachment did not fail the whole
/// document. The conflict guard then declared that exact configuration a startup error, so the test
/// and the guard could not both be right. The guard is right — the two parsers really do both claim
/// <c>message/rfc822</c> and one of them really does silently never run — but its instruction to
/// "register only one of them" was unfollowable, because <c>UseEmailChunking()</c> registered a
/// parser and a chunking strategy together and there was no way to take the strategy alone.
/// </para>
/// <para>
/// <see cref="EmailChunkingOptions.RegisterParser"/> is what makes the instruction followable, and
/// this file is the test that it does: email-shaped chunking with <c>Rag.NET.Parsers.Email</c>
/// doing the parsing, which is the pairing a user would actually want. The unknown-extension
/// attachment case it replaces is pinned by <see cref="QAPairsAttachmentClaimTests"/>, which uses
/// the one Templates registration that shares no content type with <c>AddEmailParser()</c>.
/// </para>
/// </remarks>
public sealed class EmailChunkingWithoutItsParserTests
{
    private const string OuterBody = "This is the outer message body.";
    private const string InnerBody = "This is the embedded message body.";
    private const string NotesText = "notes content";

    [Fact]
    public void OptingOutOfTheParserMakesTheTwoPackagesRegisterableTogether()
    {
        using var provider = BuildOptedOutProvider();

        var parsers = provider.GetServices<IDocumentParser>();
        Assert.Contains(parsers, p => p is EmailPackageParser);
        Assert.DoesNotContain(parsers, p => p is TemplatesEmailParser);
    }

    /// <summary>
    /// The opt-out must drop only the parser. Taking the chunking strategy is the entire reason a
    /// user calls <c>UseEmailChunking()</c> while letting the other package parse.
    /// </summary>
    [Fact]
    public void OptingOutOfTheParserKeepsTheChunkingStrategy()
    {
        using var provider = BuildOptedOutProvider();

        Assert.Contains(
            provider.GetServices<IDocumentChunkingStrategy>(), s => s is EmailChunkingStrategy);
        Assert.Contains(provider.GetServices<IChunkingStrategy>(), s => s is EmailChunkingStrategy);
        Assert.False(provider.GetRequiredService<EmailChunkingOptions>().RegisterParser);
    }

    /// <summary>
    /// Resolution, not just registration: the parser an <c>.eml</c> actually reaches has to be
    /// <c>Rag.NET.Parsers.Email</c>'s. Asserted through behaviour rather than through the selected
    /// type alone — only that parser descends into an embedded message, so the inner body
    /// surfacing is proof of which one ran. The Templates parser skips a <c>MessagePart</c>
    /// outright: it inlines only <c>.txt</c>, <c>.md</c>, <c>.csv</c> and <c>.tsv</c> attachments.
    /// </summary>
    [Fact]
    public async Task AnEmlIsParsedByTheEmailParserPackage()
    {
        using var provider = BuildOptedOutProvider();

        var parser = SelectParserFor("message/rfc822", provider);
        Assert.IsType<EmailPackageParser>(parser);

        var cancellationToken = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(await CreateNestedEmlAsync(cancellationToken));
        var sections = await parser
            .ParseAsync(stream, CreateMetadata(), cancellationToken)
            .ToListAsync(cancellationToken);

        Assert.Contains(sections, s => s.Text.Contains(OuterBody, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(InnerBody, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
    }

    /// <summary>
    /// The escape hatch the startup error advertises has to be the one that works. Without the
    /// opt-out the same pairing throws, and the message quotes the call this file uses verbatim.
    /// </summary>
    [Fact]
    public void WithoutTheOptOutTheSamePairingIsAStartupErrorNamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            }));

        Assert.Contains(
            "UseEmailChunking(o => o.RegisterParser = false)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mirrors how both <c>ParseBehavior</c> and <c>EmailAttachmentDispatcher</c> choose: the first
    /// registered parser whose <c>CanParse</c> matches.
    /// </summary>
    private static IDocumentParser SelectParserFor(string contentType, IServiceProvider provider)
    {
        foreach (var parser in provider.GetServices<IDocumentParser>())
        {
            if (parser.CanParse(contentType))
                return parser;
        }

        throw new InvalidOperationException($"No registered parser claims '{contentType}'.");
    }

    private static ServiceProvider BuildOptedOutProvider()
    {
        var services = new ServiceCollection();
        services.AddRagNet(builder =>
        {
            builder.UseEmailChunking(o => o.RegisterParser = false);
            builder.AddEmailParser();
        });

        return services.BuildServiceProvider();
    }

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("eml-optout-1"),
        FileName = "message.eml",
        ContentType = "message/rfc822",
    };

    private static async Task<byte[]> CreateNestedEmlAsync(CancellationToken cancellationToken)
    {
        var inner = new MimeMessage();
        inner.From.Add(new MailboxAddress("Inner", "inner@example.com"));
        inner.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        inner.Subject = "Embedded message";
        inner.Body = new BodyBuilder { TextBody = InnerBody }.ToMessageBody();

        // Built by hand rather than through BodyBuilder: an embedded message has to be a live
        // MessagePart for the traversal to descend into it, and BodyBuilder.Attachments only takes
        // bytes.
        var notes = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(NotesText))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "notes.txt",
        };

        var multipart = new Multipart("mixed")
        {
            new TextPart("plain") { Text = OuterBody },
            notes,
            // MimeMessage.Attachments only surfaces entities marked as attachments, so the
            // disposition is what makes the embedded message visible to the traversal at all.
            new MessagePart
            {
                Message = inner,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        var outer = new MimeMessage();
        outer.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        outer.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        outer.Subject = "Outer message";
        outer.Body = multipart;

        using var stream = new MemoryStream();
        await outer.WriteToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
