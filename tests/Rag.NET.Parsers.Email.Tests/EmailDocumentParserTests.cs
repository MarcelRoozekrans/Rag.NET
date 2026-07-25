using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

public class EmailDocumentParserTests
{
    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("eml-1"),
        FileName = "test.eml",
        ContentType = "message/rfc822",
    };

    // ── CanParse ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("message/rfc822", true)]
    [InlineData("MESSAGE/RFC822", true)]
    [InlineData("application/vnd.ms-outlook", false)]
    [InlineData("text/html", false)]
    [InlineData("application/pdf", false)]
    public void CanParse_Matrix(string contentType, bool expected)
    {
        var sut = new EmailDocumentParser([], new HtmlDocumentParser());
        Assert.Equal(expected, sut.CanParse(contentType));
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_SubjectAndTextBody_Sections()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EmailDocumentParser([], new HtmlDocumentParser());
        using var stream = await CreateEmlAsync("Quarterly Report", "Please find the numbers below.", htmlBody: null, [], ct);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Quarterly Report", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Equal("Quarterly Report", sections[0].Text);
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal("Please find the numbers below.", sections[1].Text);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task Parse_HtmlBody_DelegatesToHtml()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EmailDocumentParser([], new HtmlDocumentParser());
        using var stream = await CreateEmlAsync(
            "HTML Mail", textBody: null, "<h1>Announcement</h1><p>Details inside.</p>", [], ct);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Announcement", sections[1].Heading);
        Assert.Contains("Details inside.", sections[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_TextAttachment_DispatchedToTextParser()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EmailDocumentParser([new FakeTextParser()], new HtmlDocumentParser());
        using var stream = await CreateEmlAsync(
            "With Attachment", "See attached.", htmlBody: null,
            [("notes.txt", "text/plain", Encoding.UTF8.GetBytes("Attached note content."))], ct);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Attached note content.", sections[2].Text);
        Assert.Equal(2, sections[2].SectionIndex);
    }

    [Fact]
    public async Task Parse_UnparseableAttachment_WarnsAndSkips()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser([new FakeTextParser()], new HtmlDocumentParser(), logger);
        using var stream = await CreateEmlAsync(
            "Binary Attachment", "Body here.", htmlBody: null,
            [("data.bin", "application/octet-stream", [0x01, 0x02, 0x03])], ct);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count); // subject + body only
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("application/octet-stream", warning.Message, StringComparison.Ordinal);
        Assert.Contains("data.bin", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_MultipartAlternative_PrefersTextOverHtml()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EmailDocumentParser([], new HtmlDocumentParser());
        using var stream = await CreateEmlAsync(
            "Alternative", "Plain text wins.", "<h1>Html Heading</h1><p>Html loses.</p>", [], ct);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Plain text wins.", sections[1].Text);
        Assert.DoesNotContain(sections, s => string.Equals(s.Heading, "Html Heading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parse_EmbeddedMessageAttachment_WarnsAndSkips()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser([new FakeTextParser()], new HtmlDocumentParser(), logger);
        using var stream = await CreateEmlWithEmbeddedMessageAsync(
            "Outer", "Outer body.", "Forwarded Subject", "Forwarded body.", ct);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count); // subject + body only
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Forwarded Subject", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not yet recursed", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_TextAttachment_MetadataContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeTextParser();
        var sut = new EmailDocumentParser([fake], new HtmlDocumentParser());
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("eml-1"),
            FileName = "test.eml",
            ContentType = "message/rfc822",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "unit-test" },
        };
        using var stream = await CreateEmlAsync(
            "With Attachment", "See attached.", htmlBody: null,
            [("notes.txt", "text/plain", Encoding.UTF8.GetBytes("Note."))], ct);

        _ = await sut.ParseAsync(stream, metadata, ct).ToListAsync(ct);

        var received = Assert.Single(fake.ReceivedMetadata);
        Assert.Equal("notes.txt", received.FileName);
        Assert.Equal("text/plain", received.ContentType);
        Assert.Equal(metadata.DocumentId, received.DocumentId);
        Assert.NotSame(metadata.Tags, received.Tags); // copied, not shared
        Assert.Equal("unit-test", received.Tags["source"]);
    }

    // ── DI ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AddEmailParser_RegistersBothParsersAndHtmlDependency()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddEmailParser();

        using var provider = services.BuildServiceProvider();
        var parsers = provider.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is EmailDocumentParser);
        Assert.Contains(parsers, p => p is MsgDocumentParser);
        Assert.NotNull(provider.GetService<HtmlDocumentParser>());
    }

    // ── EML fixture builder ──────────────────────────────────────────────────

    private static async Task<MemoryStream> CreateEmlAsync(
        string? subject,
        string? textBody,
        string? htmlBody,
        (string FileName, string ContentType, byte[] Data)[] attachments,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        if (subject is not null)
            message.Subject = subject;

        var builder = new BodyBuilder();
        if (textBody is not null)
            builder.TextBody = textBody;
        if (htmlBody is not null)
            builder.HtmlBody = htmlBody;
        foreach (var (fileName, contentType, data) in attachments)
        {
            builder.Attachments.Add(fileName, data, ContentType.Parse(contentType));
        }

        message.Body = builder.ToMessageBody();

        return await WriteToStreamAsync(message, cancellationToken);
    }

    private static async Task<MemoryStream> CreateEmlWithEmbeddedMessageAsync(
        string subject,
        string textBody,
        string nestedSubject,
        string nestedBody,
        CancellationToken cancellationToken)
    {
        var nested = new MimeMessage();
        nested.From.Add(new MailboxAddress("Original Sender", "original@example.com"));
        nested.Subject = nestedSubject;
        nested.Body = new TextPart("plain") { Text = nestedBody };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = subject;
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = textBody },
            new MessagePart
            {
                Message = nested,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        return await WriteToStreamAsync(message, cancellationToken);
    }

    private static async Task<MemoryStream> WriteToStreamAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }
}
