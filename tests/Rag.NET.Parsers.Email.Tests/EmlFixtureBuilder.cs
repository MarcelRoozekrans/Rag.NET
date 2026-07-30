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
    /// rather than through <c>ContainerEntryDispatcher</c>.
    /// </summary>
    public static async Task<byte[]> CreateWithEmbeddedAsync(
        string subject,
        string textBody,
        MimeMessage embedded,
        CancellationToken cancellationToken) =>
        await WriteAsync(CreateNested(subject, textBody, embedded), cancellationToken);

    /// <summary>
    /// Builds a message carrying file attachments and <i>several</i> live
    /// <see cref="MessagePart"/> children, attachments first — the same order
    /// <see cref="CreateNested"/> uses.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateNested"/> takes a single embedded message, so every fixture built with it
    /// has at most one descent per level and no sibling after one. That shape cannot tell a
    /// depth-first walk from a breadth-first one — both emit it identically — which is exactly
    /// what the ordering tests need to distinguish.
    /// </remarks>
    public static MimeMessage CreateBranching(
        string subject,
        string textBody,
        (string FileName, string ContentType, byte[] Data)[] attachments,
        params MimeMessage[] embedded)
    {
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = textBody } };
        foreach (var (fileName, contentType, data) in attachments)
        {
            multipart.Add(CreateAttachmentPart(fileName, contentType, data));
        }

        foreach (var child in embedded)
        {
            multipart.Add(CreateMessagePart(child));
        }

        var message = CreateEnvelope(subject);
        message.Body = multipart;
        return message;
    }

    /// <summary>Serialises a message built by <see cref="CreateBranching"/> or <see cref="CreateNested"/>.</summary>
    public static Task<byte[]> SerializeAsync(MimeMessage message, CancellationToken cancellationToken) =>
        WriteAsync(message, cancellationToken);

    /// <summary>Builds an in-memory message for use as a nested <see cref="MessagePart"/> payload.</summary>
    public static MimeMessage CreateNested(
        string subject,
        string textBody,
        MimeMessage? embedded = null,
        (string FileName, string ContentType, byte[] Data)[]? attachments = null)
    {
        var message = CreateEnvelope(subject);
        if (embedded is null && attachments is not { Length: > 0 })
        {
            message.Body = new TextPart("plain") { Text = textBody };
            return message;
        }

        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = textBody } };
        foreach (var (fileName, contentType, data) in attachments ?? [])
        {
            multipart.Add(CreateAttachmentPart(fileName, contentType, data));
        }

        if (embedded is not null)
        {
            multipart.Add(CreateMessagePart(embedded));
        }

        message.Body = multipart;
        return message;
    }

    private static MimePart CreateAttachmentPart(string fileName, string contentType, byte[] data) =>
        new(ContentType.Parse(contentType))
        {
            Content = new MimeContent(new MemoryStream(data)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName,
        };

    private static MessagePart CreateMessagePart(MimeMessage embedded) =>
        new()
        {
            Message = embedded,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
        };

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
