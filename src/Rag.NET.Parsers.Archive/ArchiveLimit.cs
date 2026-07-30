namespace Rag.NET.Parsers.Archive;

/// <summary>
/// Which of <see cref="ArchiveParserOptions"/>' three bounds an archive exceeded.
/// </summary>
/// <remarks>
/// Carried on <see cref="ArchiveLimitExceededException"/> so a caller — and a test — can assert
/// <i>which</i> bound fired rather than merely that something did. A bomb that trips the wrong cap is
/// a test passing for the wrong reason: a 10 GB archive of low-ratio entries stopped by a ratio check
/// would mean the ratio check is miscalculating, not that the archive was caught for the reason it
/// was built to be caught for.
/// </remarks>
public enum ArchiveLimit
{
    /// <summary>
    /// <see cref="ArchiveParserOptions.MaxTotalUncompressedBytes"/> — bytes actually read across
    /// every entry of one archive. The bound that catches the low-ratio, high-total archive no ratio
    /// check can see.
    /// </summary>
    TotalUncompressedBytes,

    /// <summary>
    /// <see cref="ArchiveParserOptions.MaxCompressionRatio"/> — one entry expanding further than its
    /// compressed size should allow.
    /// </summary>
    CompressionRatio,

    /// <summary>
    /// <see cref="ArchiveParserOptions.MaxEntries"/> — more entries than the archive is permitted to
    /// contain. Raised by the parser after reading the central directory, not by the read-limiting
    /// stream, which sees one entry at a time and knows nothing of how many there are.
    /// </summary>
    EntryCount,
}
