using MimeKit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Builds <c>.eml</c> fixtures in code via MimeKit. Complements
/// <see cref="MsgFixtureBuilder"/> so a fixture can alternate between the two container
/// formats, which is what the embedded-message recursion tests need.
/// </summary>
internal static class EmlFixtureBuilder
{
    /// <summary>
    /// Builds a message with an optional text body and any number of binary attachments.
    /// Attachments are ordinary <see cref="MimePart"/>s, so they travel through the
    /// stream-based attachment dispatch rather than MimeKit's <see cref="MessagePart"/>.
    /// </summary>
    public static async Task<byte[]> CreateAsync(
        string subject,
        string? textBody,
        (string FileName, string ContentType, byte[] Data)[]? attachments,
        CancellationToken cancellationToken)
    {
        var builder = new BodyBuilder();
        if (textBody is not null)
            builder.TextBody = textBody;

        foreach (var (fileName, contentType, data) in attachments ?? [])
        {
            builder.Attachments.Add(fileName, data, ContentType.Parse(contentType));
        }

        var message = CreateEnvelope(subject);
        message.Body = builder.ToMessageBody();
        return await WriteAsync(message, cancellationToken);
    }

    /// <summary>
    /// Builds a message whose only attachment is a live <see cref="MessagePart"/> — the shape
    /// MimeKit produces for a forwarded mail, and the one the parser recurses into in memory
    /// rather than through <c>EmailAttachmentDispatcher</c>.
    /// </summary>
    public static async Task<byte[]> CreateWithEmbeddedAsync(
        string subject,
        string textBody,
        MimeMessage embedded,
        CancellationToken cancellationToken)
    {
        var message = CreateEnvelope(subject);
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = textBody },
            new MessagePart
            {
                Message = embedded,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        return await WriteAsync(message, cancellationToken);
    }

    /// <summary>Builds an in-memory message for use as a nested <see cref="MessagePart"/> payload.</summary>
    public static MimeMessage CreateNested(string subject, string textBody, MimeMessage? embedded = null)
    {
        var message = CreateEnvelope(subject);
        if (embedded is null)
        {
            message.Body = new TextPart("plain") { Text = textBody };
            return message;
        }

        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = textBody },
            new MessagePart
            {
                Message = embedded,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };
        return message;
    }

    private static MimeMessage CreateEnvelope(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = subject;
        return message;
    }

    private static async Task<byte[]> WriteAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
