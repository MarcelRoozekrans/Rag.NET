using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Enumerates URLs from a <c>sitemap.xml</c> or sitemap index file.
/// Follows <c>&lt;sitemapindex&gt;</c> links recursively.
/// ETag is set from the <c>&lt;lastmod&gt;</c> element when present.
/// </summary>
public sealed class SitemapDataProvider : IFileContentProvider
{
    private static readonly XNamespace s_ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly string _sitemapUrl;
    private readonly HttpClient _httpClient;

    public SitemapDataProvider(string sitemapUrl, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sitemapUrl);
        _sitemapUrl = sitemapUrl;
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in LoadSitemapAsync(_sitemapUrl, cancellationToken).ConfigureAwait(false))
            yield return entry;
    }

    private async IAsyncEnumerable<FileEntry> LoadSitemapAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var xml = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var root = XDocument.Parse(xml).Root!;

        if (string.Equals(root.Name.LocalName, "sitemapindex", StringComparison.Ordinal))
        {
            foreach (var sitemap in root.Elements(s_ns + "sitemap"))
            {
                var loc = sitemap.Element(s_ns + "loc")?.Value;
                if (loc is null) continue;
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
                var lastMod = urlEl.Element(s_ns + "lastmod")?.Value;
                var capturedLoc = loc;

                yield return new FileEntry(
                    Id: loc,
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
                    ETag: lastMod);
            }
        }
    }

    private static string InferFileName(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "index";
        return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
    }
}
