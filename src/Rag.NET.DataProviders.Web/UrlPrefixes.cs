namespace Rag.NET.DataProviders.Web;

/// <summary>
/// The URL-prefix rule shared by <see cref="SitemapOptions"/> and <see cref="WebCrawlerOptions"/>:
/// whole-URL prefixes, case-insensitive, blanks dropped.
/// </summary>
internal static class UrlPrefixes
{
    /// <summary>
    /// The prefixes, trimmed and with blanks dropped. A blank prefix matches every URL, so
    /// honouring one would silently turn a filter into a no-op or empty the result.
    /// </summary>
    public static IReadOnlyList<string> Normalise(IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0)
        {
            return [];
        }

        var kept = new List<string>(prefixes.Count);
        foreach (var prefix in prefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                kept.Add(prefix.Trim());
            }
        }

        return kept;
    }

    /// <summary>
    /// Whether <paramref name="url"/> starts with one of <paramref name="prefixes"/>; an empty
    /// list matches everything.
    /// </summary>
    public static bool MatchesAny(string url, IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0) return true;

        foreach (var prefix in prefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
