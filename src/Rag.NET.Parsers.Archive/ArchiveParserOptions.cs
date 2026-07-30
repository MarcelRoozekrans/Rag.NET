using System.Globalization;

namespace Rag.NET.Parsers.Archive;

/// <summary>
/// Bounds on how much a single archive may cost to read: total bytes decompressed, how far any one
/// entry may expand, and how many entries are admitted.
/// </summary>
/// <remarks>
/// <para>
/// All three are safety bounds, not preferences. <c>ZipArchiveEntry.Length</c> is read from the
/// archive's <b>central directory, which is attacker-controlled</b> — a decompression bomb declares
/// whatever size flatters it — so nothing here may be enforced by pre-flighting a declared size. The
/// byte bounds are enforced by counting bytes <i>actually read</i> through <c>LimitedReadStream</c>.
/// </para>
/// <para>
/// <b>Three bounds rather than two.</b> A 10 GB file of zeros at 1000:1 and a 10 GB file at 2:1 are
/// both fatal, and the second passes any ratio check that would stop the first.
/// <see cref="MaxTotalUncompressedBytes"/> catches what <see cref="MaxCompressionRatio"/> cannot, and
/// <see cref="MaxEntries"/> catches the archive that is cheap per entry and ruinous in aggregate.
/// </para>
/// <para>
/// Each property has a <c>public const</c> ceiling and a <b>clamping</b> setter, so no construction
/// path can exceed it — not <c>new ZipDocumentParser(…, options)</c>, and not mutating the options
/// instance resolved from DI after registration validated it. The unclamped request is remembered
/// (the <c>Requested…</c> properties) so <c>AddArchiveParser</c> can <b>throw</b> on it rather than
/// clamp it silently: a caller who asked for a 4 GB budget and received 2 GB should be told, not
/// quietly corrected.
/// </para>
/// </remarks>
public sealed class ArchiveParserOptions
{
    /// <summary>
    /// Hard ceiling on <see cref="MaxTotalUncompressedBytes"/> — 2 GB. Absolute: the setter clamps to
    /// it, and <c>AddArchiveParser</c> throws on a larger request.
    /// </summary>
    /// <remarks>
    /// One archive is decompressed entry by entry into memory before dispatch, so the total is the
    /// amount of work a single document may ask a host to do. 2 GB is also where
    /// <see cref="int"/>-shaped buffer arithmetic elsewhere in the BCL stops being obviously safe, so
    /// it is a ceiling with two reasons rather than a round number with one.
    /// </remarks>
    public const long MaxSupportedTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hard ceiling on <see cref="MaxCompressionRatio"/> — 1000:1. Absolute, on the same terms as
    /// <see cref="MaxSupportedTotalUncompressedBytes"/>.
    /// </summary>
    /// <remarks>
    /// Deflate's theoretical maximum is roughly 1032:1, which is what a megabyte of zero bytes very
    /// nearly achieves. A ceiling at 1000 therefore leaves no configuration in which the ratio bound
    /// is effectively disabled: above it there is nothing left for an attacker to reach.
    /// </remarks>
    public const int MaxSupportedCompressionRatio = 1000;

    /// <summary>
    /// Hard ceiling on <see cref="MaxEntries"/> — 65,535, the entry count the original ZIP central
    /// directory can express in its 16-bit field. Absolute, on the same terms as
    /// <see cref="MaxSupportedTotalUncompressedBytes"/>.
    /// </summary>
    /// <remarks>
    /// ZIP64 raises that field, so this is a policy bound rather than a format bound: an archive with
    /// more entries than the classic format could count is not mail this parser is for.
    /// </remarks>
    public const int MaxSupportedEntries = 65_535;

    private const long DefaultTotalUncompressedBytes = 256L * 1024 * 1024;
    private const int DefaultCompressionRatio = 100;
    private const int DefaultEntries = 1_024;
    private const int DefaultNestingDepth = 3;
    private const int DefaultNestedContainers = 50;

    private long _maxTotalUncompressedBytes = DefaultTotalUncompressedBytes;
    private int _maxCompressionRatio = DefaultCompressionRatio;
    private int _maxEntries = DefaultEntries;

