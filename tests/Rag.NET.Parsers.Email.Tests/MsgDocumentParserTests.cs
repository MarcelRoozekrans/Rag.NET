using System.Text;
using Microsoft.Extensions.Logging;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

public class MsgDocumentParserTests
{
    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("msg-1"),
        FileName = "test.msg",
        ContentType = "application/vnd.ms-outlook",
    };

    // ── CanParse ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/vnd.ms-outlook", true)]
    [InlineData("APPLICATION/VND.MS-OUTLOOK", true)]
    [InlineData("message/rfc822", false)]
    [InlineData("application/pdf", false)]
    public void CanParse_Matrix(string contentType, bool expected)
    {
        var sut = new MsgDocumentParser([], new HtmlDocumentParser());
        Assert.Equal(expected, sut.CanParse(contentType));
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_Msg_SubjectBodyAttachments()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new MsgDocumentParser([new FakeTextParser()], new HtmlDocumentParser());
        using var stream = MsgFixtureBuilder.Create(
            "Status Update",
            "All systems operational.",
            attachments: [("log.txt", Encoding.UTF8.GetBytes("Attachment log line."), null)]);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Status Update", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal("All systems operational.", sections[1].Text);
        Assert.Equal(1, sections[1].SectionIndex);
        // "log.txt" carries no MIME type in the fixture → inferred as text/plain
        // from the extension and dispatched to the registered fake text parser.
        Assert.Equal("Attachment log line.", sections[2].Text);
        Assert.Equal(2, sections[2].SectionIndex);
    }

    [Fact]
    public async Task Parse_MsgAttachment_ReEntersItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<MsgDocumentParser>();
        var parsers = new List<Rag.NET.Abstractions.IDocumentParser>();
        // The parser is the only one that CanParse a nested .msg, so it re-enters itself.
        // That used to be blocked by a ReferenceEquals(self) skip; the depth limit replaced it.
        var sut = new MsgDocumentParser(parsers, new HtmlDocumentParser(), logger);
        parsers.Add(sut);

        using var nested = MsgFixtureBuilder.Create("Nested", "Nested body.");
        using var stream = MsgFixtureBuilder.Create(
            "Outer", "Outer body.", attachments: [("nested.msg", nested.ToArray(), null)]);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(4, sections.Count);
        Assert.Equal("Nested", sections[2].Heading);
        Assert.Equal("Nested body.", sections[3].Text);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Parse_Msg_HtmlBodyOnly_DelegatesToHtml()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new MsgDocumentParser([], new HtmlDocumentParser());
        using var stream = MsgFixtureBuilder.Create(
            "Html Mail",
            bodyText: null,
            bodyHtml: "<html><body><h1>Announcement</h1><p>Details inside.</p></body></html>");

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Announcement", sections[1].Heading);
        Assert.Contains("Details inside.", sections[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_Msg_AttachmentWithExplicitMimeType_UsesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeTextParser();
        var sut = new MsgDocumentParser([fake], new HtmlDocumentParser());
        // ".xyz" maps to nothing in the extension map — dispatch must come from the
        // explicit PidTagAttachMimeTag written into the fixture.
        using var stream = MsgFixtureBuilder.Create(
            "Tagged", "Body.",
            attachments: [("data.xyz", Encoding.UTF8.GetBytes("Tagged content."), "text/plain")]);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Tagged content.", sections[2].Text);
        Assert.Equal("text/plain", Assert.Single(fake.ReceivedMetadata).ContentType);
    }

    // Nested Storage.Message recursion is covered by EmbeddedMessageRecursionTests.

    [Fact]
    public async Task Parse_NoBody_EmitsSubjectOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new MsgDocumentParser([], new HtmlDocumentParser());
        using var stream = MsgFixtureBuilder.Create("Subject Only", bodyText: null);

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        var section = Assert.Single(sections);
        Assert.Equal("Subject Only", section.Heading);
    }
}
