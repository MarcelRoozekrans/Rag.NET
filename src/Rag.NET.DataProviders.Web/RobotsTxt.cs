namespace Rag.NET.DataProviders.Web;

/// <summary>
/// The rules a site's <c>/robots.txt</c> sets for this crawler, read per RFC 9309 section 2.2.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="WebCrawlerDataProvider"/> and <see cref="SitemapDataProvider"/>, so both
/// providers read robots.txt by one rule (#925). Until #927 it read only prefix <c>Disallow</c>
/// lines under <c>User-agent: *</c>, matched case-insensitively.
/// </para>
/// <para>
/// Consecutive <c>user-agent</c> lines open one group. The groups naming the crawler's product
/// token apply, merged; when none does, the <c>*</c> groups apply, merged; otherwise nothing is
/// disallowed. Rules before the first <c>user-agent</c> line are ignored, and other records such
/// as <c>Sitemap</c> neither end nor break a group. Every <c>allow</c> and <c>disallow</c> rule is
/// matched case-sensitively against the URL's path and query; the longest matching rule wins,
/// and <c>allow</c> wins a tie. <c>/robots.txt</c> itself is always allowed.
/// </para>
/// </remarks>
internal sealed class RobotsTxt
{
    private static readonly RobotsTxt s_allowAll = new([]);

    private readonly IReadOnlyList<Rule> _rules;

    private RobotsTxt(IReadOnlyList<Rule> rules) => _rules = rules;

    /// <summary>
    /// Fetches <c>/robots.txt</c> for <paramref name="origin"/>'s scheme, host and port.
    /// A missing, unreachable, broken-off or timed-out file allows everything.
    /// </summary>
    public static async Task<RobotsTxt> LoadAsync(HttpClient httpClient, Uri origin, CancellationToken ct)
    {
        try
        {
            var robotsUrl = new Uri(origin, "/robots.txt").ToString();
            var content = await httpClient.GetStringWithCharsetFallbackAsync(robotsUrl, ct).ConfigureAwait(false);
            return Parse(content, productToken: null);
        }
        catch (Exception ex) when (FetchFailure.IsSkippable(ex, ct))
        {
            return s_allowAll;
        }
    }

    /// <summary>
    /// Parses <paramref name="content"/> and keeps the rules of the groups that apply to
    /// <paramref name="productToken"/>, or of the <c>*</c> groups when none names it.
    /// </summary>
    internal static RobotsTxt Parse(string content, string? productToken)
    {
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            if (!TryReadRecord(rawLine, out var key, out var value)) continue;

            if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (current is null || !lastWasAgent)
                {
                    current = new Group();
                    groups.Add(current);
                }

                current.Agents.Add(AgentName(value));
                lastWasAgent = true;
                continue;
            }

            if (key.Equals("allow", StringComparison.OrdinalIgnoreCase)
                || key.Equals("disallow", StringComparison.OrdinalIgnoreCase))
            {
                lastWasAgent = false;
                if (current is null || value.Length == 0) continue;
                current.Rules.Add(new Rule(
                    Allow: key.Equals("allow", StringComparison.OrdinalIgnoreCase),
                    Pattern: value));
            }

            // Any other record (Sitemap, Host, unknown) neither ends nor breaks a group.
        }

        var applicable = Select(groups, productToken);
        return new RobotsTxt(applicable.SelectMany(g => g.Rules).ToList());
    }

    private static List<Group> Select(List<Group> groups, string? productToken)
    {
        if (!string.IsNullOrEmpty(productToken))
        {
            var named = groups
                .Where(g => g.Agents.Contains(productToken, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (named.Count > 0) return named;
        }

        return groups.Where(g => g.Agents.Contains("*", StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// Splits one line into its record name and value, with comments, whitespace and a byte-order
    /// mark removed. Lines without a colon are not records.
    /// </summary>
    private static bool TryReadRecord(string line, out string key, out string value)
    {
        var text = line.TrimStart('﻿');
        var hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0) text = text[..hash];

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            key = value = string.Empty;
            return false;
        }

        key = text[..colon].Trim();
        value = text[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>
    /// The product token a <c>user-agent</c> value names: cut at the first character outside
    /// <c>a-z A-Z _ -</c>, so <c>MyBot/2.0</c> names <c>MyBot</c>. <c>*</c> stays <c>*</c>.
    /// </summary>
    private static string AgentName(string value)
    {
        if (value.StartsWith('*')) return "*";

        var end = 0;
        while (end < value.Length && IsTokenChar(value[end])) end++;
        return value[..end];
    }

    internal static bool IsTokenChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or '-';

    /// <summary>Whether the applicable rules disallow <paramref name="url"/>.</summary>
    public bool IsDisallowed(string url)
    {
        if (_rules.Count == 0) return false;

        var target = new Uri(url).PathAndQuery;
        if (target.Length == 0) target = "/";
        if (string.Equals(target, "/robots.txt", StringComparison.Ordinal)) return false;

        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!Matches(rule.Pattern, target)) continue;
            if (best is null
                || rule.Pattern.Length > best.Pattern.Length
                || (rule.Pattern.Length == best.Pattern.Length && rule.Allow))
            {
                best = rule;
            }
        }

        return best is { Allow: false };
    }

    private static bool Matches(string pattern, string target) =>
        target.StartsWith(pattern, StringComparison.Ordinal);

    private sealed record Rule(bool Allow, string Pattern);

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<Rule> Rules { get; } = [];
    }
}
