using System.Text.RegularExpressions;

namespace Rag.NET.DataProviders.Web;

/// <summary>Configuration for <see cref="SitemapDataProvider"/>.</summary>
/// <remarks>
/// <para>
/// Added for issue #252 (Milestone 6, Phase 6.2.2): a sitemap for a large site routinely lists
/// sections nobody wants ingested — a changelog, a tag index, a paginated archive — and before
/// this the only way to skip them was to filter after fetching every one.
/// </para>
/// <para>
/// <b>Two mechanisms rather than one predicate</b>, which was the open design question. A
/// <c>Func&lt;string, bool&gt;</c> would cover both and compose better in code, but this provider
/// is routinely constructed from configuration, and a delegate cannot come from an
/// <c>appsettings.json</c>. Prefixes and patterns can. They are also the two things the request
/// actually asked for, and a prefix is not a regex that happens to be anchored — it needs no
/// escaping, cannot be malformed, and cannot be slow.
/// </para>
/// </remarks>
public sealed class SitemapOptions
{
    /// <summary>
    /// URL prefixes to skip, compared with <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// Empty by default, so the provider skips nothing unless asked.
    /// </summary>
    /// <remarks>
    /// Matched against the full URL as the sitemap publishes it, not against a path fragment, so
    /// <c>https://example.com/blog/</c> excludes that section while <c>/blog/</c> alone excludes
    /// nothing. Compared verbatim: the sitemap protocol requires absolute URLs, and normalising
    /// them here would silently change what a prefix means.
    /// </remarks>
    public IReadOnlyList<string> ExcludedUrlPrefixes { get; init; } = [];

    /// <summary>
    /// Regular expressions to skip; a URL matching any of them is excluded.
    /// Empty by default.
    /// </summary>
    /// <remarks>
    /// <b>Each pattern is compiled with a one-second match timeout.</b> These patterns come from
    /// configuration, which means they come from a human, which means a catastrophically
    /// backtracking one is a question of when rather than whether — and a sitemap can carry tens of
    /// thousands of URLs to apply it to. A timed-out match throws
    /// <see cref="RegexMatchTimeoutException"/> rather than excluding or including the URL by
    /// default: guessing either way would be a silent, per-URL wrong answer.
    /// </remarks>
    public IReadOnlyList<string> ExcludedUrlPatterns { get; init; } = [];

    /// <summary>
    /// Whether the exclusions also prune nested <c>&lt;sitemapindex&gt;</c> links, not only page
    /// URLs. Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was the second open design question, and it is a real decision rather than an
    /// implementation detail: excluding an index link prunes <b>every page underneath it, sight
    /// unseen</b>, which is the difference between skipping a section and never fetching it.
    /// </para>
    /// <para>
    /// Default <see langword="true"/> because that is the cheaper and more usually intended
    /// behaviour — a site that partitions its sitemap index by section (<c>/sitemap-blog.xml</c>)
    /// lets one prefix skip the whole section without downloading it. Set to
    /// <see langword="false"/> when the index is partitioned by something unrelated to the URLs it
    /// contains — by date, or by shard — where an index link matching a prefix says nothing about
    /// the pages inside it.
    /// </para>
    /// </remarks>
    public bool ExcludeNestedSitemaps { get; init; } = true;

    /// <summary>Compiles <see cref="ExcludedUrlPatterns"/> once, with a match timeout.</summary>
    internal IReadOnlyList<Regex> CompilePatterns()
    {
        if (ExcludedUrlPatterns.Count == 0)
        {
            return [];
        }

        var compiled = new List<Regex>(ExcludedUrlPatterns.Count);
        foreach (var pattern in ExcludedUrlPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            compiled.Add(new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));
        }

        return compiled;
    }

    /// <summary>The prefixes, trimmed and with blanks dropped.</summary>
    /// <remarks>
    /// A blank prefix would match every URL and silently empty the whole sitemap — the one input
    /// here whose mistake is unrecoverable and invisible, so it is dropped rather than honoured.
    /// </remarks>
    internal IReadOnlyList<string> NormalisedPrefixes()
    {
        if (ExcludedUrlPrefixes.Count == 0)
        {
            return [];
        }

        var kept = new List<string>(ExcludedUrlPrefixes.Count);
        foreach (var prefix in ExcludedUrlPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                kept.Add(prefix.Trim());
            }
        }

        return kept;
    }
}
