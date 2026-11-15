using System.Globalization;
using System.Net;
using Rag.NET.Models;

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
/// <para>
/// How the fetch went decides what applies (RFC 9309 section 2.3, #998). A file that downloads
/// is parsed, up to its first 500 KiB. A 4xx other than 429, or a 3xx left after
/// <see cref="HttpClient"/> stops following redirects, means the file is unavailable, and
/// everything is allowed. A 5xx, a 429 or
/// no response at all means it is unreachable: every URL on the host is disallowed, and
/// <see cref="Unreachable"/> carries the error the provider reports for that host.
/// </para>
/// </remarks>
internal sealed class RobotsTxt
{
    /// <summary>RFC 9309 2.2 ends a line at CR, LF or CRLF; the empty piece a CRLF leaves is skipped.</summary>
    private static readonly char[] s_lineEnds = ['\r', '\n'];
    private static readonly char[] s_recordWhitespace = [' ', '\t'];

    private static readonly RobotsTxt s_allowAll = new([], crawlDelay: null);

    /// <summary>
    /// How many bytes of a robots.txt are parsed. RFC 9309 section 2.5 asks for a limit of at
    /// least 500 KiB; this is exactly that.
    /// </summary>
    internal const int ParseLimitBytes = 500 * 1024;

    private readonly IReadOnlyList<Rule> _rules;

