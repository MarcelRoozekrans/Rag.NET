using System.Globalization;

namespace Rag.NET;

/// <summary>
/// The single definition of how a <see cref="Rag.NET.Models.MetadataValueKind.DateTimeOffset"/>
/// metadata value looks as text, everywhere one has to travel as a string.
/// <para>
/// Two forms exist. The <b>plain form</b> (<see cref="Format"/>) is UTC ISO-8601 and is used
/// where surrounding structure already says "this is a date" — the <c>{"$date": "…"}</c> wrapper
/// in serialized metadata JSON and Azure AI Search's typed <c>dateValue</c> field. The
/// <b>sentinel form</b> (<see cref="EncodeSentinel"/>) prefixes <c>$date:</c> and is used where a
/// store offers only untyped strings for a value that must still read back as a date and must
/// never equal a same-looking plain string — the per-key native metadata of Chroma and Pinecone,
/// and the per-key <c>meta_*</c> filter copies of Qdrant and Weaviate.
/// </para>
/// <para>
/// Formatting normalises to UTC: the instant round-trips exactly, the original offset does not.
/// That is deliberate — it makes equality filtering on dates work as instant comparison in every
/// store, instead of failing whenever the writer's and the filterer's offsets differ.
/// </para>
/// </summary>
internal static class MetadataDateFormat
{
    /// <summary>Prefix marking a string-encoded date in stores without a native date type.</summary>
    internal const string SentinelPrefix = "$date:";

    /// <summary>Property name wrapping a date in serialized metadata JSON: <c>{"$date": "…"}</c>.</summary>
    internal const string JsonPropertyName = "$date";

    /// <summary>Formats as UTC ISO-8601 (round-trip "O" format, always ending in <c>Z</c>).</summary>
    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Formats as <c>$date:</c> + UTC ISO-8601, for stores without a native date type.</summary>
    internal static string EncodeSentinel(DateTimeOffset value) =>
        SentinelPrefix + Format(value);

    /// <summary>Parses a plain-form date string (any ISO-8601 offset is accepted).</summary>
    internal static bool TryParse(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);

    /// <summary>
    /// Decodes the sentinel form. False when <paramref name="text"/> lacks the prefix or the
    /// remainder does not parse — such a value stays an ordinary string.
    /// </summary>
    internal static bool TryDecodeSentinel(string text, out DateTimeOffset value)
    {
        if (text.StartsWith(SentinelPrefix, StringComparison.Ordinal)
            && TryParse(text[SentinelPrefix.Length..], out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
