using System.Globalization;

namespace Rag.NET.Parsers.Archive;

/// <summary>
/// Thrown when an archive exceeds one of <see cref="ArchiveParserOptions"/>' bounds, naming which.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated type rather than the <see cref="InvalidDataException"/> <c>System.IO.Compression</c>
/// already raises, because "this archive is a decompression bomb" and "this archive is corrupt" call
/// for different responses. It derives from <see cref="IOException"/> — not from
/// <see cref="InvalidDataException"/>, which is <see langword="sealed"/> — so a caller that only
/// wants to reject unreadable input still catches it without knowing this package exists.
/// </para>
/// <para>
/// <see cref="Limit"/> is <see langword="null"/> on an instance built through the standard
/// message-only constructors. Those exist because a public exception without them is awkward to use
/// and the analyzers say so; every throw inside this package uses
/// <see cref="ArchiveLimitExceededException(ArchiveLimit, long, long)"/>, which always sets it.
/// </para>
/// </remarks>
public sealed class ArchiveLimitExceededException : IOException
{
    /// <summary>Creates an instance carrying no limit. See the type's remarks.</summary>
    public ArchiveLimitExceededException()
    {
    }

    /// <summary>Creates an instance carrying no limit. See the type's remarks.</summary>
    /// <param name="message">The message.</param>
    public ArchiveLimitExceededException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates an instance carrying no limit. See the type's remarks.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ArchiveLimitExceededException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an instance carrying no limit. See the type's remarks.</summary>
    /// <param name="message">The message.</param>
    /// <param name="hresult">The HRESULT to report, mirroring <see cref="IOException"/>.</param>
    public ArchiveLimitExceededException(string? message, int hresult)
        : base(message, hresult)
    {
    }

    /// <summary>Creates an instance naming the bound that was exceeded.</summary>
    /// <param name="limit">Which bound fired.</param>
    /// <param name="observed">What was measured — bytes read, the expansion factor, or the entry count.</param>
    /// <param name="allowed">The configured bound <paramref name="observed"/> passed.</param>
    public ArchiveLimitExceededException(ArchiveLimit limit, long observed, long allowed)
        : base(BuildMessage(limit, observed, allowed))
    {
        Limit = limit;
        Observed = observed;
        Allowed = allowed;
    }

    /// <summary>Gets which bound was exceeded, or <see langword="null"/>. See the type's remarks.</summary>
    public ArchiveLimit? Limit { get; }

    /// <summary>Gets what was measured when <see cref="Limit"/> fired.</summary>
    public long Observed { get; }

    /// <summary>Gets the bound <see cref="Observed"/> passed.</summary>
    public long Allowed { get; }

    private static string BuildMessage(ArchiveLimit limit, long observed, long allowed) => limit switch
    {
        ArchiveLimit.TotalUncompressedBytes =>
            $"The archive decompressed to more than {Text(allowed)} bytes in total "
            + $"({Text(observed)} read so far), the bound set by "
            + $"{nameof(ArchiveParserOptions)}.{nameof(ArchiveParserOptions.MaxTotalUncompressedBytes)}. "
            + "Counted as the bytes were read, because a declared size in the archive's central "
            + "directory is written by whoever built the archive.",
        ArchiveLimit.CompressionRatio =>
            $"An archive entry expanded past {Text(allowed)}:1 (about {Text(observed)}:1 so far), the "
            + $"bound set by "
            + $"{nameof(ArchiveParserOptions)}.{nameof(ArchiveParserOptions.MaxCompressionRatio)}.",
        ArchiveLimit.EntryCount =>
            $"The archive declares {Text(observed)} entries, more than the "
            + $"{nameof(ArchiveParserOptions)}.{nameof(ArchiveParserOptions.MaxEntries)} bound of "
            + $"{Text(allowed)}.",
        _ => $"The archive exceeded a bound: {Text(observed)} against {Text(allowed)}.",
    };

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