    private RobotsTxt(IReadOnlyList<Rule> rules, TimeSpan? crawlDelay, RagError? unreachable = null)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
        Unreachable = unreachable;
    }

    /// <summary>
    /// Why the robots.txt could not be reached, or <see langword="null"/> when it could. When set,
    /// <see cref="IsDisallowed"/> disallows every URL (RFC 9309 section 2.3.1.4), and the provider
    /// reports this error once for the host so the run shows what it skipped.
    /// </summary>
    internal RagError? Unreachable { get; }

    /// <summary>
    /// The largest valid <c>crawl-delay</c> in the applicable groups, or <see langword="null"/>.
    /// Not part of RFC 9309, but sites use it; the providers raise their request interval to it
    /// and never lower it.
    /// </summary>
    internal TimeSpan? CrawlDelay { get; }

    /// <summary>
    /// The longest delay <see cref="Task.Delay(TimeSpan)"/> accepts. A larger <c>crawl-delay</c>
    /// is held to it rather than thrown on, which still stalls the crawl as the site asked.
    /// </summary>
    internal static TimeSpan LongestCrawlDelay { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The product token <paramref name="client"/> identifies as: the name of the first product
    /// in its default <c>User-Agent</c> header, when that name is a valid RFC 9309 token
    /// (<c>a-z A-Z _ -</c>). <see langword="null"/> otherwise, which leaves only <c>*</c> groups.
    /// </summary>
    internal static string? ProductTokenOf(HttpClient client)
    {
        var name = client.DefaultRequestHeaders.UserAgent.FirstOrDefault(p => p.Product is not null)?.Product!.Name;
        return !string.IsNullOrEmpty(name) && name.All(IsTokenChar) ? name : null;
    }

    /// <summary>
    /// Fetches <c>/robots.txt</c> for <paramref name="origin"/>'s scheme, host and port. A 3xx, or
    /// a 4xx other than 429, allows everything. A 5xx, a 429, or a request that fails, breaks off or times out
    /// disallows everything and sets <see cref="Unreachable"/>. The caller's own cancellation
    /// propagates.
    /// </summary>
    public static async Task<RobotsTxt> LoadAsync(HttpClient httpClient, Uri origin, CancellationToken ct)
    {
        var robotsUrl = new Uri(origin, "/robots.txt").ToString();
        var host = origin.GetLeftPart(UriPartial.Authority);
        try
        {
            var (content, truncated) = await httpClient
                .GetTextPrefixWithCharsetFallbackAsync(robotsUrl, ParseLimitBytes, ct)
                .ConfigureAwait(false);
            if (truncated)
            {
                // The last line was cut at the limit; applied shortened, it would be a different rule.
                var end = content.LastIndexOfAny(s_lineEnds);
                content = end < 0 ? string.Empty : content[..(end + 1)];
            }

            return Parse(content, ProductTokenOf(httpClient));
        }
        catch (Exception ex) when (FetchFailure.IsAbsent(ex))
        {
            return s_allowAll;
        }
        catch (Exception ex) when (FetchFailure.IsSkippable(ex, ct))
        {
            return new RobotsTxt([], crawlDelay: null, FetchFailure.ToError(ex, $"robots.txt at {robotsUrl}",
                $"RFC 9309 section 2.3.1.4 reads that as disallow-all for {host}, so its pages were skipped."));
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

        foreach (var rawLine in content.Split(s_lineEnds))
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

            if (key.Equals("crawl-delay", StringComparison.OrdinalIgnoreCase))
            {
                // Not an RFC 9309 record, so it must not split a run of user-agent lines (2.2.4).
                if (current is not null
                    && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && !double.IsNaN(seconds) && seconds >= 0)
                {
                    current.CrawlDelays.Add(seconds >= LongestCrawlDelay.TotalSeconds
                        ? LongestCrawlDelay
                        : TimeSpan.FromSeconds(seconds));
                }

                continue;
            }

            // Any other record (Sitemap, Host, unknown) neither ends nor breaks a group.
        }

        var applicable = Select(groups, productToken);
        var delays = applicable.SelectMany(g => g.CrawlDelays).ToList();
        return new RobotsTxt(
            applicable.SelectMany(g => g.Rules).ToList(),
            delays.Count > 0 ? delays.Max() : null);
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
        // Only spaces and tabs are record whitespace (RFC 9309 section 2.2). String.Trim() would
        // also strip U+00A0 and other Unicode spaces, which are valid path-pattern characters.
        value = text[(colon + 1)..].Trim(s_recordWhitespace);
        return key.Length > 0;
    }

    /// <summary>
    /// The product token a <c>user-agent</c> value names: cut at the first character outside
    /// <c>a-z A-Z _ -</c>, so <c>MyBot/2.0</c> names <c>MyBot</c>. <c>*</c> stays <c>*</c>.
    /// </summary>
    private static string AgentName(string value)
    {
        if (value.StartsWith('*'))
        {
            // Only a bare * is the wildcard agent; "*bot" names no agent this file can mean.
            return value.Length == 1 || !IsTokenChar(value[1]) ? "*" : string.Empty;
        }

        var end = 0;
        while (end < value.Length && IsTokenChar(value[end])) end++;
        return value[..end];
    }

    internal static bool IsTokenChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or '-';

    /// <summary>Whether the applicable rules disallow <paramref name="url"/>.</summary>
    public bool IsDisallowed(string url)
    {
        if (Unreachable is not null) return true;
        if (_rules.Count == 0) return false;

        // Never empty: an absolute URI with no path reports "/".
        var pathAndQuery = new Uri(url).PathAndQuery;
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
        /// <summary>The pattern cut at every <c>*</c>; one piece when it has none.</summary>
        private readonly string[] _segments = Pattern.Split('*');

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
        /// when anchored, else as a prefix.
        /// </summary>
        /// <remarks>
        /// The first piece must start the target. Each middle piece is found at its leftmost
        /// place after the one before: for <c>*</c> alone that is never wrong, because the
        /// leftmost place leaves the most room for the rest. The last piece then ends the target
        /// when anchored, or occurs anywhere after when not. Each piece is one ordinal search,
        /// so 1,000 hostile rules no longer cost seconds per URL as the backtracking walk this
        /// replaced did (#998).
        /// </remarks>
        public bool Matches(string target)
        {
            var first = _segments[0];
            if (!target.StartsWith(first, StringComparison.Ordinal)) return false;
            if (_segments.Length == 1) return !Anchored || target.Length == first.Length;

            var position = first.Length;
            for (var i = 1; i < _segments.Length - 1; i++)
            {
                var found = target.IndexOf(_segments[i], position, StringComparison.Ordinal);
                if (found < 0) return false;
                position = found + _segments[i].Length;
            }

            var last = _segments[^1];
            return Anchored
                ? target.Length - last.Length >= position && target.EndsWith(last, StringComparison.Ordinal)
                : target.IndexOf(last, position, StringComparison.Ordinal) >= 0;
        }
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<Rule> Rules { get; } = [];

        public List<TimeSpan> CrawlDelays { get; } = [];
    }
}
