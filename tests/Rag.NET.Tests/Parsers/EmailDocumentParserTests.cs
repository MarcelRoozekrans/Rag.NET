using System.Text;
using MimeKit;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class EmailDocumentParserTests
{
    private readonly HtmlDocumentParser _htmlParser = new();

    private EmailDocumentParser CreateSut(params IDocumentParser[] extraParsers) =>
        new([_htmlParser, ..extraParsers], _htmlParser);

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("email-1"),
        FileName = "test.eml",
        ContentType = "message/rfc822",
    };

    [Fact]
    public void CanParse_MessageRfc822_ReturnsTrue()
    {
        Assert.True(CreateSut().CanParse("message/rfc822"));
    }

    [Fact]
    public void CanParse_OtherType_ReturnsFalse()
    {
        Assert.False(CreateSut().CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_PlainTextEmail_YieldsSubjectAndBody()
    {
        using var stream = BuildEml("Hello World", textBody: "This is the body.");

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        // First section: subject
        Assert.Equal("Hello World", sections[0].Text);
        Assert.Equal(1, sections[0].HeadingLevel);
        // Second section: body
        Assert.Contains("This is the body.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_HtmlBodyNoPlainText_ParsesHtmlBody()
    {
        var html = "<html><body><h1>Title</h1><p>Body text.</p></body></html>";
        using var stream = BuildEml("Subject", htmlBody: html);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Subject + at least one HTML section
        Assert.True(sections.Count >= 2);
        Assert.Equal("Subject", sections[0].Text);
        Assert.Contains(sections.Skip(1), s => s.Text.Contains("Title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_WithTextAttachment_IncludesAttachmentSections()
    {
        var attachmentParser = Substitute.For<IDocumentParser>();
        attachmentParser.CanParse("text/plain").Returns(true);
        attachmentParser.ParseAsync(
                Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(YieldSection("attachment content", new DocumentId("email-1")));

        using var stream = BuildEml("Subject", textBody: "Body.", attachmentName: "notes.txt", attachmentMime: "text/plain", attachmentContent: "attachment content");

        var sections = await CreateSut(attachmentParser)
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(sections, s => s.Text.Contains("attachment content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_AttachmentWithNoParser_SkipsAttachment()
    {
        // No parser registered for application/pdf
        using var stream = BuildEml("Subject", textBody: "Body.", attachmentName: "file.pdf", attachmentMime: "application/pdf", attachmentContent: "PDF bytes");

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Subject + body only; no crash
        Assert.Equal(2, sections.Count);
    }

    [Fact]
    public async Task ParseAsync_EmptyBody_YieldsSubjectOnly()
    {
        using var stream = BuildEml("Only Subject", textBody: null);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Equal("Only Subject", sections[0].Text);
    }

    // Helpers

    private static MemoryStream BuildEml(
        string subject,
        string? textBody = null,
        string? htmlBody = null,
        string? attachmentName = null,
        string? attachmentMime = null,
        string? attachmentContent = null)
    {
        var message = new MimeMessage();
        message.Subject = subject;
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));

        var multipart = new Multipart("mixed");

        if (textBody is not null || htmlBody is not null)
        {
            var alternative = new MultipartAlternative();
            if (textBody is not null)
                alternative.Add(new TextPart("plain") { Text = textBody });
            if (htmlBody is not null)
                alternative.Add(new TextPart("html") { Text = htmlBody });
            multipart.Add(alternative.Count == 1 ? (MimeEntity)alternative[0] : alternative);
        }

        if (attachmentName is not null && attachmentContent is not null)
        {
            var contentTypeParts = (attachmentMime ?? "application/octet-stream").Split('/');
            var part = new MimePart(contentTypeParts[0], contentTypeParts.ElementAtOrDefault(1) ?? "octet-stream")
            {
                FileName = attachmentName,
                Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(attachmentContent)), ContentEncoding.Default),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            };
            multipart.Add(part);
        }

        message.Body = multipart.Count == 1 ? multipart[0] : multipart;

        var ms = new MemoryStream();
        message.WriteTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static async IAsyncEnumerable<DocumentSection> YieldSection(string text, DocumentId id)
    {
        await Task.Yield();
        yield return new DocumentSection { Text = text, DocumentId = id, SectionIndex = 0 };
    }
}
