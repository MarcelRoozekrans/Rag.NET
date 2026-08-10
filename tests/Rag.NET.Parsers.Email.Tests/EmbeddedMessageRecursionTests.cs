using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Covers the depth/node bounds on embedded-message recursion, including the alternating
/// <c>.eml</c> → <c>.msg</c> → <c>.eml</c> chain that the old <c>ReferenceEquals(self)</c>
/// skip never caught: consecutive levels of that chain are handled by two <i>different</i>
/// parser instances, so the skip never fired and nothing bounded the chain.
/// </summary>
public class EmbeddedMessageRecursionTests
{
    private const string InnermostMarker = "INNERMOST-CHAIN-MARKER";

    private static DocumentMetadata CreateMetadata(IDictionary<string, MetadataValue>? tags = null) => new()
    {
        DocumentId = new DocumentId("chain-1"),
        FileName = "outer.eml",
        ContentType = "message/rfc822",
        Tags = tags ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
    };

    // ── The alternating chain (Task C1) ──────────────────────────────────────

    [Fact]
    public async Task AlternatingEmlMsgChain_StopsAtMaxEmbeddedDepth()
    {
        var ct = TestContext.Current.CancellationToken;
        var outer = await BuildAlternatingChainAsync(10, ct);
        var harness = new ParserHarness();

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        // Bounded: the top-level message plus MaxEmbeddedDepth nested ones, two sections each.
        // Without the depth limit all ten levels were traversed instead.
        Assert.Equal(2 * (harness.Options.MaxEmbeddedDepth + 1), sections.Count);
        Assert.DoesNotContain(sections, s => string.Equals(s.Text, InnermostMarker, StringComparison.Ordinal));
        Assert.Contains(harness.Warnings, w => w.Contains("maximum embedded depth", StringComparison.Ordinal));
    }

    // ── In-place recursion (Task C3) ─────────────────────────────────────────

    [Fact]
    public async Task Eml_WithEmbeddedEml_YieldsNestedBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness();
        var nested = EmlFixtureBuilder.CreateNested("Forwarded Subject", "Forwarded body.");
        var outer = await EmlFixtureBuilder.CreateWithEmbeddedAsync("Outer", "Outer body.", nested, ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(4, sections.Count);
        Assert.Equal("Outer", sections[0].Heading);
        Assert.Equal("Outer body.", sections[1].Text);
        Assert.Equal("Forwarded Subject", sections[2].Heading);
        Assert.Equal("Forwarded body.", sections[3].Text);
        Assert.Empty(harness.Warnings);

        // SectionIndex is stamped once, at the top level; recursion must not re-stamp.
        Assert.Equal([0, 1, 2, 3], sections.Select(s => s.SectionIndex));
        Assert.All(sections, s => Assert.Equal(new DocumentId("chain-1"), s.DocumentId));
    }

    [Fact]
    public async Task Msg_WithNestedMsg_YieldsNestedBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness();
        using var stream = MsgFixtureBuilder.Create(
            "Outer", "Outer body.",
            embeddedMessageSubject: "Forwarded Subject",
            embeddedMessageBody: "Forwarded body.");

