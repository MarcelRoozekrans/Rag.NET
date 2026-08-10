using System.Text;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// One depth counter and one entry budget across two container formats — the entire justification
/// for promoting the container machinery out of <c>Rag.NET.Parsers.Email</c>, tested directly rather
/// than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The budget fixtures are two branches wide on purpose.</b> A single-branch chain cannot tell a
/// shared budget from a per-branch one: both stop in the same place. Every container in these
/// fixtures carries a marker <c>.txt</c> named after itself, so the markers a run produces
/// <i>enumerate</i> the containers it admitted rather than standing in for them.
/// </para>
/// <para>
/// <b>The <c>cap ^ depth</c> trap, stated as a number.</b> With a budget of 3 and this shape a shared
/// budget admits 3 containers in the whole tree. A budget that reset per dispatched branch — what
/// happens when <c>ContainerBudget</c> stops writing each decrement back into the tag dictionary the
/// dispatcher built for the child — admits 5, because the second top-level branch starts again from
/// the count its parent stamped before descending. That is not a prediction: deleting the
/// <c>sink[ContainerContext.BudgetTag] = …</c> line and rebuilding turned these tests red with
/// exactly 5 markers where 3 were expected.
/// </para>
/// </remarks>
public class NestedContainerBudgetTests
{
    /// <summary>The shared bound both parsers are configured with. See the type's remarks.</summary>
    private const int Budget = 3;

    /// <summary>The byte total the nested-payload fixtures are read against.</summary>
    private const long ByteCap = 50_000;

