namespace Rag.NET.Parsers.Archive;

/// <summary>
/// One archive's view of the document-wide decompression total, shared by every entry read through a
/// <see cref="LimitedReadStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared on purpose, and that is the whole reason this type exists rather than a field on the
/// stream.</b> <see cref="ArchiveParserOptions.MaxTotalUncompressedBytes"/> is a bound on the
/// document, not on an entry: a thousand entries of 2 MB each, all of them at an unremarkable 2:1,
/// trip no per-entry check and together cost 2 GB. A counter that reset per entry would enforce
/// <c>cap × entries</c> instead of <c>cap</c> — the same shape of hole
/// <c>ContainerBudget</c> documents for the nesting budget, in a different place.
/// </para>
/// <para>
/// <b>The count itself is not held here, and the phase's whole-phase review is why.</b> It lives on
/// <see cref="ContainerByteBudget"/>, which rides <c>ContainerContext</c>'s reserved tags across the
/// <c>IDocumentParser</c> boundary. A per-invocation counter was a counter that <i>reset per
/// archive</i>: a nested zip re-entered through <c>ContainerEntryDispatcher</c>, got a fresh one, and
/// cost its parent only its small compressed size — <c>cap × entries</c> refused one level down and
/// reintroduced one level up, worth roughly <c>51 × cap</c> at the default nesting budget. This type
/// now holds the bounds and the verdict; the number is the document's.
/// </para>
/// <para>
/// <b>It is also where a breach is remembered, not only where it is counted.</b>
/// <c>ContainerEntryDispatcher</c> catches everything an entry parser throws, so a refusal raised
/// inside <see cref="LimitedReadStream"/> never reaches <c>ZipDocumentParser</c> as an exception. The
/// total survives that containment because it is a running count the parser can re-read; the ratio is
/// a per-read test with nothing left behind, so it is recorded here instead. Both are then re-raised
/// by <see cref="ThrowIfExceeded"/> after each entry.
/// </para>
/// <para>
/// <b>The ratio breach deliberately does not cross the boundary, where the total does.</b> The total
/// is a resource the whole document spends and a parent has to be told about; the ratio is a verdict
/// on one archive's entry, and a nested archive that refuses itself, contained by its parent, is the
/// same boundary a zip nested in an <c>.eml</c> already has. The parent still refuses once the shared
/// total is passed, so nothing is lost by leaving the verdict local.
/// </para>
/// <para>
/// One instance per archive, created by the parser before it enumerates entries. Not thread-safe:
/// entries are read one at a time on the caller's thread, and making the counter concurrent would
/// suggest an interleaving the parser does not produce.
/// </para>
/// </remarks>
internal sealed class ArchiveReadBudget
{
    private readonly ContainerByteBudget documentBytes;

    /// <summary>
    /// The expansion factor of the first entry that breached the ratio bound, or <see langword="null"/>
    /// while none has. See the type's remarks on why a breach has to outlive the exception.
    /// </summary>
    private long? breachedRatio;

    /// <summary>Creates the budget one archive is read against.</summary>
    /// <param name="options">The bounds, read once.</param>
    /// <param name="documentBytes">
    /// The document-wide running total this archive books its reads against, normally
    /// <c>ContainerContext.Bytes</c>. Omitted only where there is no surrounding document — the
    /// stream's own unit tests — in which case this archive starts the count at zero.
    /// </param>
    public ArchiveReadBudget(ArchiveParserOptions options, ContainerByteBudget? documentBytes = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Read once. The options instance registered in DI is the one the parser captured, so a later
        // mutation must not move a bound out from under an archive that is already half-read.
        MaxTotalUncompressedBytes = options.MaxTotalUncompressedBytes;
        MaxCompressionRatio = options.MaxCompressionRatio;
        this.documentBytes = documentBytes ?? new ContainerByteBudget(0, null);
    }

    /// <summary>Gets the total-bytes bound this archive is read against.</summary>
    public long MaxTotalUncompressedBytes { get; }

    /// <summary>Gets the per-entry expansion bound entries of this archive are read against.</summary>
    public int MaxCompressionRatio { get; }

    /// <summary>
    /// Gets the bytes decompressed so far — across every entry of this archive <i>and</i> every
    /// container the document already spent before this archive was opened.
    /// </summary>
    public long TotalUncompressedBytes => documentBytes.Spent;

    /// <summary>Adds bytes that have <b>already been read</b> to the running total.</summary>
    /// <param name="count">The byte count a read returned.</param>
    /// <remarks>
    /// Deliberately does not throw. Accounting and refusal are separate calls so the caller controls
    /// which bound is reported when a single read breaches more than one — see
    /// <see cref="LimitedReadStream"/>.
    /// </remarks>
    public void Add(int count) => documentBytes.Add(count);

    /// <summary>Remembers that an entry expanded past <see cref="MaxCompressionRatio"/>.</summary>
    /// <param name="observedRatio">The expansion factor that entry had reached.</param>
    /// <remarks>
    /// The first breach wins. A later entry that also expands too far says nothing new about the
    /// archive, and the number an operator is shown should be the one that actually stopped the read.
    /// </remarks>
    public void RecordRatioBreach(long observedRatio) => breachedRatio ??= observedRatio;

    /// <summary>Refuses the archive if either byte bound has been passed.</summary>
    /// <exception cref="ArchiveLimitExceededException">
    /// An entry expanded past <see cref="MaxCompressionRatio"/>, or more bytes have been read than
    /// <see cref="MaxTotalUncompressedBytes"/> allows.
    /// </exception>
    /// <remarks>
    /// <b>Ratio first, and the order matters more here than it does in
    /// <see cref="LimitedReadStream"/>.</b> The bytes of a breaching read are booked against the total
    /// before the ratio is tested, so a single read that passes both bounds leaves both recorded.
    /// Testing the total first would report the vaguer diagnosis — "the archive got too big" — for an
    /// archive that is specifically a bomb.
    /// </remarks>
    public void ThrowIfExceeded()
    {
        if (breachedRatio is { } observed)
            throw new ArchiveLimitExceededException(ArchiveLimit.CompressionRatio, observed, MaxCompressionRatio);

        ThrowIfTotalExceeded();
    }

    /// <summary>Refuses the archive if the running total has passed its bound.</summary>
    /// <exception cref="ArchiveLimitExceededException">
    /// More bytes have been read than <see cref="MaxTotalUncompressedBytes"/> allows.
    /// </exception>
    public void ThrowIfTotalExceeded()
    {
        if (TotalUncompressedBytes > MaxTotalUncompressedBytes)
        {
            throw new ArchiveLimitExceededException(
                ArchiveLimit.TotalUncompressedBytes, TotalUncompressedBytes, MaxTotalUncompressedBytes);
        }
    }
}
