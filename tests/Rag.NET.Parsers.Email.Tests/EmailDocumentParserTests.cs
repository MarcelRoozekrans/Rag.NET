using System.Globalization;
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

    // MessagePart recursion is covered by EmbeddedMessageRecursionTests.

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
            Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "unit-test" },
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
        Assert.Null(provider.GetService<EmailParserOptions>()); // only the configuring overload registers them
    }

    [Fact]
    public async Task AddEmailParser_Configured_OptionsReachBothParserInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddEmailParser(o => o.MaxEmbeddedDepth = 0);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(0, provider.GetRequiredService<EmailParserOptions>().MaxEmbeddedDepth);
        var parsers = provider.GetServices<IDocumentParser>().ToList();

        // Recursion off is observable: each parser yields subject + body only for a message
        // that embeds another. Asserting it on both pins that both instances got the options.
        var eml = Assert.Single(parsers.OfType<EmailDocumentParser>());
        var nested = EmlFixtureBuilder.CreateNested("Forwarded", "Forwarded body.");
        using var emlStream = new MemoryStream(
            await EmlFixtureBuilder.CreateWithEmbeddedAsync("Outer", "Outer body.", nested, ct));
        Assert.Equal(2, await eml.ParseAsync(emlStream, CreateMetadata(), ct).CountAsync(ct));

        var msg = Assert.Single(parsers.OfType<MsgDocumentParser>());
        using var msgStream = MsgFixtureBuilder.Create("Outer", "Outer body.",
            embeddedMessageSubject: "Forwarded", embeddedMessageBody: "Forwarded body.");
        Assert.Equal(2, await msg.ParseAsync(msgStream, CreateMetadata(), ct).CountAsync(ct));
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(3, -1)]
    [InlineData(EmailParserOptions.MaxSupportedEmbeddedDepth + 1, 50)]
    public void AddEmailParser_InvalidOptions_Throws(int maxDepth, int maxMessages)
    {
        var builder = new RagBuilder(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddEmailParser(o =>
        {
            o.MaxEmbeddedDepth = maxDepth;
            o.MaxEmbeddedMessages = maxMessages;
        }));
    }

    /// <summary>
    /// Pinned loosely on purpose. This message rotted unnoticed — it went on asserting the
    /// traversal was stack-recursive for a whole phase after Phase 3.9 flattened it — precisely
    /// because no test touched it. An exact-prose assertion would only rot differently, so what
    /// is held here is what a caller reading a stack trace actually needs: which argument was
    /// rejected, what value they passed, and the ceiling they have to come down to.
    /// </summary>
    [Fact]
    public void AddEmailParser_DepthAboveTheCeiling_ThrowsNamingTheCeiling()
    {
        var builder = new RagBuilder(new ServiceCollection());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddEmailParser(
            o => o.MaxEmbeddedDepth = EmailParserOptions.MaxSupportedEmbeddedDepth + 1));

        Assert.Equal("configure", ex.ParamName);
        Assert.Equal(EmailParserOptions.MaxSupportedEmbeddedDepth + 1, ex.ActualValue);
        Assert.Contains(nameof(EmailParserOptions.MaxEmbeddedDepth), ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            EmailParserOptions.MaxSupportedEmbeddedDepth.ToString(CultureInfo.InvariantCulture),
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddEmailParser_DepthAtTheCeiling_IsAccepted()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        // The ceiling bounds the dispatcher path — a third-party parser registered for a message
        // content type re-enters through IDocumentParser — plus per-document fan-out. The in-place
        // traversal is flat. 64 must be the last accepted value, 65 the first rejected.
        builder.AddEmailParser(o => o.MaxEmbeddedDepth = EmailParserOptions.MaxSupportedEmbeddedDepth);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(64, provider.GetRequiredService<EmailParserOptions>().MaxEmbeddedDepth);
    }

    // ── The ceiling is absolute, not merely validated ────────────────────────

    /// <summary>
    /// <c>AddEmailParser</c> is only one of three ways to reach an <see cref="EmailParserOptions"/>:
    /// both parsers take one on a public constructor, and the instance registered in DI is the
    /// same one both parsers captured. The setter therefore clamps, so no path can arm a depth
    /// above <see cref="EmailParserOptions.MaxSupportedEmbeddedDepth"/> — the bound on re-entry
    /// through the dispatcher and on per-document fan-out. These are setter assertions; nothing
    /// here parses a message.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(EmailParserOptions.MaxSupportedEmbeddedDepth, EmailParserOptions.MaxSupportedEmbeddedDepth)]
    [InlineData(EmailParserOptions.MaxSupportedEmbeddedDepth + 1, EmailParserOptions.MaxSupportedEmbeddedDepth)]
    [InlineData(10_000, EmailParserOptions.MaxSupportedEmbeddedDepth)]
    [InlineData(int.MaxValue, EmailParserOptions.MaxSupportedEmbeddedDepth)]
    public void MaxEmbeddedDepth_Setter_ClampsToTheCeiling(int assigned, int expected)
    {
        var options = new EmailParserOptions { MaxEmbeddedDepth = assigned };

        Assert.Equal(expected, options.MaxEmbeddedDepth);
    }

    [Fact]
    public void MaxEmbeddedDepth_DirectConstruction_CannotExceedTheCeiling()
    {
        // The bypass: EmailDocumentParser/MsgDocumentParser have public constructors taking the
        // options, so ValidateOptions never runs on this instance.
        var options = new EmailParserOptions { MaxEmbeddedDepth = 10_000 };
        var parsers = new List<IDocumentParser>();
        var parser = new EmailDocumentParser(parsers, new HtmlDocumentParser(), null, options);
        parsers.Add(parser);

        Assert.Equal(EmailParserOptions.MaxSupportedEmbeddedDepth, options.MaxEmbeddedDepth);
    }

    [Fact]
    public void MaxEmbeddedDepth_MutatingTheDiResolvedInstance_CannotExceedTheCeiling()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);
        builder.AddEmailParser(o => o.MaxEmbeddedDepth = 3);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<EmailParserOptions>();

        // The second bypass: Register puts the very instance both parsers captured into DI, so
        // a post-validation write would re-arm what startup validation had just rejected.
        options.MaxEmbeddedDepth = 10_000;

        Assert.Equal(EmailParserOptions.MaxSupportedEmbeddedDepth, options.MaxEmbeddedDepth);
    }

    [Fact]
    public void MaxEmbeddedDepth_AboveTheCeiling_StillThrowsFromAddEmailParser()
    {
        // Clamping must not silence the startup check: the clamp keeps the process alive, the
        // throw keeps the misconfiguration loud. Both hold at once because the setter remembers
        // the unclamped request for ValidateOptions to read.
        var builder = new RagBuilder(new ServiceCollection());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddEmailParser(o => o.MaxEmbeddedDepth = 10_000));

        Assert.Equal(10_000, ex.ActualValue); // the value the caller asked for, not the clamp
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

    private static async Task<MemoryStream> WriteToStreamAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }
}
