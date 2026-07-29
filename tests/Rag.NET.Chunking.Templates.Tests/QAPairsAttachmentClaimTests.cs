using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Xunit;
using EmailPackageParser = Rag.NET.Parsers.Email.EmailDocumentParser;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// <c>QAPairsDocumentParser</c> reached through email attachment dispatch, end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// Phase 3.11 removed <c>application/octet-stream</c> from this parser's <c>CanParse</c>, and its
/// failure route — <c>Cannot resolve question/answer columns</c> — was pinned only by a
/// <c>CanParse</c> theory: nothing drove a real CSV through a real container. A theory over
/// <c>CanParse</c> cannot show that the claim the parser answers is the claim the dispatcher
/// routes on, nor that the parser throwing costs only its own attachment.
/// </para>
/// <para>
/// <c>UseQAPairsChunking()</c> and <c>AddEmailParser()</c> share no content type, so this pairing
/// stays a legal registration.
/// </para>
/// </remarks>
public sealed class QAPairsAttachmentClaimTests
{
    private const string BodyText = "This is the body text of the message.";
    private const string NotesText = "notes content";
    private const string ResolvableCsv = "question,answer\nWhat is RAG?,Retrieval augmented generation\n";
    private const string UnresolvableCsv = "column_one,column_two\nsomething,something else\n";

    private static readonly byte[] OpaqueBytes = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD, 0x7F, 0x80];

    /// <summary>
    /// The guard against over-correcting Task 2: dropping <c>application/octet-stream</c> must not
    /// have narrowed the legitimate <c>text/csv</c> claim, and a <c>payload.dat</c> in the same
    /// message must now be skipped rather than parsed as a guess.
    /// </summary>
    [Fact]
    public async Task ACsvAttachmentStillReachesTheQAPairsParser()
    {
        var sections = await ParseAsync(ResolvableCsv, TestContext.Current.CancellationToken);

        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
        Assert.Contains(sections, s =>
            s.Text.Contains("What is RAG?", StringComparison.Ordinal) &&
            string.Equals(s.Heading, "Retrieval augmented generation", StringComparison.Ordinal));
    }

    /// <summary>
    /// The failure route, end-to-end. <c>QAPairsDocumentParser</c> claims <c>text/csv</c>, so a CSV
    /// whose headers it cannot resolve is a parser that accepted an attachment and then threw —
    /// the shape the dispatcher's containment exists for, reached here through a real registration
    /// rather than through a purpose-built fake.
    /// </summary>
    [Fact]
    public async Task ACsvItCannotResolveCostsOnlyItsOwnAttachment()
    {
        var sections = await ParseAsync(UnresolvableCsv, TestContext.Current.CancellationToken);

        Assert.Contains(sections, s => s.Text.Contains(BodyText, StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains(NotesText, StringComparison.Ordinal));
        Assert.DoesNotContain(sections, s => s.Text.Contains("column_one", StringComparison.Ordinal));
    }

    /// <summary>
    /// The regression Task 2 fixed, pinned where it is still observable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The section assertions above cannot see this one. Containment means a parser that wrongly
    /// claims <c>application/octet-stream</c> and then throws costs only its own attachment, so the
    /// body and the siblings survive whether or not the claim was ever removed — the two states
    /// produce identical sections. Restoring the <c>application/octet-stream</c> clause in
    /// <c>QAPairsDocumentParser.CanParse</c> and re-running left both section-level tests green.
    /// </para>
    /// <para>
    /// What still differs is the reason <c>payload.dat</c> was skipped, and the dispatcher writes
    /// it: <c>NoParserForAttachment</c> when nothing claims the type, <c>AttachmentParserFailed</c>
    /// when something claimed it and threw. Asserting the first <i>and</i> the absence of the
    /// second is what fails against a reverted Task 2.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOctetStreamAttachmentIsUnclaimedRatherThanMisclaimed()
    {
        using var logs = new CapturingLoggerProvider();
        await ParseAsync(ResolvableCsv, logs, TestContext.Current.CancellationToken);

        Assert.True(
            logs.Contains("No parser registered for attachment content type application/octet-stream"),
            $"payload.dat should have been skipped as unclaimed. Log was:\n{string.Join("\n", logs.Messages)}");
        Assert.False(
            logs.Contains("failed on attachment 'payload.dat'"),
            $"payload.dat was claimed by a parser that then threw. Log was:\n{string.Join("\n", logs.Messages)}");
    }

    private static Task<IReadOnlyList<DocumentSection>> ParseAsync(
        string csv,
        CancellationToken cancellationToken) =>
        ParseAsync(csv, loggerProvider: null, cancellationToken);

    private static async Task<IReadOnlyList<DocumentSection>> ParseAsync(
        string csv,
        CapturingLoggerProvider? loggerProvider,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        if (loggerProvider is not null)
            services.AddLogging(logging => logging.AddProvider(loggerProvider));

        services.AddRagNet(builder =>
        {
            builder.UseQAPairsChunking();
            builder.AddEmailParser();
        });

        await using var provider = services.BuildServiceProvider();
        var parser = provider.GetServices<IDocumentParser>().OfType<EmailPackageParser>().Single();

        using var stream = new MemoryStream(await CreateEmlAsync(csv, cancellationToken));
        return await parser.ParseAsync(stream, CreateMetadata(), cancellationToken).ToListAsync(cancellationToken);
    }

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("eml-qapairs-1"),
        FileName = "message.eml",
        ContentType = "message/rfc822",
    };

    private static async Task<byte[]> CreateEmlAsync(string csv, CancellationToken cancellationToken)
    {
        var body = new BodyBuilder { TextBody = BodyText };
        body.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes(NotesText), ContentType.Parse("text/plain"));
        body.Attachments.Add("pairs.csv", Encoding.UTF8.GetBytes(csv), ContentType.Parse("text/csv"));
        body.Attachments.Add("payload.dat", OpaqueBytes, ContentType.Parse("application/octet-stream"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "QA pairs attachment";
        message.Body = body.ToMessageBody();

        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
