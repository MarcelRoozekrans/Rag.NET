using System.Globalization;

namespace Rag.NET.DataProviders;

/// <summary>
/// Parses a connector-supplied timestamp string into the typed <see cref="FileHandle.CreatedAt"/>/
/// <see cref="FileHandle.UpdatedAt"/> channel.
/// </summary>
/// <remarks>
/// Every source API used by the connectors in this repository (Asana <c>modified_at</c>, Jira
/// <c>updated</c>, Notion <c>last_edited_time</c>, Zendesk <c>updated_at</c>) documents its
/// timestamps as ISO-8601, so <see cref="DateTimeStyles.RoundtripKind"/> is the right style: it
/// preserves a <c>Z</c> suffix as <see cref="DateTimeKind.Utc"/> rather than silently converting
/// to the machine's local time zone.
/// <para>
/// <b>Failure is silent by design.</b> A value that does not parse becomes <see langword="null"/>,
/// not an exception and not a best-effort guess. An unset timestamp is a truthful "unknown"; a
/// misparsed one is a fabricated fact that <c>TimeWeightedRetriever</c> would then rank on with no
/// way to tell it apart from a real one.
/// </para>
/// </remarks>
public static class ConnectorTimestampParser
{
    /// <summary>
    /// Parses <paramref name="value"/> as an ISO-8601 timestamp.
    /// </summary>
    /// <param name="value">The raw string a connector read off the wire, or <see langword="null"/>.</param>
    /// <returns>
    /// The parsed <see cref="DateTime"/>, or <see langword="null"/> when <paramref name="value"/>
    /// is null/empty or does not parse.
    /// </returns>
    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
