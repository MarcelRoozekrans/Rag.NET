using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using System.Text;
using Xunit;
using EmailPackageParser = Rag.NET.Parsers.Email.EmailDocumentParser;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// The end-to-end case Phase 3.11 exists for: both parser packages registered in one container,
/// one <c>.eml</c> carrying an attachment whose content type is <c>application/octet-stream</c>.
/// </summary>
/// <remarks>
/// <para>
/// This file is the first thing in the repository to reference both
/// <c>Rag.NET.Chunking.Templates</c> and <c>Rag.NET.Parsers.Email</c>. The defect only exists when
/// they share an <see cref="IDocumentParser"/> collection, which is why it survived two suites that
/// each covered one package thoroughly.
/// </para>
/// <para>
/// Which parser wins a content type is registration-order dependent — <c>ParseBehavior</c> and
/// <c>EmailAttachmentDispatcher</c> both take the <i>first</i> match — so every case runs in both
/// orders.
/// </para>
/// </remarks>
public sealed class ParserClaimConflictTests
{
    private const string BodyText = "This is the body text of the message.";
    private const string NotesText = "notes content";

    /// <summary>
    /// An extension no MIME table knows. A mail client types such an attachment
    /// <c>application/octet-stream</c> in the part headers, which is the same fallback
    /// <c>MimeTypeMap.FromFileName</c> produces on the <c>.msg</c> path.
    /// </summary>
    private const string UnknownExtensionAttachment = "payload.dat";

    private static readonly byte[] OpaqueBytes = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD, 0x7F, 0x80];

    [Fact]
    public async Task AnUnknownExtensionAttachmentDoesNotFailTheWholeDocument_TemplatesRegisteredFirst()
    {
        // Rag.NET.Chunking.Templates' parsers claim application/octet-stream, the content type any
        // unknown extension resolves to. EmailAttachmentDispatcher has no try/catch around
        // parser.ParseAsync, so the Templates parser throwing on opaque bytes escapes the entire
        // document parse -- body and every other attachment with it.
        var sections = await ParseWithBothPackagesRegisteredAsync(
            templatesRegisteredFirst: true, TestContext.Current.CancellationToken);

        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnknownExtensionAttachmentDoesNotFailTheWholeDocument_EmailParserRegisteredFirst()
    {
        // The other order. Rag.NET.Parsers.Email's parsers decline application/octet-stream, so the
        // search falls through to the Templates parser either way -- registering the email package
        // first is not a workaround.
        var sections = await ParseWithBothPackagesRegisteredAsync(
            templatesRegisteredFirst: false, TestContext.Current.CancellationToken);

        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<DocumentSection>> ParseWithBothPackagesRegisteredAsync(
        bool templatesRegisteredFirst,
        CancellationToken cancellationToken)
    {
        await using var provider = BuildProvider(templatesRegisteredFirst);

        // The document is parsed by Rag.NET.Parsers.Email's parser deliberately: it is the one that
        // dispatches attachments, so the test is about attachment routing rather than about which
        // parser wins message/rfc822 at the top level.
        var parser = provider.GetServices<IDocumentParser>().OfType<EmailPackageParser>().Single();

        using var stream = new MemoryStream(await CreateEmlAsync(cancellationToken));
        return await parser.ParseAsync(stream, CreateMetadata(), cancellationToken).ToListAsync(cancellationToken);
    }

    private static ServiceProvider BuildProvider(bool templatesRegisteredFirst)
    {
        var services = new ServiceCollection();
        services.AddRagNet(builder =>
        {
            if (templatesRegisteredFirst)
            {
                builder.UseEmailChunking();
                builder.AddEmailParser();
            }
            else
            {
                builder.AddEmailParser();
                builder.UseEmailChunking();
            }
        });

        return services.BuildServiceProvider();
    }

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("eml-conflict-1"),
        FileName = "message.eml",
        ContentType = "message/rfc822",
    };

    private static async Task<byte[]> CreateEmlAsync(CancellationToken cancellationToken)
    {
        var body = new BodyBuilder { TextBody = BodyText };
        body.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes(NotesText), ContentType.Parse("text/plain"));
        body.Attachments.Add(
            UnknownExtensionAttachment, OpaqueBytes, ContentType.Parse("application/octet-stream"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Conflicting parser claims";
        message.Body = body.ToMessageBody();

        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
