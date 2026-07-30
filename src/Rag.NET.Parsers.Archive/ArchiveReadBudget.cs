namespace Rag.NET.Parsers.Archive;

/// <summary>
/// The running total of bytes decompressed from one archive, shared by every entry read through a
/// <see cref="LimitedReadStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared on purpose, and that is the whole reason this type exists rather than a field on the
/// stream.</b> <see cref="ArchiveParserOptions.MaxTotalUncompressedBytes"/> is a bound on the
/// archive, not on an entry: a thousand entries of 2 MB each, all of them at an unremarkable 2:1,
/// trip no per-entry check and together cost 2 GB. A counter that reset per entry would enforce
/// <c>cap × entries</c> instead of <c>cap</c> — the same shape of hole
/// <c>ContainerBudget</c> documents for the nesting budget, in a different place.
/// </para>
/// <para>
/// One instance per archive, created by the parser before it enumerates entries. Not thread-safe:
/// entries are read one at a time on the caller's thread, and making the counter concurrent would
/// suggest an interleaving the parser does not produce.
/// </para>
/// </remarks>
internal sealed class ArchiveReadBudget
{
    public ArchiveReadBudget(ArchiveParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Read once. The options instance registered in DI is the one the parser captured, so a later
        // mutation must not move a bound out from under an archive that is already half-read.
        MaxTotalUncompressedBytes = options.MaxTotalUncompressedBytes;
        MaxCompressionRatio = options.MaxCompressionRatio;
    }

    /// <summary>Gets the total-bytes bound this archive is read against.</summary>
    public long MaxTotalUncompressedBytes { get; }

    /// <summary>Gets the per-entry expansion bound entries of this archive are read against.</summary>
    public int MaxCompressionRatio { get; }

    /// <summary>Gets the bytes read from this archive so far, across every entry.</summary>
    public long TotalUncompressedBytes { get; private set; }

    /// <summary>Adds bytes that have <b>already been read</b> to the running total.</summary>
    /// <param name="count">The byte count a read returned.</param>
    /// <remarks>
    /// Deliberately does not throw. Accounting and refusal are separate calls so the caller controls
    /// which bound is reported when a single read breaches more than one — see
    /// <see cref="LimitedReadStream"/>.
    /// </remarks>
    public void Add(int count) => TotalUncompressedBytes += count;

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
