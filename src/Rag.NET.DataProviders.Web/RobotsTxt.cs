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
                current.Rules.Add(Rule.Create(
                    allow: key.Equals("allow", StringComparison.OrdinalIgnoreCase),
                    value: value));
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

        var pathAndQuery = new Uri(url).PathAndQuery;
        if (pathAndQuery.Length == 0) pathAndQuery = "/";
        if (string.Equals(pathAndQuery, "/robots.txt", StringComparison.Ordinal)) return false;

        var target = Normalise(pathAndQuery, isPattern: false);
        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(target)) continue;
            if (best is null
                || rule.Length > best.Length
                || (rule.Length == best.Length && rule.Allow))
            {
                best = rule;
            }
        }

        return best is { Allow: false };
    }

    /// <summary>
    /// Brings a URL's path and query, or a rule's value, to the one form RFC 9309 2.2.2 compares:
    /// non-ASCII as UTF-8 <c>%XX</c>, <c>%XX</c> of an unreserved character decoded, every other
    /// <c>%XX</c> with upper-case hex. In a URL a literal <c>*</c> or <c>$</c> is encoded, so only
    /// a rule's own <c>*</c> is a wildcard (2.2.3); in a rule a <c>$</c> is literal unless it ends
    /// the rule, which <see cref="Rule.Create"/> strips first.
    /// </summary>
    internal static string Normalise(string text, bool isPattern)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '%' && i + 2 < text.Length
                && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                var octet = (char)Convert.ToByte(text.Substring(i + 1, 2), 16);
                if (IsUnreserved(octet)) builder.Append(octet);
                else AppendEscaped(builder, (byte)octet);
                i += 2;
            }
            else if (c > 0x7F)
            {
                var length = char.IsHighSurrogate(c) && i + 1 < text.Length ? 2 : 1;
                var written = System.Text.Encoding.UTF8.GetBytes(text.AsSpan(i, length), utf8);
                foreach (var b in utf8[..written]) AppendEscaped(builder, b);
                i += length - 1;
            }
            else if (c == '$' || (c == '*' && !isPattern))
            {
                AppendEscaped(builder, (byte)c);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~';

    private static void AppendEscaped(System.Text.StringBuilder builder, byte octet) =>
        builder.Append('%').Append(octet.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>One <c>allow</c> or <c>disallow</c> line, its value normalised.</summary>
    /// <param name="Allow">Whether the line is <c>allow</c>.</param>
    /// <param name="Pattern">The normalised value, without an end anchor; <c>*</c> is a wildcard.</param>
    /// <param name="Anchored">Whether the value ended in <c>$</c>.</param>
    private sealed record Rule(bool Allow, string Pattern, bool Anchored)
    {
        /// <summary>The octets the rule has, for "the most specific match" (2.2.2).</summary>
        public int Length => Pattern.Length + (Anchored ? 1 : 0);

        public static Rule Create(bool allow, string value)
        {
            var anchored = value.EndsWith('$');
            var body = anchored ? value[..^1] : value;
            return new Rule(allow, Normalise(body, isPattern: true), anchored);
        }

        /// <summary>
        /// Whether the rule matches from the first octet of <paramref name="target"/>: to its end
        /// when anchored, else as a prefix. An iterative wildcard walk that retries from the last
        /// <c>*</c> on a mismatch: no recursion, at most target-times-pattern steps.
        /// </summary>
        public bool Matches(string target)
        {
            int t = 0, p = 0, star = -1, resume = 0;
            while (t < target.Length)
            {
                if (p < Pattern.Length && Pattern[p] == '*')
                {
                    star = p++;
                    resume = t;
                }
                else if (p < Pattern.Length && Pattern[p] == target[t])
                {
                    p++;
                    t++;
                }
                else if (!Anchored && p == Pattern.Length)
                {
                    return true;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++resume;
                }
                else
                {
                    return false;
                }
            }

            while (p < Pattern.Length && Pattern[p] == '*') p++;
            return p == Pattern.Length;
        }
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<Rule> Rules { get; } = [];
    }
}