    /// <summary>The bytes one nested archive's single entry holds. Comfortably over half the cap.</summary>
    private const int PayloadBytes = 40_000;

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("nested-1"),
        FileName = "outer.zip",
        ContentType = "application/zip",
        Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
    };

    /// <summary>
    /// <c>zip → .eml → zip</c>. The budget is spent by containers of two formats, parsed by two
    /// parsers in two packages that share nothing but <c>Rag.NET.Abstractions</c>.
    /// </summary>
    [Fact]
    public async Task AnAlternatingChainSpendsOneBudgetAcrossBothFormats()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(Budget, maxNestingDepth: 3);

        var admitted = await AdmitAsync(harness, await Chains.AlternatingAsync(ct), ct);

        // Three in the tree, not three per branch: the second .eml is refused because the first
        // branch spent the budget, which only happens if the count survived the trip back across the
        // IDocumentParser boundary.
        Assert.Equal(["eml-a", "zip-a-x", "zip-a-y"], admitted);
        Assert.Equal(Budget, admitted.Count);
        Assert.Contains(harness.Warnings, w => w.Contains("b.eml", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same shape without the format change. Design §5's claim is that alternating formats buys
    /// an attacker nothing, so this is the control and its numbers must be the alternating chain's
    /// numbers exactly.
    /// </summary>
    [Fact]
    public async Task AUniformChainIsBoundedByTheSameNumbers()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(Budget, maxNestingDepth: 3);

        var admitted = await AdmitAsync(harness, Chains.UniformZip(), ct);

        Assert.Equal(["zip-a", "zip-a-x", "zip-a-y"], admitted);
        Assert.Equal(Budget, admitted.Count);
    }

    /// <summary>
    /// The two counts side by side, which is the comparison the design actually makes. The tests
    /// above pin the number; this one pins the equality, so a change that loosened both by the same
    /// amount is still caught by them rather than slipping through here.
    /// </summary>
    [Fact]
    public async Task AlternatingBuysNothingOverStayingInOneFormat()
    {
        var ct = TestContext.Current.CancellationToken;

        var alternating = await AdmitAsync(
            new Harness(Budget, maxNestingDepth: 3), await Chains.AlternatingAsync(ct), ct);
        var uniform = await AdmitAsync(
            new Harness(Budget, maxNestingDepth: 3), Chains.UniformZip(), ct);

        Assert.Equal(uniform.Count, alternating.Count);
    }

    /// <summary>
    /// Depth, on the same terms as the budget. With a bound of 2 the chain stops at the second
    /// nested container whichever formats it alternates between — the level an <c>.eml</c> occupies
    /// is the level a <c>.zip</c> would have occupied in its place.
    /// </summary>
    [Fact]
    public async Task DepthIsCountedAcrossTheFormatBoundaryToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(maxNestedContainers: 50, maxNestingDepth: 2);

        var alternating = await AdmitAsync(harness, await Chains.DeepAlternatingAsync(ct), ct);
        var uniform = await AdmitAsync(
            new Harness(maxNestedContainers: 50, maxNestingDepth: 2), Chains.DeepUniformZip(), ct);

        // "top" is the document's own marker; the two below it are the nested containers the bound
        // admits. The fourth level exists in the fixture and is never reached.
        Assert.Equal(["top", "depth-1", "depth-2"], alternating);
        Assert.DoesNotContain("depth-3", alternating, StringComparer.Ordinal);
        Assert.Equal(uniform, alternating);
        Assert.Contains(harness.Warnings, w => w.Contains("nesting depth", StringComparison.Ordinal));
    }

    // ── The byte total, on the same channel ──────────────────────────────────

    /// <summary>
    /// The bound the whole-phase review found missing. <c>ArchiveReadBudget</c> was created per
    /// <c>ParseAsync</c>, so a nested zip re-entered through <see cref="ContainerEntryDispatcher"/>
    /// and got a <b>fresh</b> allowance, costing its parent only its small compressed size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture is the review's own repro: five inner archives of 40,000 bytes each against a
    /// 50,000-byte cap. Per-archive, every branch is admitted in full and the run delivers 200,000
    /// bytes with no exception and no warning — and with the default nesting budget of 50 the real
    /// ceiling was roughly <c>51 × cap</c>. Shared, the second branch is refused.
    /// </para>
    /// <para>
    /// The inner content is <see cref="ZipFixtureBuilder.Compressible"/> rather than
    /// <see cref="ZipFixtureBuilder.Zeros"/> or <see cref="ZipFixtureBuilder.Incompressible"/> for
    /// reasons stated there: incompressible content would make the <i>outer</i> archive's own reads
    /// exhaust the budget, which is the wrong mechanism and would keep the test green under the
    /// mutation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANestedArchiveTreeSpendsOneByteTotalRatherThanOnePerArchive()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var parsers = new List<IDocumentParser>();
        var zip = new ZipDocumentParser(
            parsers, options: new ArchiveParserOptions { MaxTotalUncompressedBytes = ByteCap });
        parsers.Add(zip);
        parsers.Add(text);

        var archive = Chains.NestedPayloads(branches: 5);
        AssertTheOuterArchiveCannotExhaustTheBudgetByItself(archive);

        using var stream = new MemoryStream(archive);
        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await zip.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
        Assert.Equal(ByteCap, exception.Allowed);

        // The number that says "one total, not one per archive". Per-archive this run delivered
        // 200,000 bytes and threw nothing at all; shared, it stops within one read of the cap.
        Assert.InRange(exception.Observed, ByteCap, 2 * ByteCap);
        Assert.True(
            text.ReceivedMetadata.Count < 5,
            $"All five nested archives were read, delivering {exception.Observed} bytes against a " +
            $"{ByteCap}-byte cap, which is the per-archive budget this test exists to refuse.");
    }

    /// <summary>
    /// The byte total across the format boundary, which is the same claim design §5 makes for depth
    /// and the container budget. An <c>.eml</c> has no byte bound of its own, so it neither reads nor
    /// writes the running total — it has to carry it, or a <c>zip → .eml → zip</c> chain buys an
    /// attacker the reset that <c>zip → zip</c> no longer does.
    /// </summary>
    [Fact]
    public async Task TheByteTotalSurvivesAHopThroughTheEmailParser()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var parsers = new List<IDocumentParser>();
        var zip = new ZipDocumentParser(
            parsers, options: new ArchiveParserOptions { MaxTotalUncompressedBytes = ByteCap });
        var eml = new EmailDocumentParser(parsers, new HtmlDocumentParser());
        parsers.Add(zip);
        parsers.Add(eml);
        parsers.Add(text);

        using var stream = new MemoryStream(await Chains.NestedPayloadsBehindEmailAsync(branches: 5, ct));
        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await zip.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
        Assert.InRange(exception.Observed, ByteCap, 2 * ByteCap);
    }

    // ── Where a refusal stops ────────────────────────────────────────────────

    /// <summary>
    /// <b>The containment boundary, stated as a test because it was neither tested nor documented.</b>
    /// A zip bomb attached to an <c>.eml</c> refuses <i>itself</i> — <c>ZipDocumentParser</c> throws
    /// out of its own <c>ParseAsync</c> — and the email parser's dispatcher contains that refusal one
    /// level up, exactly as it contains a corrupt PDF. The message keeps its subject, its body and its
    /// other attachments, and the bomb becomes a warning naming the zip.
    /// </summary>
    /// <remarks>
    /// This is intended, not a second instance of the defect the whole-phase review found inside the
    /// archive parser. There, containment swallowed a refusal the parser itself was responsible for
    /// raising and the archive went on being indexed. Here the refusal has already done its work: the
    /// bomb stopped being read, and what the caller named was a message, not an archive — the same
    /// reason one unreadable attachment must not cost the mail it arrived on. The byte total still
    /// crosses the boundary, so a document made of nothing but such attachments is bounded by
    /// <c>MaxTotalUncompressedBytes</c> rather than by one warning per attachment.
    /// </remarks>
    [Fact]
    public async Task AZipBombInsideAnEmailRefusesItselfAndCostsOnlyThatAttachment()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var parsers = new List<IDocumentParser>();
        var emailLog = new CapturingLogger<EmailDocumentParser>();
        var zip = new ZipDocumentParser(parsers, new CapturingLogger<ZipDocumentParser>());
        var eml = new EmailDocumentParser(parsers, new HtmlDocumentParser(), emailLog);
        parsers.Add(zip);
        parsers.Add(eml);
        parsers.Add(text);

        // A real megabyte of zeros at a real ~1000:1 against the default 100:1 ratio bound.
        var bomb = ZipFixtureBuilder.Archive(("zeros.txt", ZipFixtureBuilder.Zeros(1024 * 1024)));
        var message = await Chains.CarryingAsync("bomb.zip", "application/zip", bomb, ct);

        using var stream = new MemoryStream(message);
        var sections = await eml.ParseAsync(stream, EmailMetadata(), ct).ToListAsync(ct);

        // Nothing escaped: the message's own content is all here, and the marker attachment beside
        // the bomb was still dispatched.
        Assert.Contains(sections, s => s.Text.Contains("carrier", StringComparison.Ordinal));
        Assert.Contains(text.ReceivedMetadata, m => m.FileName.EndsWith("carrier.txt", StringComparison.Ordinal));

        // And the bomb is a warning naming the archive parser and the attachment, not a lost message.
        Assert.Contains(
            emailLog.Warnings,
            w => w.Contains("bomb.zip", StringComparison.Ordinal) &&
                 w.Contains(nameof(ZipDocumentParser), StringComparison.Ordinal));
    }

    private static DocumentMetadata EmailMetadata() => new()
    {
        DocumentId = new DocumentId("carrier-1"),
        FileName = "carrier.eml",
        ContentType = "message/rfc822",
        Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
    };

    /// <summary>
    /// Asserts the outer archive's own reads are nowhere near the cap, so a refusal can only have
    /// come from what the nested archives spent. Without this the test would stay green under a
    /// per-archive budget for the wrong reason.
    /// </summary>
    private static void AssertTheOuterArchiveCannotExhaustTheBudgetByItself(byte[] archiveBytes)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var archive = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Read);

        long outerBytes = 0;
        foreach (var entry in archive.Entries)
        {
            outerBytes += entry.Length;
        }

        Assert.True(
            outerBytes < ByteCap / 2,
            $"The outer archive's entries are {outerBytes} bytes against a {ByteCap}-byte cap, so " +
            "reading the outer archive alone could account for the refusal.");
    }

    private static async Task<IReadOnlyList<string>> AdmitAsync(
        Harness harness,
        byte[] archive,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(archive);
        await harness.Zip.ParseAsync(stream, CreateMetadata(), cancellationToken).ToListAsync(cancellationToken);
        return harness.AdmittedContainers;
    }

    /// <summary>
    /// Both parsers over one parser collection, each configured with its own package's options.
    /// </summary>
    /// <remarks>
    /// The two options types are set to the same numbers explicitly. They default to the same numbers
    /// too, but a test that leaned on the defaults agreeing would be asserting the defaults rather
    /// than the sharing.
    /// </remarks>
    private sealed class Harness
    {
        private readonly RecordingParser text = new("text/plain");
        private readonly CapturingLogger<ZipDocumentParser> zipLog = new();
        private readonly CapturingLogger<EmailDocumentParser> emailLog = new();

        public Harness(int maxNestedContainers, int maxNestingDepth)
        {
            var parsers = new List<IDocumentParser>();
            Zip = new ZipDocumentParser(
                parsers,
                zipLog,
                new ArchiveParserOptions
                {
                    MaxNestedContainers = maxNestedContainers,
                    MaxNestingDepth = maxNestingDepth,
                });

            var eml = new EmailDocumentParser(
                parsers,
                new HtmlDocumentParser(),
                emailLog,
                new EmailParserOptions
                {
                    MaxEmbeddedMessages = maxNestedContainers,
                    MaxEmbeddedDepth = maxNestingDepth,
                });

            parsers.Add(Zip);
            parsers.Add(eml);
            parsers.Add(text);
        }

        public ZipDocumentParser Zip { get; }

        /// <summary>
        /// The marker every container in the fixtures carries, so this is the list of containers the
        /// run actually entered, in order.
        /// </summary>
        public IReadOnlyList<string> AdmittedContainers =>
            text.ReceivedMetadata.ConvertAll(m => Path.GetFileNameWithoutExtension(m.FileName.Split('#')[^1]));

        public IEnumerable<string> Warnings => zipLog.Warnings.Concat(emailLog.Warnings);
    }

    /// <summary>
    /// The fixtures. Every container holds a marker <c>.txt</c> named after itself, listed before its
    /// children so the markers come out in traversal order.
    /// </summary>
    private static class Chains
    {
        /// <summary>
        /// <c>outer.zip</c> → two <c>.eml</c>s → two <c>.zip</c>s each: two branches wide at both
        /// levels, which is what makes a per-branch budget distinguishable from a shared one.
        /// </summary>
        public static async Task<byte[]> AlternatingAsync(CancellationToken cancellationToken)
        {
            var a = await EmlAsync("A", "eml-a", [InnerZip("zip-a-x"), InnerZip("zip-a-y")], cancellationToken);
            var b = await EmlAsync("B", "eml-b", [InnerZip("zip-b-x"), InnerZip("zip-b-y")], cancellationToken);
            return ZipFixtureBuilder.Archive(("a.eml", a), ("b.eml", b));
        }

        /// <summary>The same shape with zips all the way down.</summary>
        public static byte[] UniformZip()
        {
            var a = ZipFixtureBuilder.Archive(
                Marker("zip-a"), ("x.zip", InnerZip("zip-a-x").Data), ("y.zip", InnerZip("zip-a-y").Data));
            var b = ZipFixtureBuilder.Archive(
                Marker("zip-b"), ("x.zip", InnerZip("zip-b-x").Data), ("y.zip", InnerZip("zip-b-y").Data));
            return ZipFixtureBuilder.Archive(("a.zip", a), ("b.zip", b));
        }

        /// <summary>One branch, four levels, alternating <c>.eml</c> and <c>.zip</c>.</summary>
        public static async Task<byte[]> DeepAlternatingAsync(CancellationToken cancellationToken)
        {
            var third = await EmlAsync("L3", "depth-3", [], cancellationToken);
            var second = ZipFixtureBuilder.Archive(Marker("depth-2"), ("l3.eml", third));
            var first = await EmlAsync(
                "L1", "depth-1", [("l2.zip", "application/zip", second)], cancellationToken);
            return ZipFixtureBuilder.Archive(Marker("top"), ("l1.eml", first));
        }

        /// <summary>The same depth without the format change.</summary>
        public static byte[] DeepUniformZip()
        {
            var third = ZipFixtureBuilder.Archive(Marker("depth-3"));
            var second = ZipFixtureBuilder.Archive(Marker("depth-2"), ("l3.zip", third));
            var first = ZipFixtureBuilder.Archive(Marker("depth-1"), ("l2.zip", second));
            return ZipFixtureBuilder.Archive(Marker("top"), ("l1.zip", first));
        }

        /// <summary>
        /// One outer zip holding <paramref name="branches"/> inner zips, each holding one
        /// <see cref="PayloadBytes"/> entry. Wide rather than deep, because a per-archive byte budget
        /// and a shared one are only distinguishable across siblings.
        /// </summary>
        public static byte[] NestedPayloads(int branches)
        {
            var entries = new (string Name, byte[] Content)[branches];
            for (var index = 0; index < branches; index++)
            {
                entries[index] = ($"branch-{index}.zip", Payload(index));
            }

            return ZipFixtureBuilder.Archive(entries);
        }

        /// <summary>The same shape with an <c>.eml</c> between each inner zip and the outer one.</summary>
        public static async Task<byte[]> NestedPayloadsBehindEmailAsync(
            int branches,
            CancellationToken cancellationToken)
        {
            var entries = new (string Name, byte[] Content)[branches];
            for (var index = 0; index < branches; index++)
            {
                var carrier = await EmlAsync(
                    $"branch {index}",
                    $"carrier-{index}",
                    [($"payload-{index}.zip", "application/zip", Payload(index))],
                    cancellationToken);
                entries[index] = ($"branch-{index}.eml", carrier);
            }

            return ZipFixtureBuilder.Archive(entries);
        }

        /// <summary>One <c>.eml</c> carrying its own marker attachment plus the given one.</summary>
        public static Task<byte[]> CarryingAsync(
            string name,
            string contentType,
            byte[] data,
            CancellationToken cancellationToken) =>
            EmlAsync("carrier", "carrier", [(name, contentType, data)], cancellationToken);

        /// <summary>A one-entry zip whose entry is big enough that two of them pass the byte cap.</summary>
        private static byte[] Payload(int index) =>
            ZipFixtureBuilder.Archive(
                ($"payload-{index}.txt", ZipFixtureBuilder.Compressible(PayloadBytes, seed: index + 1)));

        /// <summary>A one-entry zip holding nothing but its own marker.</summary>
        private static (string Name, string ContentType, byte[] Data) InnerZip(string marker) =>
            ($"{marker}.zip", "application/zip", ZipFixtureBuilder.Archive(Marker(marker)));

        private static (string Name, byte[] Content) Marker(string marker) =>
            ($"{marker}.txt", Encoding.UTF8.GetBytes(marker));

        private static async Task<byte[]> EmlAsync(
            string subject,
            string marker,
            (string Name, string ContentType, byte[] Data)[] attachments,
            CancellationToken cancellationToken)
        {
            var body = new BodyBuilder { TextBody = "body" };
            body.Attachments.Add($"{marker}.txt", Encoding.UTF8.GetBytes(marker), new ContentType("text", "plain"));
            foreach (var (name, contentType, data) in attachments)
            {
                body.Attachments.Add(name, data, ContentType.Parse(contentType));
            }

            var message = new MimeMessage { Subject = subject, Body = body.ToMessageBody() };
            message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));

            using var stream = new MemoryStream();
            await message.WriteToAsync(stream, cancellationToken);
            return stream.ToArray();
        }
    }
}