        var sections = await harness.Msg.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(4, sections.Count);
        Assert.Equal("Forwarded Subject", sections[2].Heading);
        Assert.Equal("Forwarded body.", sections[3].Text);
        Assert.Empty(harness.Warnings);
        Assert.Equal([0, 1, 2, 3], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task NestedAttachments_StillDispatchToOwnParsers()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new FakeTextParser();
        var harness = new ParserHarness(extraParsers: [text]);

        var nested = EmlFixtureBuilder.CreateNested("Forwarded Subject", "Forwarded body.",
            attachments: [("notes.txt", "text/plain", Encoding.UTF8.GetBytes("Attached note."))]);
        var outer = await EmlFixtureBuilder.CreateWithEmbeddedAsync("Outer", "Outer body.", nested, ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(5, sections.Count);
        Assert.Equal("Attached note.", sections[4].Text);

        // Dispatch from inside an embedded message scopes metadata exactly as it does at the
        // top level: the attachment's own name and type, the parent document's id.
        var received = Assert.Single(text.ReceivedMetadata);
        Assert.Equal(new DocumentId("chain-1"), received.DocumentId);
        Assert.Equal("notes.txt", received.FileName);
        Assert.Equal("text/plain", received.ContentType);
    }

    [Fact]
    public async Task EmbeddedMessageChain_DepthExceeded_WarnsAndSkips()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness();
        var outer = await BuildEmbeddedEmlChainAsync(harness.Options.MaxEmbeddedDepth + 1, ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2 * (harness.Options.MaxEmbeddedDepth + 1), sections.Count);
        Assert.Equal("Outer", sections[0].Heading); // outer content still parses
        Assert.DoesNotContain(sections, s => string.Equals(s.Text, InnermostMarker, StringComparison.Ordinal));
        var warning = Assert.Single(harness.Warnings);
        Assert.Contains("maximum embedded depth of 3", warning, StringComparison.Ordinal);
    }

    // ── Limits ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DepthExceeded_WarnsAndSkips_OuterContentStillParses()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness(new EmailParserOptions { MaxEmbeddedDepth = 2 });
        var outer = await BuildAlternatingChainAsync(6, ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(6, sections.Count); // depths 0, 1 and 2 — subject + body each
        Assert.Equal("Level 6", sections[0].Heading); // the outermost content survives
        Assert.Equal("Body 6.", sections[1].Text);
        var warning = Assert.Single(harness.Warnings);
        Assert.Contains("maximum embedded depth of 2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeCapExceeded_WarnsAndSkips()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness(new EmailParserOptions { MaxEmbeddedDepth = 10, MaxEmbeddedMessages = 2 });
        var outer = await BuildAlternatingChainAsync(6, ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(6, sections.Count); // the top-level message plus two embedded ones
        var warning = Assert.Single(harness.Warnings);
        Assert.Contains("maximum of 2 embedded messages", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroDepth_DisablesRecursion_WithoutWarningOrLosingOuterContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new FakeTextParser();
        var harness = new ParserHarness(new EmailParserOptions { MaxEmbeddedDepth = 0 }, extraParsers: [text]);

        var nested = EmlFixtureBuilder.CreateNested("Forwarded Subject", "Forwarded body.");
        var outer = await EmlFixtureBuilder.CreateWithEmbeddedAsync("Outer", "Outer body.", nested, ct);
        var withAttachment = await EmlFixtureBuilder.CreateAsync("Outer", "Outer body.",
            [("notes.txt", "text/plain", Encoding.UTF8.GetBytes("Attached note."))], ct);

        using var embeddedStream = new MemoryStream(outer);
        var embeddedSections = await harness.Eml.ParseAsync(embeddedStream, CreateMetadata(), ct).ToListAsync(ct);

        using var attachmentStream = new MemoryStream(withAttachment);
        var attachmentSections = await harness.Eml.ParseAsync(attachmentStream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, embeddedSections.Count); // subject + body; the embedded message is skipped
        Assert.Equal("Outer body.", embeddedSections[1].Text);

        // Turning recursion off must not disturb ordinary attachment dispatch.
        Assert.Equal(3, attachmentSections.Count);
        Assert.Equal("Attached note.", attachmentSections[2].Text);

        // The skip is deliberate, so it is silent rather than one warning per message.
        Assert.Empty(harness.Warnings);
    }

    [Fact]
    public async Task BudgetTag_FromCaller_CannotRaiseTheCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness(new EmailParserOptions { MaxEmbeddedDepth = 10, MaxEmbeddedMessages = 2 });
        var outer = await BuildAlternatingChainAsync(6, ct);

        // Reserved tags are attacker-reachable — DocumentMetadata.Tags can be populated from
        // remote data by a connector. A larger budget must be clamped back to the configured cap.
        var hostileTags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["__rag_email_budget"] = "1000000",
        };

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(hostileTags), ct).ToListAsync(ct);

        Assert.Equal(6, sections.Count); // the configured cap of 2 still applies
        Assert.Single(harness.Warnings);

        // Depth was 0, so the caller's dictionary is never adopted as a write-back sink.
        Assert.Equal("1000000", hostileTags["__rag_email_budget"]);
    }

