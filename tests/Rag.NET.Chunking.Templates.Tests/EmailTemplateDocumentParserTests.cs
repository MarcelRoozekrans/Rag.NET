using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using System.Text;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class EmailTemplateDocumentParserTests
{
    private const string SimpleEml = """
        From: sender@example.com
        To: recipient@example.com
        Subject: Hello World
        Date: Tue, 01 Apr 2026 12:00:00 +0000
        MIME-Version: 1.0
        Content-Type: text/plain; charset=utf-8

        This is the body of the email. It contains important information.
        """;

    private static readonly DocumentMetadata Metadata =
        new() { DocumentId = new DocumentId("email.eml"), FileName = "email.eml" };

    [Fact]
    public async Task ParseAsync_EmitsBodySection()
    {
        var parser = new EmailTemplateDocumentParser(new EmailChunkingOptions { IncludeHeaders = false });
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleEml));

        var sections = new List<DocumentSection>();
        await foreach (var s in parser.ParseAsync(stream, Metadata, TestContext.Current.CancellationToken))
            sections.Add(s);

        Assert.Contains(sections, s => s.Text.Contains("body of the email", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_EmitsHeadersSectionWhenEnabled()
    {
        var parser = new EmailTemplateDocumentParser(new EmailChunkingOptions { IncludeHeaders = true });
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleEml));

        var sections = new List<DocumentSection>();
        await foreach (var s in parser.ParseAsync(stream, Metadata, TestContext.Current.CancellationToken))
            sections.Add(s);

        Assert.Contains(sections, s => s.Text.Contains("sender@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_CanParseMessageRfc822()
    {
        var parser = new EmailTemplateDocumentParser(new EmailChunkingOptions());
        Assert.True(parser.CanParse("message/rfc822"));
    }

    /// <summary>
    /// The legitimate claim survives Phase 3.11 and the greedy one does not.
    /// <c>application/octet-stream</c> is the fallback for "unknown binary", so a format-specific
    /// parser answering it is always guessing — and a wrong guess here threw
    /// <c>InvalidOperationException</c> out of the caller's whole document parse rather than
    /// merely missing one attachment. The <c>false</c> row is the fix; the <c>true</c> row is the
    /// guard against over-correcting it into a narrower parser.
    /// </summary>
    [Theory]
    [InlineData("message/rfc822", true)]
    [InlineData("application/octet-stream", false)]
    public void CanParse_ClaimsRfc822ButNotUnknownBinary(string contentType, bool expected)
    {
        var parser = new EmailTemplateDocumentParser(new EmailChunkingOptions());
        Assert.Equal(expected, parser.CanParse(contentType));
    }

    [Fact]
    public async Task ParseAsync_HeadersSectionMarkedWithHeading()
    {
        var parser = new EmailTemplateDocumentParser(new EmailChunkingOptions { IncludeHeaders = true });
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleEml));

        var sections = new List<DocumentSection>();
        await foreach (var s in parser.ParseAsync(stream, Metadata, TestContext.Current.CancellationToken))
            sections.Add(s);

        Assert.Contains(sections, s =>
            s.Heading != null &&
            string.Equals(s.Heading, "headers", StringComparison.Ordinal));
    }
}
