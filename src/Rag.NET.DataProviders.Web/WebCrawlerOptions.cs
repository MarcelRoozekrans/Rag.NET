namespace Rag.NET.DataProviders.Web;

/// <summary>Configuration for <see cref="WebCrawlerDataProvider"/>.</summary>
public sealed class WebCrawlerOptions
{
    /// <summary>Maximum link-following depth from the seed URL. Default: 3.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Maximum number of pages to crawl. Default: 200.</summary>
    public int MaxPages { get; init; } = 200;

    /// <summary>
    /// Only follow links whose host matches the seed URL's host. Default: <see langword="true"/>.
    /// </summary>
    public bool SameDomain { get; init; } = true;

    /// <summary>
    /// Fetch <c>/robots.txt</c> and skip disallowed paths. Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Read per RFC 9309: the group naming the product token of the <see cref="HttpClient"/>'s
    /// default <c>User-Agent</c> applies, else the <c>User-agent: *</c> group. <c>Allow</c> and
    /// <c>Disallow</c> rules may use <c>*</c> and <c>$</c>; each is matched case-sensitively against
    /// the path and query, and the longest match wins. A <c>Crawl-delay</c> raises
    /// <see cref="MinimumRequestInterval"/> and never lowers it.
    /// </remarks>
    public bool RespectRobotsTxt { get; init; } = true;

    /// <summary>
    /// URL prefixes the crawl stays beneath, compared with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. When non-empty, a URL is fetched only if
    /// it starts with one of them. Empty by default: <see cref="SameDomain"/> is the only bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to every URL, the seed included, so a seed outside every prefix yields nothing.
    /// Links outside the prefixes are dropped before they are fetched, which is what makes this
    /// a bound on a site you do not run rather than a filter applied afterwards.
    /// </para>
    /// <para>
    /// Matched against the normalised URL the crawler uses as the page id — no fragment, no
    /// trailing slash — so write prefixes without a trailing slash when the seed itself should
    /// match. Blank entries are dropped, the same rule as <see cref="SitemapOptions.IncludedUrlPrefixes"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> IncludedUrlPrefixes { get; init; } = [];

    /// <summary>
    /// The minimum time between the start of two requests this crawler makes, robots.txt
    /// included. Default <see cref="TimeSpan.Zero"/>: no spacing. Negative values are rejected at
    /// construction.
    /// </summary>
    /// <remarks>
    /// With <see cref="RespectRobotsTxt"/> on, a longer <c>Crawl-delay</c> in the site's robots.txt
    /// takes over from this value; a shorter one is ignored.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.Zero;
}