    [Fact]
    public async Task NodeCap_IsTotal_AcrossSiblingBranches()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new ParserHarness(new EmailParserOptions { MaxEmbeddedDepth = 10, MaxEmbeddedMessages = 3 });

        // Two sibling branches of two nested messages each. A per-branch budget would admit
        // all four; a total budget admits three and warns once.
        var outer = await EmlFixtureBuilder.CreateAsync("Root", "Root body.",
            [
                ("a.msg", "application/vnd.ms-outlook", BuildTwoLevelBranch()),
                ("b.msg", "application/vnd.ms-outlook", BuildTwoLevelBranch()),
            ],
            ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(8, sections.Count); // root + 3 embedded messages, two sections each
        Assert.Single(harness.Warnings);
    }

    [Fact]
    public async Task DepthAboveTheCeiling_DirectConstruction_IsClampedAtParseTime()
    {
        var ct = TestContext.Current.CancellationToken;

        // The parsers are constructed straight over the options here, so AddEmailParser's
        // validation never ran — the bypass the setter's clamp closes. The assertion is that
        // the ceiling is what actually bounds the parse; 65 levels is two orders of magnitude
        // below the ~480-level survival floor, so no real stack overflow is provoked.
        var options = new EmailParserOptions { MaxEmbeddedDepth = 10_000, MaxEmbeddedMessages = 500 };
        Assert.Equal(EmailParserOptions.MaxSupportedEmbeddedDepth, options.MaxEmbeddedDepth);

        var harness = new ParserHarness(options);
        var outer = await BuildEmbeddedEmlChainAsync(EmailParserOptions.MaxSupportedEmbeddedDepth + 1, ct);

        using var stream = new MemoryStream(outer);
        var sections = await harness.Eml.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2 * (EmailParserOptions.MaxSupportedEmbeddedDepth + 1), sections.Count);
        Assert.DoesNotContain(sections, s => string.Equals(s.Text, InnermostMarker, StringComparison.Ordinal));
        Assert.Contains("maximum embedded depth of 64", Assert.Single(harness.Warnings), StringComparison.Ordinal);
    }

    // ── Reserved tags ────────────────────────────────────────────────────────

    [Fact]
    public async Task DepthTag_NotLeakedIntoSectionMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new FakeTextParser();
        var harness = new ParserHarness(extraParsers: [text]);

        // outer.eml → inner.msg → note.txt: the text parser sits below an embedded message,
        // so it is the first place a leaked depth tag would show up.
        using var inner = MsgFixtureBuilder.Create("Inner", "Inner body.",
            attachments: [("note.txt", Encoding.UTF8.GetBytes("Note."), "text/plain")]);
        var outer = await EmlFixtureBuilder.CreateAsync("Outer", "Outer body.",
            [("inner.msg", "application/vnd.ms-outlook", inner.ToArray())], ct);

        var callerTags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "unit-test" };
        using var stream = new MemoryStream(outer);
        _ = await harness.Eml.ParseAsync(stream, CreateMetadata(callerTags), ct).ToListAsync(ct);

        var received = Assert.Single(text.ReceivedMetadata);
        Assert.Equal("unit-test", received.Tags["source"]);
        Assert.DoesNotContain(received.Tags, t => t.Key.StartsWith("__rag_email", StringComparison.Ordinal));

        // The caller's own dictionary is what reaches stored chunk metadata; it must be untouched.
        Assert.Equal(["source"], callerTags.Keys, StringComparer.Ordinal);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds <paramref name="nestedLevels"/> messages nested inside one another, alternating
    /// container format at every level so consecutive levels are handled by two different
    /// parser instances. Every level is carried as an ordinary binary attachment, so the whole
    /// chain travels through <c>ContainerEntryDispatcher</c>. <paramref name="nestedLevels"/>
    /// must be even, which makes the outermost container an <c>.eml</c>.
    /// </summary>
    private static async Task<byte[]> BuildAlternatingChainAsync(int nestedLevels, CancellationToken cancellationToken)
    {
        Assert.Equal(0, nestedLevels % 2);

        var current = await EmlFixtureBuilder.CreateAsync("Level 0", InnermostMarker, null, cancellationToken);
        bool currentIsEml = true;

        for (int level = 1; level <= nestedLevels; level++)
        {
            if (currentIsEml)
            {
                using var msg = MsgFixtureBuilder.Create(
                    $"Level {level}",
                    $"Body {level}.",
                    attachments: [("inner.eml", current, "message/rfc822")]);
                current = msg.ToArray();
            }
            else
            {
                current = await EmlFixtureBuilder.CreateAsync(
                    $"Level {level}",
                    $"Body {level}.",
                    [("inner.msg", "application/vnd.ms-outlook", current)],
                    cancellationToken);
            }

            currentIsEml = !currentIsEml;
        }

        return current;
    }

    /// <summary>
    /// Builds one <c>.eml</c> holding a chain of <paramref name="nestedLevels"/> messages
    /// nested as live <c>MessagePart</c>s — the in-place recursion path, not the dispatcher.
    /// </summary>
    private static async Task<byte[]> BuildEmbeddedEmlChainAsync(int nestedLevels, CancellationToken cancellationToken)
    {
        MimeMessage? inner = null;
        for (int level = 0; level < nestedLevels; level++)
        {
            inner = EmlFixtureBuilder.CreateNested(
                $"Nested {level}",
                level == 0 ? InnermostMarker : $"Nested body {level}.",
                inner);
        }

        return await EmlFixtureBuilder.CreateWithEmbeddedAsync("Outer", "Outer body.", inner!, cancellationToken);
    }

    /// <summary>A <c>.msg</c> holding one further <c>.msg</c> — two embedded messages in one branch.</summary>
    private static byte[] BuildTwoLevelBranch()
    {
        using var leaf = MsgFixtureBuilder.Create("Leaf", "Leaf body.");
        using var branch = MsgFixtureBuilder.Create("Branch", "Branch body.",
            attachments: [("leaf.msg", leaf.ToArray(), null)]);
        return branch.ToArray();
    }

    /// <summary>An EML and an MSG parser sharing one parser list, options and warning sink.</summary>
    private sealed class ParserHarness
    {
        private readonly CapturingLogger<EmailDocumentParser> emlLog = new();
        private readonly CapturingLogger<MsgDocumentParser> msgLog = new();

        public ParserHarness(EmailParserOptions? options = null, IDocumentParser[]? extraParsers = null)
        {
            Options = options ?? new EmailParserOptions();
            var parsers = new List<IDocumentParser>();
            Eml = new EmailDocumentParser(parsers, new HtmlDocumentParser(), emlLog, Options);
            Msg = new MsgDocumentParser(parsers, new HtmlDocumentParser(), msgLog, Options);
            parsers.Add(Eml);
            parsers.Add(Msg);
            parsers.AddRange(extraParsers ?? []);
        }

        public EmailParserOptions Options { get; }

        public EmailDocumentParser Eml { get; }

        public MsgDocumentParser Msg { get; }

        public IEnumerable<string> Warnings =>
            emlLog.Entries.Concat(msgLog.Entries)
                .Where(e => e.Level == LogLevel.Warning)
                .Select(e => e.Message);
    }
}
