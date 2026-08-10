using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// The parser end to end: entries reach the parsers that claim them, the ones with nothing in them
/// are skipped, a failing entry parser costs its own entry, and the entry-count bound refuses the
/// archive by name.
/// </summary>
public class ZipDocumentParserTests
{
    private const string ArchiveName = "bundle.zip";
    private const string NotesText = "the note's content";
    private const string PageText = "<p>the page</p>";

    /// <summary>A megabyte, which is what makes the ratio fixture a real bomb rather than a mock.</summary>
    private const int OneMegabyte = 1024 * 1024;

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("archive-1"),
        FileName = ArchiveName,
        ContentType = "application/zip",
        Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
    };

    // ── What it claims ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/zip")]
    [InlineData("application/x-zip-compressed")]
    [InlineData("APPLICATION/ZIP")]
    public void TheTwoZipContentTypesAreClaimed(string contentType) =>
        Assert.True(new ZipDocumentParser([]).CanParse(contentType));

    /// <summary>
    /// An EPUB is a zip, and <c>EpubDocumentParser</c> owns it. A generic zip parser answering here
    /// would emit entry-by-entry rubbish — a container XML, a stylesheet, an OPF manifest — instead of
    /// chapters, and would do it silently because both parsers "work".
    /// </summary>
    [Fact]
    public void TheEpubContentTypeIsNotClaimed() =>
        Assert.False(new ZipDocumentParser([]).CanParse("application/epub+zip"));

    /// <summary>
    /// <c>application/octet-stream</c> means "unknown binary". Phase 3.11 made it load-bearing that
    /// nothing format-specific answers it: that is what turns an unclaimed attachment into a warning
    /// instead of a wrong parser failing on it.
    /// </summary>
    [Fact]
    public void TheUnknownBinaryContentTypeIsNotClaimed() =>
        Assert.False(new ZipDocumentParser([]).CanParse("application/octet-stream"));

    /// <summary>
    /// The drift Phase 3.11 left unenforced: <c>AddArchiveParser</c> declares the claims the startup
    /// guard reasons about, and nothing made them agree with the predicate that actually selects the
    /// parser. Asserted in both directions, so adding a clause to <c>CanParse</c> without a matching
    /// <see cref="ParserClaim"/> — or the reverse — is a failure here rather than a conflict the guard
    /// silently stops detecting.
    /// </summary>
    /// <remarks>
    /// <b>The universe is wider than <see cref="ContentTypeMap"/>, and the whole-phase review is why.</b>
    /// Built from the map alone, this test caught an over-claim of <c>application/epub+zip</c> — which
    /// is in the map — and missed one of <c>application/x-7z-compressed</c> entirely, leaving 44 of 44
    /// green. The startup <see cref="ParserClaim"/> guard cannot see that either, because an
    /// over-claim in <c>CanParse</c> is by definition one nobody declared. So the map is joined by the
    /// declared claims themselves and by <see cref="NeighbouringContentTypes"/> — the types a zip
    /// parser is plausibly tempted into, none of which this repository maps.
    /// </remarks>
    [Fact]
    public void TheDeclaredClaimsMatchCanParseExactly()
    {
        var builder = new TestRagBuilder();
        builder.AddArchiveParser();
        using var provider = builder.Services.BuildServiceProvider();

        var claims = provider.GetServices<ParserClaim>();
        var claimed = new HashSet<string>(
            claims.Select(c => c.ContentType), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, claimed.Count);
        Assert.All(claims, c => Assert.Equal(typeof(ZipDocumentParser).FullName, c.ParserTypeName));
        Assert.All(claims, c => Assert.Equal("AddArchiveParser()", c.RegistrationMethod));

        var parser = new ZipDocumentParser([]);
        foreach (var contentType in CandidateContentTypes(claimed))
        {
            Assert.Equal(claimed.Contains(contentType), parser.CanParse(contentType));
        }
    }

    /// <summary>
    /// Guards the guard: the widened universe must actually contain types
    /// <see cref="ContentTypeMap"/> does not know, or it is the old test under a new name.
    /// </summary>
    [Fact]
    public void TheClaimDriftUniverseReachesBeyondTheContentTypeMap()
    {
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in MappedExtensions)
        {
            mapped.Add(ContentTypeMap.FromFileName("file" + extension));
        }

        Assert.NotEmpty(NeighbouringContentTypes);
        Assert.All(NeighbouringContentTypes, t => Assert.DoesNotContain(t, mapped, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The wire-up, not the rule: the ceilings and their messages are asserted directly in
    /// <see cref="ArchiveParserOptionsTests"/>, and this only pins that the registration calls them
    /// rather than re-implementing a check that could drift from the consts it quotes.
    /// </summary>
    [Fact]
    public void RegistrationRefusesABoundAboveItsCeiling()
    {
        var builder = new TestRagBuilder();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.AddArchiveParser(o => o.MaxEntries = ArchiveParserOptions.MaxSupportedEntries + 1));

        Assert.Contains(nameof(ArchiveParserOptions.MaxEntries), exception.Message, StringComparison.Ordinal);
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EntriesReachTheParsersThatClaimTheirContentType()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var html = new RecordingParser("text/html");
        var parser = new ZipDocumentParser([text, html]);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("notes.txt", Encoding.UTF8.GetBytes(NotesText)),
            ("page.html", Encoding.UTF8.GetBytes(PageText))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal([NotesText, PageText], sections.Select(s => s.Text), StringComparer.Ordinal);

        // Stamped once, by this parser, across every entry — the entry parsers each numbered their
        // own section 0.
        Assert.Equal([0, 1], sections.Select(s => s.SectionIndex));
        Assert.All(sections, s => Assert.Equal(new DocumentId("archive-1"), s.DocumentId));

        var received = Assert.Single(text.ReceivedMetadata);
        Assert.Equal("bundle.zip#notes.txt", received.FileName);
        Assert.Equal("text/plain", received.ContentType);
        Assert.Equal(new DocumentId("archive-1"), received.DocumentId);
    }

    /// <summary>
    /// A directory entry has no content to parse and an empty one has nothing in it, so neither
    /// reaches a parser — and neither produces a "no parser registered" warning for a content type
    /// nobody was ever going to claim.
    /// </summary>
    [Fact]
    public async Task DirectoryAndEmptyEntriesAreSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var logger = new CapturingLogger<ZipDocumentParser>();
        var parser = new ZipDocumentParser([text], logger);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("docs/", []),
            ("docs/empty.txt", []),
            ("docs/notes.txt", Encoding.UTF8.GetBytes(NotesText))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal([NotesText], sections.Select(s => s.Text), StringComparer.Ordinal);
        var received = Assert.Single(text.ReceivedMetadata);

        // The '/' in the entry name is sanitised out — hygiene, because this name reaches metadata.
        // It is not zip-slip mitigation: nothing here writes a file.
        Assert.Equal("bundle.zip#docs_notes.txt", received.FileName);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// A path-traversal entry name is a naming problem here and nothing more. It is recorded as a
    /// test so the claim in <see cref="ZipDocumentParser"/>'s remarks is checkable: the name is
    /// cleaned on its way to <see cref="DocumentMetadata.FileName"/>, which is the only place it
    /// goes.
    /// </summary>
    [Fact]
    public async Task ATraversalShapedEntryNameIsCleanedOnItsWayToMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var parser = new ZipDocumentParser([text]);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("../../etc/passwd.txt", Encoding.UTF8.GetBytes(NotesText))));

        await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        var received = Assert.Single(text.ReceivedMetadata);
        Assert.Equal("bundle.zip#.._.._etc_passwd.txt", received.FileName);
    }

    /// <summary>
    /// The content type comes from the entry's <b>unsanitised</b> name, and this is the case where
    /// the two answers differ. <see cref="FileNameSanitizer"/> caps a stem at 128 characters, so an
    /// entry named with 130 characters and a <c>.txt</c> extension loses that extension on its way to
    /// metadata — and a parser selected from the sanitised name would type it as
    /// <c>application/octet-stream</c>, warn that nothing claimed it, and drop content the archive
    /// really did carry.
    /// </summary>
    /// <remarks>
    /// The reasoning was recorded in <see cref="ZipDocumentParser"/>'s remarks and in features.md but
    /// pinned by nothing, so ordering the two operations the other way round was a silent change.
    /// Typing an entry is not something a display concern gets to decide.
    /// </remarks>
    [Fact]
    public async Task TheContentTypeComesFromTheEntryNameBeforeItIsSanitised()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var logger = new CapturingLogger<ZipDocumentParser>();
        var parser = new ZipDocumentParser([text], logger);

        var stem = new string('a', 130);
        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            (stem + ".txt", Encoding.UTF8.GetBytes(NotesText))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal([NotesText], sections.Select(s => s.Text), StringComparer.Ordinal);
        var received = Assert.Single(text.ReceivedMetadata);
        Assert.Equal("text/plain", received.ContentType);

        // The sanitised name really did lose the extension, so the assertion above is about ordering
        // rather than about a name the sanitiser left alone.
        Assert.Equal("bundle.zip#" + new string('a', 128), received.FileName);
        Assert.Equal(
            "application/octet-stream",
            ContentTypeMap.FromFileName(FileNameSanitizer.Sanitize(stem + ".txt", "archive-entry")));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task AnEntryWithNoRegisteredParserIsWarnedAboutAndSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<ZipDocumentParser>();
        var parser = new ZipDocumentParser([new RecordingParser("text/plain")], logger);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("unknown.bin", Encoding.UTF8.GetBytes("binary")),
            ("notes.txt", Encoding.UTF8.GetBytes(NotesText))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal([NotesText], sections.Select(s => s.Text), StringComparer.Ordinal);
        Assert.Contains(
            logger.Warnings,
            w => w.Contains("application/octet-stream", StringComparison.Ordinal) &&
                 w.Contains("unknown.bin", StringComparison.Ordinal));
    }

    /// <summary>
    /// One unreadable entry must not cost the archive. Before Phase 3.11's containment the exception
    /// escaped the whole parse, so a single bad attachment lost every sibling with it.
    /// </summary>
    [Fact]
    public async Task AnEntryWhoseParserThrowsCostsOnlyThatEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<ZipDocumentParser>();
        var parser = new ZipDocumentParser(
            [new ThrowingParser("text/html"), new RecordingParser("text/plain")], logger);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("first.txt", Encoding.UTF8.GetBytes("first")),
            ("bad.html", Encoding.UTF8.GetBytes(PageText)),
            ("last.txt", Encoding.UTF8.GetBytes("last"))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(["first", "last"], sections.Select(s => s.Text), StringComparer.Ordinal);
        Assert.Equal([0, 1], sections.Select(s => s.SectionIndex));
        Assert.Contains(
            logger.Warnings,
            w => w.Contains(nameof(ThrowingParser), StringComparison.Ordinal) &&
                 w.Contains("bundle.zip#bad.html", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sections a failing parser already yielded are kept: the caller has consumed them by the time
    /// the failure arrives, and discarding them would lose content that parser did produce.
    /// </summary>
    [Fact]
    public async Task SectionsYieldedBeforeAnEntryParserFailedSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = new ZipDocumentParser(
            [new ThrowingParser("text/html", sectionsBeforeThrow: 2), new RecordingParser("text/plain")]);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("bad.html", Encoding.UTF8.GetBytes(PageText)),
            ("last.txt", Encoding.UTF8.GetBytes("last"))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(
            [ThrowingParser.YieldedText, ThrowingParser.YieldedText, "last"],
            sections.Select(s => s.Text), StringComparer.Ordinal);
        Assert.Equal([0, 1, 2], sections.Select(s => s.SectionIndex));
    }

    // ── The entry-count bound ────────────────────────────────────────────────

    /// <summary>
    /// The bound <see cref="LimitedReadStream"/> cannot enforce: it sees one entry at a time and
    /// knows nothing of how many there are. Asserted on <see cref="ArchiveLimitExceededException.Limit"/>
    /// rather than on the fact that something threw — a bomb that trips the wrong cap is a test
    /// passing for the wrong reason.
    /// </summary>
    [Fact]
    public async Task TooManyEntriesIsReportedAsTheEntryCountCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = new ZipDocumentParser([new RecordingParser("text/plain")], options: new() { MaxEntries = 2 });

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("a.txt", Encoding.UTF8.GetBytes("a")),
            ("b.txt", Encoding.UTF8.GetBytes("b")),
            ("c.txt", Encoding.UTF8.GetBytes("c"))));

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.Equal(ArchiveLimit.EntryCount, exception.Limit);
        Assert.Equal(3, exception.Observed);
        Assert.Equal(2, exception.Allowed);
    }

    /// <summary>
    /// The count is of what the central directory <i>declares</i>, including the directory and empty
    /// entries the parse then skips. A thousand empty entries still cost a thousand central-directory
    /// records to read, which is the work this bound exists to refuse.
    /// </summary>
    [Fact]
    public async Task SkippedEntriesStillCountTowardsTheEntryCountCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = new ZipDocumentParser([new RecordingParser("text/plain")], options: new() { MaxEntries = 1 });

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("docs/", []),
            ("notes.txt", Encoding.UTF8.GetBytes(NotesText))));

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.Equal(ArchiveLimit.EntryCount, exception.Limit);
    }

    [Fact]
    public async Task AnArchiveAtTheEntryCountCapIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = new ZipDocumentParser([new RecordingParser("text/plain")], options: new() { MaxEntries = 2 });

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("a.txt", Encoding.UTF8.GetBytes("a")),
            ("b.txt", Encoding.UTF8.GetBytes("b"))));

        var sections = await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(["a", "b"], sections.Select(s => s.Text), StringComparer.Ordinal);
    }

    // ── The byte bounds, through the parser rather than the stream ───────────

    /// <summary>
    /// The total-bytes bound has to survive the containment, and this is the case that proves it
    /// does. <see cref="ContainerEntryDispatcher"/> catches everything an entry parser throws — it
    /// cannot tell a decompression bomb from a corrupt PDF — so <see cref="LimitedReadStream"/>'s
    /// refusal is swallowed there and the parser re-checks the archive-wide total after each entry.
    /// Without that re-check a bomb degrades into a warning and a partly-indexed archive.
    /// </summary>
    [Fact]
    public async Task ATotalBytesBombRefusesTheArchiveRatherThanWarningPerEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new ArchiveParserOptions { MaxTotalUncompressedBytes = 100 };
        var parser = new ZipDocumentParser([new RecordingParser("text/plain")], options: options);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("payload.txt", ZipFixtureBuilder.Incompressible(300, seed: 7))));

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
    }

    /// <summary>
    /// The ratio bound, on exactly the same terms, and at the <b>default</b> options — a real
    /// megabyte of zeros at a real ~1000:1 against the default 100:1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole-phase review found this half missing. The parser re-checked only the archive-wide
    /// total after each entry, so a ratio breach stayed swallowed by the dispatcher: the archive was
    /// not refused, the sibling entry was indexed, and the only signal was a "parser failed on archive
    /// entry" warning — precisely the degradation the total's re-check exists to prevent. Every ratio
    /// test lived in <see cref="LimitedReadStreamTests"/>, which drives its own read loop and never
    /// touches this parser, so deleting the ratio refusal cost two unit tests and no end-to-end one.
    /// </para>
    /// <para>
    /// The sibling assertion is the part that fails loudest under that mutation: an archive that was
    /// not refused goes on to index what follows the bomb. The warning is asserted as still being
    /// logged — containment has not changed, and the entry-level failure is still reported — because
    /// the fix is that the warning is no longer <i>all</i> that happens.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARatioBombRefusesTheArchiveRatherThanWarningPerEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new RecordingParser("text/plain");
        var logger = new CapturingLogger<ZipDocumentParser>();
        var parser = new ZipDocumentParser([text], logger);

        using var stream = new MemoryStream(ZipFixtureBuilder.Archive(
            ("zeros.txt", ZipFixtureBuilder.Zeros(OneMegabyte)),
            ("notes.txt", Encoding.UTF8.GetBytes(NotesText))));

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await parser.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct));

        Assert.Equal(ArchiveLimit.CompressionRatio, exception.Limit);
        Assert.Equal(new ArchiveParserOptions().MaxCompressionRatio, exception.Allowed);

        var received = Assert.Single(text.ReceivedMetadata);
        Assert.Equal("bundle.zip#zeros.txt", received.FileName);
        Assert.Contains(
            logger.Warnings,
            w => w.Contains("bundle.zip#zeros.txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// The order of refusal, end to end. One read of this entry passes both byte bounds at once, and
    /// the caller must be told about the ratio: it names an entry as malicious where the total only
    /// says the archive got too big.
    /// </summary>
    /// <remarks>
    /// This is the case the re-check gets wrong most easily. <see cref="LimitedReadStream"/> books the
    /// bytes against the total <i>before</i> it tests the ratio, so by the time the parser re-checks,
    /// both bounds are breached and whichever it tests first is what the caller sees. Before the
    /// whole-phase review it was the total, for the entry the ratio had actually stopped.
    /// </remarks>
    [Fact]
    public async Task AReadPassingBothByteBoundsIsReportedAsTheRatioCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var archive = ZipFixtureBuilder.Archive(("zeros.txt", ZipFixtureBuilder.Zeros(4096)));

        var both = await AssertRefusedAsync(
            archive, new ArchiveParserOptions { MaxTotalUncompressedBytes = 100, MaxCompressionRatio = 5 }, ct);

        Assert.Equal(ArchiveLimit.CompressionRatio, both.Limit);

        // The control, and the reason the assertion above is about ordering rather than about the
        // ratio being the only bound in reach: the same archive with the ratio lifted out of the way
        // trips the total, so the total was genuinely breached in the case above too.
        var totalOnly = await AssertRefusedAsync(
            archive,
            new ArchiveParserOptions
            {
                MaxTotalUncompressedBytes = 100,
                MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio,
            },
            ct);

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, totalOnly.Limit);
    }

    /// <summary>
    /// The low-ratio, high-total archive through the parser, which is where the budget's
    /// <i>lifetime</i> is decided. Four entries of 32 KB of incompressible bytes: no entry expands, no
    /// entry passes the 100,000-byte bound on its own, and together they pass it.
    /// </summary>
    /// <remarks>
    /// <see cref="LimitedReadStreamTests.ALowRatioHighTotalArchiveIsReportedAsTheTotalBytesCap"/>
    /// claimed to be "the test that catches a per-entry byte counter" and was not: it drives its own
    /// read loop and constructs the budget itself, so it cannot see how long the parser keeps one.
    /// This is that test.
    /// </remarks>
    [Fact]
    public async Task ALowRatioHighTotalArchiveIsRefusedThroughTheParser()
    {
        var ct = TestContext.Current.CancellationToken;
        var archive = ZipFixtureBuilder.Archive(
            ("a.txt", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 1)),
            ("b.txt", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 2)),
            ("c.txt", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 3)),
            ("d.txt", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 4)));

        var exception = await AssertRefusedAsync(
            archive, new ArchiveParserOptions { MaxTotalUncompressedBytes = 100_000 }, ct);

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
        Assert.Equal(100_000, exception.Allowed);
    }

    private static async Task<ArchiveLimitExceededException> AssertRefusedAsync(
        byte[] archiveBytes,
        ArchiveParserOptions options,
        CancellationToken cancellationToken)
    {
        var parser = new ZipDocumentParser([new RecordingParser("text/plain")], options: options);
        using var stream = new MemoryStream(archiveBytes, writable: false);

        return await Assert.ThrowsAsync<ArchiveLimitExceededException>(async () =>
            await parser.ParseAsync(stream, CreateMetadata(), cancellationToken)
                .ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Every extension this repository maps, so a content type added to <see cref="ContentTypeMap"/>
    /// is automatically covered by the drift assertion rather than written out twice.
    /// </summary>
    private static readonly string[] MappedExtensions =
    [
        ".pdf", ".html", ".htm", ".docx", ".xlsx", ".pptx", ".epub", ".eml", ".msg", ".zip",
        ".txt", ".md", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff",
        ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".mp4", ".mov", ".mkv", ".avi", ".webm",
        ".no-such-extension",
    ];

    /// <summary>
    /// Content types a zip parser is plausibly tempted into and that this repository maps <b>no</b>
    /// extension to, which is exactly why they have to be listed by hand.
    /// </summary>
    /// <remarks>
    /// Other archive containers first, since "it is an archive, so this parser should have it" is the
    /// over-claim that would actually get written — and <c>System.IO.Compression</c> reads none of
    /// them. Then the zip-shaped formats another parser owns or nobody does: a JAR, an APK and an
    /// ODF document are all zips, and answering their content type here would produce entry-by-entry
    /// rubbish for the same reason <c>application/epub+zip</c> would. Finally the generic
    /// "compressed" aliases, which are the ones a well-meaning alias list grows by.
    /// </remarks>
    private static readonly string[] NeighbouringContentTypes =
    [
        "application/x-7z-compressed",
        "application/x-tar",
        "application/gzip",
        "application/x-gzip",
        "application/vnd.rar",
        "application/x-rar-compressed",
        "application/x-bzip2",
        "application/java-archive",
        "application/vnd.android.package-archive",
        "application/vnd.oasis.opendocument.text",
        "application/x-compressed",
        "multipart/x-zip",
    ];

    /// <summary>
    /// The universe the claim-drift assertion runs over: everything this repository maps, everything
    /// the registration declared, and the near misses of <see cref="NeighbouringContentTypes"/>.
    /// </summary>
    /// <param name="claimed">The declared claims, so a claim for an unmapped type is checked too.</param>
    private static IReadOnlyCollection<string> CandidateContentTypes(IEnumerable<string> claimed)
    {
        var types = new HashSet<string>(claimed, StringComparer.OrdinalIgnoreCase);
        foreach (var extension in MappedExtensions)
        {
            types.Add(ContentTypeMap.FromFileName("file" + extension));
        }

        foreach (var contentType in NeighbouringContentTypes)
        {
            types.Add(contentType);
        }

        return types;
    }
}
