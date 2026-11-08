namespace Rag.NET.DataProviders.Web;

/// <summary>
/// The <c>Disallow</c> rules a site's <c>/robots.txt</c> sets for <c>User-agent: *</c>.
/// </summary>
/// <remarks>
/// Extracted from <see cref="WebCrawlerDataProvider"/> when <see cref="SitemapDataProvider"/>
/// gained <see cref="SitemapOptions.RespectRobotsTxt"/>, so both providers read robots.txt by
/// one rule rather than two copies that can drift. The rule itself is unchanged: prefix
/// <c>Disallow</c> paths for the wildcard agent, matched case-insensitively; no <c>Allow</c>,
/// no <c>*</c>/<c>$</c> patterns and no <c>Crawl-delay</c>.
/// </remarks>
internal sealed class RobotsTxt
{
    private static readonly RobotsTxt s_allowAll = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private readonly HashSet<string> _disallowed;

    private RobotsTxt(HashSet<string> disallowed) => _disallowed = disallowed;

    /// <summary>
    /// Fetches <c>/robots.txt</c> for <paramref name="origin"/>'s scheme, host and port.
    /// A missing or unreachable file allows everything.
    /// </summary>
    public static async Task<RobotsTxt> LoadAsync(HttpClient httpClient, Uri origin, CancellationToken ct)
    {
        try
        {
            var robotsUrl = new Uri(origin, "/robots.txt").ToString();
            var content = await httpClient.GetStringWithCharsetFallbackAsync(robotsUrl, ct).ConfigureAwait(false);
            return Parse(content);
        }
        catch (HttpRequestException)
        {
            return s_allowAll;
        }
    }

    internal static RobotsTxt Parse(string content)
    {
        var disallowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applyToUs = false;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
            {
                var agent = trimmed["User-agent:".Length..].Trim();
                applyToUs = string.Equals(agent, "*", StringComparison.Ordinal);
            }
            else if (applyToUs && trimmed.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
            {
                var path = trimmed["Disallow:".Length..].Trim();
                if (!string.IsNullOrEmpty(path))
                    disallowed.Add(path);
            }
        }

        return new RobotsTxt(disallowed);
    }

    /// <summary>Whether <paramref name="url"/>'s path falls under a <c>Disallow</c> rule.</summary>
    public bool IsDisallowed(string url)
    {
        if (_disallowed.Count == 0) return false;

        var path = new Uri(url).AbsolutePath;
        foreach (var rule in _disallowed)
        {
            if (path.StartsWith(rule, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
