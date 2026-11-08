using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Enumerates URLs from a <c>sitemap.xml</c> or sitemap index file.
/// Follows <c>&lt;sitemapindex&gt;</c> links recursively.
/// ETag is set from the <c>&lt;lastmod&gt;</c> element when present.
/// </summary>
/// <remarks>
/// Phase 4.10 Task 5: <c>&lt;lastmod&gt;</c> also becomes the typed
/// <see cref="FileEntry.UpdatedAt"/>, parsed via <see cref="ConnectorTimestampParser"/>. The
/// existing <c>lastmod</c> metadata tag (see <see cref="BuildMetadata"/>) is kept exactly as-is —
/// it stays unreserved and continues to pass the raw string through verbatim, precision and all;
/// the typed field is an addition, not a replacement.
/// </remarks>
public sealed class SitemapDataProvider : IFileContentProvider
{
    private static readonly XNamespace s_ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly string _sitemapUrl;
    private readonly HttpClient _httpClient;
    private readonly SitemapOptions _options;
    private readonly IReadOnlyList<string> _excludedPrefixes;
    private readonly IReadOnlyList<string> _includedPrefixes;
    private readonly IReadOnlyList<Regex> _excludedPatterns;
    private readonly RequestThrottle _throttle;

    public SitemapDataProvider(string sitemapUrl, HttpClient httpClient, SitemapOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sitemapUrl);
        _sitemapUrl = sitemapUrl;
        _httpClient = httpClient;
        _options = options ?? new SitemapOptions();
        if (_options.MinimumRequestInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.MinimumRequestInterval,
                $"{nameof(SitemapOptions.MinimumRequestInterval)} must not be negative.");
        }

        _throttle = new RequestThrottle(_options.MinimumRequestInterval);
        // Compiled once here, not per URL: a sitemap index can carry tens of thousands of entries.
        _excludedPrefixes = _options.NormalisedPrefixes();
        _includedPrefixes = _options.NormalisedIncludedPrefixes();
        _excludedPatterns = _options.CompilePatterns();
    }

    /// <summary>
    /// Whether <paramref name="url"/> is excluded by <see cref="SitemapOptions"/>.
    /// </summary>
    /// <remarks>
    /// Prefixes are checked before patterns because a prefix cannot be slow and a pattern can:
    /// on a large sitemap the cheap test should be the one that runs on every URL.
    /// </remarks>
    private bool IsExcluded(string url)
    {
        foreach (var prefix in _excludedPrefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var pattern in _excludedPatterns)
        {
            if (pattern.IsMatch(url))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a page URL passes <see cref="SitemapOptions.IncludedUrlPrefixes"/>. Not applied to
    /// index links: a sharded index's links match no content prefix.
    /// </summary>
    private bool IsIncluded(string url)
    {
        if (_includedPrefixes.Count == 0) return true;

        foreach (var prefix in _includedPrefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Per walk, not per provider: a second walk should see a robots.txt the site has changed.
        var robotsByHost = new Dictionary<string, RobotsTxt>(StringComparer.OrdinalIgnoreCase);
        await foreach (var entry in LoadSitemapAsync(_sitemapUrl, robotsByHost, cancellationToken).ConfigureAwait(false))
            yield return entry;
    }

    /// <summary>
    /// Whether robots.txt for <paramref name="url"/>'s host disallows it, fetching that file the
    /// first time the host is seen.
    /// </summary>
    private async Task<bool> IsDisallowedByRobotsAsync(
        string url, Dictionary<string, RobotsTxt> robotsByHost, CancellationToken ct)
    {
        if (!_options.RespectRobotsTxt) return false;

        var uri = new Uri(url);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!robotsByHost.TryGetValue(origin, out var robots))
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            robots = await RobotsTxt.LoadAsync(_httpClient, uri, ct).ConfigureAwait(false);
            robotsByHost[origin] = robots;
        }

        return robots.IsDisallowed(url);
    }

    private async IAsyncEnumerable<Result<FileEntry, RagError>> LoadSitemapAsync(
        string url,
        Dictionary<string, RobotsTxt> robotsByHost,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var xml = await _httpClient.GetStringWithCharsetFallbackAsync(url, cancellationToken).ConfigureAwait(false);
        var root = XDocument.Parse(xml).Root!;

        if (string.Equals(root.Name.LocalName, "sitemapindex", StringComparison.Ordinal))
        {
            foreach (var sitemap in root.Elements(s_ns + "sitemap"))
            {
                var loc = sitemap.Element(s_ns + "loc")?.Value;
                if (loc is null) continue;
                // Pruning here skips every page under this index without fetching it, which is why
                // it is opt-out (SitemapOptions.ExcludeNestedSitemaps) rather than implied.
                if (_options.ExcludeNestedSitemaps && IsExcluded(loc)) continue;
                await foreach (var entry in LoadSitemapAsync(loc, robotsByHost, cancellationToken).ConfigureAwait(false))
                    yield return entry;
            }
        }
        else
        {
            foreach (var urlEl in root.Elements(s_ns + "url"))
            {
                var loc = urlEl.Element(s_ns + "loc")?.Value;
                if (loc is null) continue;
                if (!IsIncluded(loc) || IsExcluded(loc)) continue;
                if (await IsDisallowedByRobotsAsync(loc, robotsByHost, cancellationToken).ConfigureAwait(false)) continue;
                var lastMod = urlEl.Element(s_ns + "lastmod")?.Value;
                var capturedLoc = loc;

                yield return Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId(loc),
                    FileName: InferFileName(loc),
                    OpenContentAsync: async ct =>
                    {
                        await _throttle.WaitAsync(ct).ConfigureAwait(false);
                        var response = await _httpClient.GetStreamAsync(capturedLoc, ct).ConfigureAwait(false);
                        var buffer = new MemoryStream();
                        await response.CopyToAsync(buffer, ct).ConfigureAwait(false);
                        await response.DisposeAsync().ConfigureAwait(false);
                        buffer.Position = 0;
                        return (Stream)buffer;
                    },
                    ETag: lastMod,
                    Metadata: BuildMetadata(loc, lastMod),
                    UpdatedAt: ConnectorTimestampParser.Parse(lastMod)));
            }
        }
    }

    /// <summary>
    /// Tags for a sitemap URL. <c>lastmod</c> is passed through verbatim — the sitemap protocol
    /// permits both a full W3C datetime and a bare date, and normalising would discard which
    /// precision the site actually published.
    /// </summary>
    private static Dictionary<string, MetadataValue>? BuildMetadata(string url, string? lastMod)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(url))     metadata["url"]     = url;
        if (!string.IsNullOrEmpty(lastMod)) metadata["lastmod"] = lastMod;
        return metadata.Count == 0 ? null : metadata;
    }

    private static string InferFileName(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "index";
        return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
    }
}
