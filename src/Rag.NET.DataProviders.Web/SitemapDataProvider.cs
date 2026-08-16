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
    private readonly IReadOnlyList<Regex> _excludedPatterns;

    public SitemapDataProvider(string sitemapUrl, HttpClient httpClient, SitemapOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sitemapUrl);
        _sitemapUrl = sitemapUrl;
        _httpClient = httpClient;
        _options = options ?? new SitemapOptions();
        // Compiled once here, not per URL: a sitemap index can carry tens of thousands of entries.
        _excludedPrefixes = _options.NormalisedPrefixes();
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

    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in LoadSitemapAsync(_sitemapUrl, cancellationToken).ConfigureAwait(false))
            yield return entry;
    }

    private async IAsyncEnumerable<Result<FileEntry, RagError>> LoadSitemapAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
                await foreach (var entry in LoadSitemapAsync(loc, cancellationToken).ConfigureAwait(false))
                    yield return entry;
            }
        }
        else
        {
            foreach (var urlEl in root.Elements(s_ns + "url"))
            {
                var loc = urlEl.Element(s_ns + "loc")?.Value;
                if (loc is null) continue;
                if (IsExcluded(loc)) continue;
                var lastMod = urlEl.Element(s_ns + "lastmod")?.Value;
                var capturedLoc = loc;

                yield return Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId(loc),
                    FileName: InferFileName(loc),
                    OpenContentAsync: async ct =>
                    {
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
