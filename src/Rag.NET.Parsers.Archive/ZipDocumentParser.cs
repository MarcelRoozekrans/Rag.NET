using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Archive;

/// <summary>
/// Parses <c>.zip</c> archives by decompressing each entry and handing it to whichever registered
/// parser claims its content type, through <see cref="ContainerEntryDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it claims, and what it must not.</b> <see cref="ContainerContentTypes.Zip"/> and
/// <see cref="ContainerContentTypes.ZipCompressed"/> — the latter is what older Windows and Internet
/// Explorer emit and is common in real mail. Deliberately <b>not</b> <c>application/epub+zip</c>: an
/// EPUB is a zip, but <c>EpubDocumentParser</c> owns it and a generic zip parser answering it would
/// emit entry-by-entry rubbish instead of chapters. Deliberately <b>not</b>
/// <c>application/octet-stream</c> either — nothing format-specific may answer "unknown binary", the
/// invariant Phase 3.11 made load-bearing and which is what turns an unclaimed entry into a warning
/// instead of a failure. Both are enforced rather than intended: <c>AddArchiveParser</c> declares a
/// <see cref="ParserClaim"/> per claimed type, and the startup guard fails on any overlap.
/// </para>
/// <para>
/// <b>Entry names are sanitised as naming hygiene, not as zip-slip mitigation.</b> Zip-slip is an
/// <i>extraction</i> vulnerability: <c>../../etc/passwd</c> is dangerous because an extractor writes
/// it to disk. This parser never touches the filesystem. Entries are decompressed into memory and
/// handed to another <see cref="IDocumentParser"/>, so a traversal-shaped entry name reaches
/// <see cref="DocumentMetadata.FileName"/> and nowhere else. <see cref="FileNameSanitizer"/> is still
/// applied, for the same reason it is applied to a mail subject — a name that reaches metadata should
/// be a clean name — but recording it as a closed vulnerability would leave a future reader believing
/// this parser was exposed to something it never was. See design §4.
/// </para>
/// <para>
/// <b>The bounds are counted, never read.</b> <see cref="ZipArchiveEntry.Length"/> comes from the
/// central directory, which is written by whoever built the archive, so every entry is read through a
/// <see cref="LimitedReadStream"/> that counts the bytes it actually produces. One
/// <see cref="ArchiveReadBudget"/> is shared by every entry of one archive: a per-entry counter would
/// enforce <c>cap × entries</c> rather than <c>cap</c>.
/// </para>
/// <para>
/// Nesting is bounded by the same depth counter and entry budget the email parsers use, carried
/// across the <c>IDocumentParser</c> boundary on <see cref="ContainerContext"/>'s reserved tags. A
/// <c>zip → .eml → zip</c> chain therefore spends one budget rather than two that each look correct
/// in isolation. <b>The byte total rides the same channel</b>, on
/// <see cref="ContainerByteBudget"/>: it was per-invocation until the phase's whole-phase review, so
/// a nested archive got a fresh allowance and cost its parent only its compressed size — the
/// <c>cap × entries</c> hole the paragraph above refuses, reintroduced one level up.
/// </para>
/// </remarks>
public sealed class ZipDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    ILogger<ZipDocumentParser>? logger = null,
    ArchiveParserOptions? options = null) : IDocumentParser
{
    internal const string ZipContentType = ContainerContentTypes.Zip;
    internal const string ZipCompressedContentType = ContainerContentTypes.ZipCompressed;

    /// <summary>Joins the archive's name to an entry's, mirroring <c>parent.eml#child.eml</c>.</summary>
    private const string Separator = "#";

    /// <summary>Used when an entry name sanitises away to nothing at all.</summary>
    private const string EntryFallbackName = "archive-entry";

    private readonly ArchiveParserOptions options = options ?? new ArchiveParserOptions();
    private readonly ArchiveContainerLog? containerLog = ArchiveContainerLog.For(logger);

    public bool CanParse(string contentType) =>
        contentType.Equals(ZipContentType, StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals(ZipCompressedContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Streams the sections of every entry a registered parser claims.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">
    /// Any of the three bounds was passed: the archive declares more entries than
    /// <see cref="ArchiveParserOptions.MaxEntries"/> allows, an entry expanded past
    /// <see cref="ArchiveParserOptions.MaxCompressionRatio"/>, or the archive decompressed past
    /// <see cref="ArchiveParserOptions.MaxTotalUncompressedBytes"/>. The entry count is refused before
    /// any entry is read; the two byte bounds are re-checked after each entry, because the refusal
    /// <see cref="LimitedReadStream"/> raises is contained by
    /// <see cref="ContainerEntryDispatcher"/> along with everything else an entry parser throws.
    /// </exception>
    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = ContainerContext.Create(metadata, options.ToContainerLimits());
        var readBudget = new ArchiveReadBudget(options, context.Bytes);

        // leaveOpen: the stream belongs to whoever handed it over — the ingestion pipeline at the top
        // level, ContainerEntryDispatcher for a nested archive — and both dispose it themselves.
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ThrowIfTooManyEntries(archive.Entries.Count);

        int sectionIndex = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSkipped(entry))
                continue;

            await foreach (var section in DispatchEntryAsync(entry, context, readBudget, cancellationToken)
                .ConfigureAwait(false))
            {
                // SectionIndex is stamped exactly once, here. Entry parsers number their own sections
                // from zero and know nothing of the archive they were opened from.
                yield return section with { SectionIndex = sectionIndex++ };
            }

            // The dispatcher contains everything an entry parser throws, which is right — it cannot
            // tell a decompression bomb from a corrupt PDF, and one bad entry must not cost the
            // archive. But that swallows LimitedReadStream's refusal too, so *both* byte bounds are
            // re-checked here, where refusing the archive is this parser's decision rather than the
            // shared machinery's. Without it a bomb would degrade into a warning per entry.
            readBudget.ThrowIfExceeded();
        }
    }

    /// <summary>
    /// Decompresses one entry through a <see cref="LimitedReadStream"/> and dispatches it by content
    /// type.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="ParseAsync"/> because MA0051 caps a method at 60 lines, and split
    /// here rather than elsewhere because this is the part that owns a disposable per entry.
    /// </remarks>
    private async IAsyncEnumerable<DocumentSection> DispatchEntryAsync(
        ZipArchiveEntry entry,
        ContainerContext context,
        ArchiveReadBudget readBudget,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The content type comes from the entry's own name rather than from the sanitised one: the
        // sanitiser caps length, so a pathological name could lose its extension, and typing an entry
        // is not something a display concern should be able to change.
        var contentType = ContentTypeMap.FromFileName(entry.FullName);
        var entryName = ComposeEntryName(context.Metadata.FileName, entry.FullName);

        var limited = new LimitedReadStream(entry.Open(), entry.CompressedLength, readBudget);
        await using (limited.ConfigureAwait(false))
        {
            await foreach (var section in ContainerEntryDispatcher.DispatchAsync(
                parsers, entryName, contentType, limited, context, containerLog, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }

    /// <summary>Builds <c>archive.zip#report.pdf</c>. See the type's remarks on why this is hygiene.</summary>
    private static string ComposeEntryName(string archiveFileName, string entryFullName) =>
        string.Concat(archiveFileName, Separator, FileNameSanitizer.Sanitize(entryFullName, EntryFallbackName));

    /// <summary>
    /// Reports whether an entry is one there is nothing to parse in: a directory, or empty.
    /// </summary>
    /// <remarks>
    /// <see cref="ZipArchiveEntry.Length"/> is the declared length and is attacker-controlled, so this
    /// is the one place the central directory is believed — and believing it here can only cause
    /// <i>less</i> work. An entry that lies about being empty hides itself, which it could equally do
    /// by not being in the archive; an entry that lies about being non-empty is read through
    /// <see cref="LimitedReadStream"/> like any other.
    /// </remarks>
    private static bool IsSkipped(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.Length == 0;

    /// <summary>
    /// Refuses an archive declaring more entries than <see cref="ArchiveParserOptions.MaxEntries"/>.
    /// </summary>
    /// <remarks>
    /// Checked <i>after</i> <c>ZipArchive.Entries</c> has been read rather than during, because that
    /// property parses the whole central directory on first access. A million-entry directory is
    /// therefore read before the count is rejected — bounded by the archive the caller already holds
    /// in a stream, so not a new exposure, but not streaming either. Recorded rather than hidden; see
    /// design §3.
    /// </remarks>
    private void ThrowIfTooManyEntries(int declaredEntries)
    {
        if (declaredEntries > options.MaxEntries)
            throw new ArchiveLimitExceededException(ArchiveLimit.EntryCount, declaredEntries, options.MaxEntries);
    }
}
