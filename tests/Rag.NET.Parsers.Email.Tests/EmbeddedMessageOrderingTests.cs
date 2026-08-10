using System.Text;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Pins the <b>exact</b> order in which both parsers emit sections for a message that is
/// deliberately wide as well as deep.
/// </summary>
/// <remarks>
/// <para>
/// Written before the traversal was flattened, and green against the recursive parsers, so it
/// records behaviour rather than transcribing it. Any difference after the flattening is a bug,
/// not an accepted change.
/// </para>
/// <para>
/// The assertion is on the sequence, not on a count: a count assertion passes while depth-first
/// quietly becomes breadth-first, which is the one thing an explicit stack can plausibly get
/// wrong. The fixture needs a sibling <i>after</i> a descent for the two to differ at all —
/// a single-descent-per-level fixture is emitted identically by both.
/// </para>
/// </remarks>
public class EmbeddedMessageOrderingTests
{
    /// <summary>
    /// <code>
    /// root                 "Root Subject" / "root body"
    ///   attachment A
    ///   attachment C
    ///   embedded child 1   "Child 1" / "child 1 body"
    ///       attachment B
    ///       embedded grandchild
    ///   embedded child 2   "Child 2" / "child 2 body"
    /// </code>
    /// File attachments precede embedded messages at every level because that is the order both
    /// fixture builders write them in, and both parsers emit attachments in document order.
    /// </summary>
    private static readonly string[] ExpectedSequence =
    [
        "Root Subject",
        "root body",
        "A",
        "C",
        "Child 1",
        "child 1 body",
        "B",
        "Grandchild",
        "grandchild body",
        "Child 2",
        "child 2 body",
    ];

    [Fact]
    public async Task Eml_MultiBranchFixture_EmitsThisExactSectionSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new OrderingHarness();

        using var stream = new MemoryStream(await BuildEmlAsync(ct));
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata("root.eml"), ct).ToListAsync(ct);

        AssertSequence(sections);
        Assert.Empty(harness.Warnings);
    }

    [Fact]
    public async Task Msg_MultiBranchFixture_EmitsThisExactSectionSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new OrderingHarness();

        using var stream = BuildMsg();
        var sections = await harness.Msg.ParseAsync(stream, CreateMetadata("root.msg"), ct).ToListAsync(ct);

        AssertSequence(sections);
        Assert.Empty(harness.Warnings);
    }

    private static void AssertSequence(IReadOnlyList<DocumentSection> sections)
    {
        Assert.Equal(ExpectedSequence, sections.Select(s => s.Text).ToArray());

        // SectionIndex is stamped exactly once, in ParseAsync, so it is contiguous 0..n-1 in
        // emission order no matter how deep the section came from.
        Assert.Equal(
            Enumerable.Range(0, ExpectedSequence.Length).ToArray(),
            sections.Select(s => s.SectionIndex).ToArray());
    }

    private static DocumentMetadata CreateMetadata(string fileName) => new()
    {
        DocumentId = new DocumentId("ordering-1"),
        FileName = fileName,
        Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
    };

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static async Task<byte[]> BuildEmlAsync(CancellationToken cancellationToken)
    {
        var grandchild = EmlFixtureBuilder.CreateNested("Grandchild", "grandchild body");
        var child1 = EmlFixtureBuilder.CreateNested("Child 1", "child 1 body", grandchild, [EmlAttachment("B")]);
        var child2 = EmlFixtureBuilder.CreateNested("Child 2", "child 2 body");
        var root = EmlFixtureBuilder.CreateBranching(
            "Root Subject", "root body", [EmlAttachment("A"), EmlAttachment("C")], child1, child2);

        return await EmlFixtureBuilder.SerializeAsync(root, cancellationToken);
    }

    private static MemoryStream BuildMsg() =>
        MsgFixtureBuilder.CreateTree(new MsgFixtureBuilder.MsgNode
        {
            Subject = "Root Subject",
            BodyText = "root body",
            Attachments = [MsgAttachment("A"), MsgAttachment("C")],
            Embedded =
            [
                new MsgFixtureBuilder.MsgNode
                {
                    Subject = "Child 1",
                    BodyText = "child 1 body",
                    Attachments = [MsgAttachment("B")],
                    Embedded = [new MsgFixtureBuilder.MsgNode { Subject = "Grandchild", BodyText = "grandchild body" }],
                },
                new MsgFixtureBuilder.MsgNode { Subject = "Child 2", BodyText = "child 2 body" },
            ],
        });

    private static (string FileName, string ContentType, byte[] Data) EmlAttachment(string text) =>
        ($"{text}.txt", "text/plain", Encoding.UTF8.GetBytes(text));

    private static (string FileName, byte[] Data, string? MimeType) MsgAttachment(string text) =>
        ($"{text}.txt", Encoding.UTF8.GetBytes(text), "text/plain");

    /// <summary>An EML and an MSG parser sharing one parser list, options and warning sink.</summary>
    private sealed class OrderingHarness
    {
        private readonly CapturingLogger<EmailDocumentParser> emlLog = new();
        private readonly CapturingLogger<MsgDocumentParser> msgLog = new();

        public OrderingHarness()
        {
            // Both limits are set deliberately rather than left at their defaults: at the default
            // MaxEmbeddedMessages of 50 a fixture can stop on the fan-out cap while the test
            // believes it stopped on the depth ceiling, which is how an earlier probe in this
            // area measured the wrong bound. This fixture is 2 deep and 3 messages wide.
            var options = new EmailParserOptions { MaxEmbeddedDepth = 3, MaxEmbeddedMessages = 10 };
            var parsers = new List<IDocumentParser>();
            Eml = new EmailDocumentParser(parsers, new HtmlDocumentParser(), emlLog, options);
            Msg = new MsgDocumentParser(parsers, new HtmlDocumentParser(), msgLog, options);
            parsers.Add(Eml);
            parsers.Add(Msg);
            parsers.Add(new FakeTextParser());
        }

        public EmailDocumentParser Eml { get; }

        public MsgDocumentParser Msg { get; }

        public IEnumerable<string> Warnings =>
            emlLog.Entries.Concat(msgLog.Entries)
                .Where(e => e.Level == LogLevel.Warning)
                .Select(e => e.Message);
    }
}
