using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Html.Parser;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Crawls a website via BFS link-following from a seed URL, yielding each discovered page.
/// Content is captured at crawl time; <see cref="FileEntry.OpenContentAsync"/> returns the already-fetched HTML.
/// No ETag — no cheap pre-check is available for web pages without server cooperation.
/// </summary>
public sealed class WebCrawlerDataProvider : IFileContentProvider
{
    private readonly string _seedUrl;
    private readonly HttpClient _httpClient;
    private readonly WebCrawlerOptions _options;

    public WebCrawlerDataProvider(string seedUrl, HttpClient httpClient, WebCrawlerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedUrl);
        _seedUrl = seedUrl;
        _httpClient = httpClient;
        _options = options ?? new WebCrawlerOptions();
    }

    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seedUri = new Uri(_seedUrl);
        var disallowed = _options.RespectRobotsTxt
            ? await LoadRobotsAsync(seedUri, cancellationToken).ConfigureAwait(false)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string url, int depth)>();

        // Through the same rule as every discovered link (#288). Enqueued verbatim, a seed written
        // the way a person types it — with the trailing slash — never matched the normalised form a
        // back-link produces, so the seed page was crawled and yielded twice.
        queue.Enqueue((Normalise(seedUri), 0));
        var pageCount = 0;

        while (queue.Count > 0 && pageCount < _options.MaxPages)
        {
            var (url, depth) = queue.Dequeue();
            if (!visited.Add(url)) continue;
            if (IsDisallowed(url, disallowed)) continue;
            if (_options.SameDomain && !string.Equals(new Uri(url).Host, seedUri.Host, StringComparison.OrdinalIgnoreCase)) continue;

            string html;
            try
            {
                html = await _httpClient.GetStringWithCharsetFallbackAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            pageCount++;
            var capturedHtml = html;

            yield return Result<FileEntry, RagError>.Success(new FileEntry(
                Id: new EntryId(url),
                FileName: InferFileName(url),
                OpenContentAsync: _ =>
                {
                    var bytes = Encoding.UTF8.GetBytes(capturedHtml);
                    return Task.FromResult<Stream>(new MemoryStream(bytes));
                },
                Metadata: BuildMetadata(url, depth)));

            if (depth < _options.MaxDepth)
            {
                foreach (var link in ExtractLinks(html, url))
                {
                    if (!visited.Contains(link))
                        queue.Enqueue((link, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// Tags for a crawled page. <c>depth</c> is the BFS distance from the seed (the seed itself
    /// is <c>"0"</c>), which lets a caller keep only shallow pages at query time. Built
    /// synchronously — assembling the dictionary inside the async iterator would trip HLQ012.
    /// </summary>
    private static Dictionary<string, MetadataValue> BuildMetadata(string url, int depth)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["url"]   = url,
            ["depth"] = depth.ToString(CultureInfo.InvariantCulture),
        };

        var host = new Uri(url).Host;
        if (!string.IsNullOrEmpty(host)) metadata["host"] = host;
        return metadata;
    }

    private async Task<HashSet<string>> LoadRobotsAsync(Uri seedUri, CancellationToken ct)
    {
        try
        {
            var robotsUrl = new Uri(seedUri, "/robots.txt").ToString();
            var content = await _httpClient.GetStringWithCharsetFallbackAsync(robotsUrl, ct).ConfigureAwait(false);
            return ParseRobotsDisallowed(content);
        }
        catch (HttpRequestException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static HashSet<string> ParseRobotsDisallowed(string content)
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

        return disallowed;
    }

    private static bool IsDisallowed(string url, HashSet<string> disallowed)
    {
        var path = new Uri(url).AbsolutePath;
        foreach (var rule in disallowed)
        {
            if (path.StartsWith(rule, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractLinks(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var baseUri = new Uri(baseUrl);

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            Uri uri;
            try
            {
                uri = new Uri(baseUri, href);
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (!string.Equals(uri.Scheme, "http", StringComparison.Ordinal) &&
                !string.Equals(uri.Scheme, "https", StringComparison.Ordinal)) continue;

            yield return Normalise(uri);
        }
    }

    /// <summary>
    /// Reduces a URL to the single form the visited set compares: no fragment, no trailing slash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted so there is <b>one</b> rule rather than two. Discovered links were normalised here
    /// and the seed was enqueued verbatim, so a seed of <c>http://host/</c> and the same page reached
    /// through a back-link as <c>http://host</c> were two entries in an ordinal
    /// <see cref="HashSet{T}"/> — the root was fetched and yielded twice under two
    /// <see cref="EntryId"/>s (#288). Downstream that is one page indexed twice, embedded twice, and
    /// returnable twice by a retrieval that assumes one id per page.
    /// </para>
    /// <para>
    /// A named method rather than a second inline copy: the defect was not the rule, it was the rule
    /// existing in one of the two places that needed it, and a copy would leave that possible again.
    /// </para>
    /// </remarks>
    /// <param name="uri">The absolute URL to reduce.</param>
    /// <returns>The normalised URL.</returns>
    private static string Normalise(Uri uri) =>
        new UriBuilder(uri) { Fragment = string.Empty }.Uri
            .ToString().TrimEnd('/');

    private static string InferFileName(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "index";
        return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
    }
}
