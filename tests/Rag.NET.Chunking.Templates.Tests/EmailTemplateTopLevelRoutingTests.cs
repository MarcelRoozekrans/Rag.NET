using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// <see cref="EmailTemplateDocumentParser"/> reached through top-level ingestion, which design §1
/// named as a second route to the <c>application/octet-stream</c> defect and which nothing else in
/// Phase 3.11 covered.
/// </summary>
/// <remarks>
/// <para>
/// Removing <c>application/octet-stream</c> from this parser's <c>CanParse</c> was pinned by a
/// <c>CanParse</c> theory and nothing else. <see cref="QAPairsAttachmentClaimTests"/>' own header
/// argues that a theory over <c>CanParse</c> cannot show that the claim the parser answers is the
/// claim the dispatcher routes on; the argument applies here and was not followed through.
/// </para>
/// <para>
/// It cannot be followed through by the same route. Registering this parser alongside
/// <c>AddEmailParser()</c> is a startup error now, so there is no container in which its
/// attachment dispatch can be observed. Top-level ingestion is the reachable surface:
/// <see cref="ParseBehavior"/> takes the first registered parser whose <c>CanParse</c> matches and
/// throws <see cref="NoParserFoundException"/> when none does — no containment, by design, because
/// a document the caller passed by name should fail loudly rather than index nothing.
/// </para>
/// </remarks>
public sealed class EmailTemplateTopLevelRoutingTests
{
    private const string OctetStream = "application/octet-stream";
    private const string EmlContentType = "message/rfc822";

    private static readonly byte[] OpaqueBytes = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD, 0x7F, 0x80];

    /// <summary>
    /// The regression Phase 3.11 fixed, at top level. The parser is registered and reachable — the
    /// assertion below says so — and must still not be the one an unknown binary is handed to.
    /// A wrong guess here is an exception out of the whole document rather than a miss.
    /// </summary>
    [Fact]
    public async Task AnOctetStreamDocumentIsNotRoutedToTheEmailTemplateParser()
    {
        using var provider = BuildProvider();

        Assert.Contains(
            provider.GetServices<IDocumentParser>(), p => p is EmailTemplateDocumentParser);

        var behavior = provider.GetRequiredService<ParseBehavior>();
        var context = CreateContext(OctetStream, "payload.dat", OpaqueBytes);

        var ex = await Assert.ThrowsAsync<NoParserFoundException>(
            () => behavior.HandleAsync(context, TestContext.Current.CancellationToken, StubNext).AsTask());

        Assert.Contains(OctetStream, ex.Message, StringComparison.Ordinal);
        Assert.Empty(context.Sections);
    }

    /// <summary>
    /// The guard against over-correcting: the type this parser legitimately claims must still
    /// reach it through the same path.
    /// </summary>
    [Fact]
    public async Task AnEmlDocumentStillReachesTheEmailTemplateParser()
    {
        using var provider = BuildProvider();

        var behavior = provider.GetRequiredService<ParseBehavior>();
        var context = CreateContext(EmlContentType, "message.eml", CreateEml());

        await behavior.HandleAsync(context, TestContext.Current.CancellationToken, StubNext);

        Assert.Contains(
            context.Sections,
            s => string.Equals(s.Heading, "body", StringComparison.Ordinal) &&
                 s.Text.Contains("plain body text", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>UseEmailChunking()</c> alone, because pairing it with <c>AddEmailParser()</c> is a
    /// startup error and the point is to observe this parser doing the routing.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddRagNet(builder => builder.UseEmailChunking());
        return services.BuildServiceProvider();
    }

    private static IngestionContext CreateContext(string contentType, string fileName, byte[] content) =>
        new()
        {
            Stream = new MemoryStream(content),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("top-level-1"),
                FileName = fileName,
                ContentType = contentType,
            },
            GetNextBm25DocId = () => 0,
        };

    private static ValueTask<IngestionResult> StubNext(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    /// <summary>
    /// Written by hand rather than through MimeKit: the assertion is about which parser ran, and a
    /// four-header message with a plain body is enough to tell.
    /// </summary>
    private static byte[] CreateEml() => System.Text.Encoding.UTF8.GetBytes(
        "From: sender@example.com\r\n" +
        "To: recipient@example.com\r\n" +
        "Subject: Top level routing\r\n" +
        "Date: Wed, 29 Jul 2026 12:00:00 +0000\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "plain body text\r\n");
}