    /// <summary>
    /// Total bytes decompressed across every entry of one archive, counted as they are read. Default
    /// 256 MB.
    /// </summary>
    /// <remarks>
    /// The bound two caps would miss is the <b>low-ratio, high-total</b> archive: a thousand entries
    /// that each expand 2:1 trip no ratio check and no per-entry check, and together exhaust the host.
    /// The setter clamps to <see cref="MaxSupportedTotalUncompressedBytes"/>; see
    /// <see cref="RequestedMaxTotalUncompressedBytes"/> for why the request is still remembered.
    /// </remarks>
    public long MaxTotalUncompressedBytes
    {
        get => _maxTotalUncompressedBytes;
        set
        {
            RequestedMaxTotalUncompressedBytes = value;
            _maxTotalUncompressedBytes = value > MaxSupportedTotalUncompressedBytes
                ? MaxSupportedTotalUncompressedBytes
                : value;
        }
    }

    /// <summary>
    /// Largest expansion factor any single entry may reach — uncompressed bytes actually read divided
    /// by the entry's compressed size. Default 100:1.
    /// </summary>
    /// <remarks>
    /// The denominator is the archive's own <c>CompressedLength</c> and is therefore
    /// attacker-controlled too, but <b>the lie fails safe</b>: understating it makes the computed
    /// ratio higher and trips this bound earlier. Overstating it is bounded by the archive the caller
    /// already holds — the compressed bytes have to exist to be claimed.
    /// </remarks>
    public int MaxCompressionRatio
    {
        get => _maxCompressionRatio;
        set
        {
            RequestedMaxCompressionRatio = value;
            _maxCompressionRatio = value > MaxSupportedCompressionRatio ? MaxSupportedCompressionRatio : value;
        }
    }

    /// <summary>
    /// Number of entries admitted from one archive. Default 1,024.
    /// </summary>
    /// <remarks>
    /// Checked after <c>ZipArchive.Entries</c> has been read, not during: that property reads the
    /// entire central directory on first access, so a million-entry directory is parsed before the
    /// count is rejected. Bounded by the archive the caller already holds in a stream, so it is not a
    /// new exposure — but it is not streaming either, and it is recorded rather than hidden.
    /// </remarks>
    public int MaxEntries
    {
        get => _maxEntries;
        set
        {
            RequestedMaxEntries = value;
            _maxEntries = value > MaxSupportedEntries ? MaxSupportedEntries : value;
        }
    }

    /// <summary>
    /// How deep a chain of nested containers this parser follows below the archive it was handed.
    /// <c>0</c> disables descent entirely, and does so silently. Default <c>3</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default matches <c>EmailParserOptions.MaxEmbeddedDepth</c> deliberately.</b> Depth is
    /// carried across the <c>IDocumentParser</c> boundary on <see cref="ContainerContext"/>'s reserved
    /// tags, so a <c>zip → .eml → zip</c> chain is counted by whichever parser owns each level. If the
    /// two packages defaulted differently, a chain that alternates formats would be bounded by
    /// whichever happened to be lower at each hop rather than by a number a reader can predict.
    /// Design §5's claim — that an alternating chain is bounded by the same numbers as one that does
    /// not alternate — is only true while these defaults agree.
    /// </para>
    /// </remarks>
    public int MaxNestingDepth { get; set; } = DefaultNestingDepth;

    /// <summary>
    /// How many nested containers may be entered across the whole document, as a total rather than
    /// per branch. Default <c>50</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not <see cref="MaxEntries"/>, and the two are easy to confuse.</b> <see cref="MaxEntries"/>
    /// bounds how many entries <i>one archive</i> may declare, of any kind. This bounds how many of
    /// those entries — across every archive and every message in the document — may themselves be
    /// containers that get parsed in turn. A zip of 900 PDFs spends 900 of the first and none of the
    /// second.
    /// </para>
    /// <para>
    /// Shared with the email parsers through <see cref="ContainerBudget"/>, which is what makes the
    /// bound a total rather than <c>cap ^ depth</c>. Defaulted to
    /// <c>EmailParserOptions.MaxEmbeddedMessages</c> for the reason given on
    /// <see cref="MaxNestingDepth"/>.
    /// </para>
    /// </remarks>
    public int MaxNestedContainers { get; set; } = DefaultNestedContainers;

    /// <summary>
    /// Projects these options onto the format-neutral bounds the shared container machinery takes.
    /// </summary>
    /// <remarks>
    /// One mapping point, mirroring <c>EmailParserOptions.ToContainerLimits</c>, so the two properties
    /// cannot drift apart from the limits they feed.
    /// </remarks>
    internal ContainerLimits ToContainerLimits() => new(MaxNestingDepth, MaxNestedContainers);

