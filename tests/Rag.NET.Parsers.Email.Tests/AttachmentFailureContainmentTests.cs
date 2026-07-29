using System.Text;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// A parser that accepts an attachment's content type and then throws must cost only that
/// attachment.
/// </summary>
/// <remarks>
/// Phase 3.11 removed <c>application/octet-stream</c> from the two <c>Rag.NET.Chunking.Templates</c>
/// parsers, which fixes the collision that was actually shipping. It does nothing about the next
/// parser that claims a type and then fails on it — including a third-party one the email package
/// only sees through <see cref="IDocumentParser"/>. These tests pin the containment rather than the
/// particular parser that motivated it.
/// </remarks>
public sealed class AttachmentFailureContainmentTests
{
    private const string BodyText = "Body that must survive the failing attachment.";
    private const string NotesText = "Sibling attachment content.";
    private const string FailingContentType = "application/x-throws";
    private const string FailingAttachment = "payload.throws";

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("eml-containment-1"),
        FileName = "message.eml",
        ContentType = "message/rfc822",
    };

    /// <summary>
    /// The case a naive implementation gets wrong. Containing the failure by wrapping the whole
    /// <c>await foreach</c> — or by buffering the attachment's sections and discarding them on
    /// failure — loses everything the parser produced before it threw. Written first for that
    /// reason.
    /// </summary>
    [Fact]
    public async Task AParserThatThrowsAfterYieldingKeepsTheSectionsItAlreadyYielded()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser(
            [new FakeTextParser(), new ThrowingDocumentParser(FailingContentType, sectionsBeforeThrow: 2)],
            new HtmlDocumentParser(),
            logger);
        using var stream = new MemoryStream(await CreateEmlAsync(ct));

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count(s => string.Equals(s.Text, ThrowingDocumentParser.YieldedText, StringComparison.Ordinal)));
        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThrowingAttachmentParserCostsOnlyItsOwnAttachment()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser(
            [new FakeTextParser(), new ThrowingDocumentParser(FailingContentType)],
            new HtmlDocumentParser(),
            logger);
        using var stream = new MemoryStream(await CreateEmlAsync(ct));

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
        Assert.DoesNotContain(sections, s => string.Equals(s.Text, ThrowingDocumentParser.YieldedText, StringComparison.Ordinal));
    }

    /// <summary>
    /// The attachment alone does not say which registration is at fault, so the warning names the
    /// parser too. Asserted by its parts rather than by its prose.
    /// </summary>
    [Fact]
    public async Task TheWarningNamesTheAttachmentAndTheParser()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser(
            [new FakeTextParser(), new ThrowingDocumentParser(FailingContentType)],
            new HtmlDocumentParser(),
            logger);
        using var stream = new MemoryStream(await CreateEmlAsync(ct));

        _ = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(FailingAttachment, warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingDocumentParser), warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Containment must not turn a cancelled parse into a quietly partial one:
    /// <see cref="OperationCanceledException"/> is rethrown rather than logged and skipped.
    /// </summary>
    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser(
            [
                new FakeTextParser(),
                new ThrowingDocumentParser(
                    FailingContentType, sectionsBeforeThrow: 0, () => new OperationCanceledException()),
            ],
            new HtmlDocumentParser(),
            logger);
        using var stream = new MemoryStream(await CreateEmlAsync(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A <c>ParseAsync</c> written as an ordinary method rather than as an iterator throws when it
    /// is called, before the dispatcher ever reaches <c>MoveNextAsync</c>. Containment covers that
    /// route too — nothing in <see cref="IDocumentParser"/> requires an iterator.
    /// </summary>
    [Fact]
    public async Task AParserThatThrowsBeforeReturningAnEnumerableIsContained()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<EmailDocumentParser>();
        var sut = new EmailDocumentParser(
            [new FakeTextParser(), new EagerlyThrowingDocumentParser(FailingContentType)],
            new HtmlDocumentParser(),
            logger);
        using var stream = new MemoryStream(await CreateEmlAsync(ct));

        var sections = await sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(EagerlyThrowingDocumentParser), warning.Message, StringComparison.Ordinal);
    }

    private static Task<byte[]> CreateEmlAsync(CancellationToken cancellationToken) =>
        EmlFixtureBuilder.CreateAsync(
            "Containment",
            BodyText,
            [
                ("notes.txt", "text/plain", Encoding.UTF8.GetBytes(NotesText)),
                (FailingAttachment, FailingContentType, [0x01, 0x02, 0x03]),
            ],
            cancellationToken);
}
