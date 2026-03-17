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
    public bool RespectRobotsTxt { get; init; } = true;
}