    /// <summary>
    /// The last value assigned to <see cref="MaxTotalUncompressedBytes"/>, before clamping. Exists
    /// only so <c>AddArchiveParser</c> can still see — and throw on — a request the setter has
    /// already made safe; nothing at parse time reads it.
    /// </summary>
    internal long RequestedMaxTotalUncompressedBytes { get; private set; } = DefaultTotalUncompressedBytes;

    /// <summary>
    /// The last value assigned to <see cref="MaxCompressionRatio"/>, before clamping. See
    /// <see cref="RequestedMaxTotalUncompressedBytes"/>.
    /// </summary>
    internal int RequestedMaxCompressionRatio { get; private set; } = DefaultCompressionRatio;

    /// <summary>
    /// The last value assigned to <see cref="MaxEntries"/>, before clamping. See
    /// <see cref="RequestedMaxTotalUncompressedBytes"/>.
    /// </summary>
    internal int RequestedMaxEntries { get; private set; } = DefaultEntries;

    /// <summary>
    /// Throws if any bound was asked for beyond its ceiling, or asked for as a negative number.
    /// </summary>
    /// <param name="paramName">
    /// The registration parameter to blame — <c>nameof(configure)</c> from <c>AddArchiveParser</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>This is the registration-time half of the ceiling and it lives here, unlike
    /// <c>EmailParserOptions</c>, whose equivalent lives in <c>AddEmailParser</c>.</b> The bounds and
    /// the message that explains them are one thing; splitting them would let a future ceiling change
    /// land on the const without the message that quotes it. <c>AddArchiveParser</c> calls this and
    /// adds nothing of its own.
    /// </para>
    /// <para>
    /// Every check reads a <c>Requested…</c> property rather than the public one. The setters already
    /// clamped the effective values — that is what makes the ceilings absolute on every construction
    /// path — so the public properties can no longer be out of range and validating them would never
    /// fire. The two are equal unless the caller asked for more than the ceiling, which is exactly the
    /// case that must throw.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A bound is negative or exceeds its ceiling.</exception>
    internal void ThrowIfOutOfRange(string paramName)
    {
        ThrowIfNegative(RequestedMaxTotalUncompressedBytes, nameof(MaxTotalUncompressedBytes), paramName);
        ThrowIfNegative(RequestedMaxCompressionRatio, nameof(MaxCompressionRatio), paramName);
        ThrowIfNegative(RequestedMaxEntries, nameof(MaxEntries), paramName);

        // No ceiling on these two, so the requested value is the effective one and there is nothing
        // to read a Requested… property back from. A negative nesting bound is still nonsense rather
        // than harmless: it would read as "descend nowhere" while looking like a number.
        ThrowIfNegative(MaxNestingDepth, nameof(MaxNestingDepth), paramName);
        ThrowIfNegative(MaxNestedContainers, nameof(MaxNestedContainers), paramName);

        ThrowIfAboveCeiling(
            RequestedMaxTotalUncompressedBytes,
            MaxSupportedTotalUncompressedBytes,
            nameof(MaxTotalUncompressedBytes),
            paramName,
            "Each entry is decompressed into memory before it is dispatched, so this is the total work "
                + "one document may ask a host to do.");

        ThrowIfAboveCeiling(
            RequestedMaxCompressionRatio,
            MaxSupportedCompressionRatio,
            nameof(MaxCompressionRatio),
            paramName,
            "Deflate tops out near 1032:1, so a larger bound is one no archive can reach — it disables "
                + "ratio detection rather than relaxing it.");

        ThrowIfAboveCeiling(
            RequestedMaxEntries,
            MaxSupportedEntries,
            nameof(MaxEntries),
            paramName,
            "That is the entry count the classic ZIP central directory can express; more than that is "
                + "not mail this parser is for.");
    }

    /// <summary>
    /// Builds the out-of-range message. The ceiling appears in it <b>once</b>, and the number is
    /// formatted invariantly so an assertion on it does not depend on the machine's culture.
    /// </summary>
    private static void ThrowIfAboveCeiling(long requested, long ceiling, string propertyName, string paramName, string why)
    {
        if (requested > ceiling)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                requested,
                $"{nameof(ArchiveParserOptions)}.{propertyName} must not exceed "
                    + $"{ceiling.ToString(CultureInfo.InvariantCulture)}. {why}");
        }
    }

    private static void ThrowIfNegative(long value, string propertyName, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{nameof(ArchiveParserOptions)}.{propertyName} must not be negative.");
        }
    }
}
