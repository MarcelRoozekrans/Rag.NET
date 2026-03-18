using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Enumerates items from an RSS 2.0 or Atom feed.
/// ETag is the <c>&lt;pubDate&gt;</c> (RSS) or <c>&lt;updated&gt;</c> (Atom) value when present.
/// </summary>
public sealed class RssDataProvider : IFileContentProvider
{
    private static readonly XNamespace s_atomNs = "http://www.w3.org/2005/Atom";

    private readonly string _feedUrl;
    private readonly HttpClient _httpClient;

    public RssDataProvider(string feedUrl, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);
        _feedUrl = feedUrl;
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var xml = await _httpClient.GetStringAsync(_feedUrl, cancellationToken).ConfigureAwait(false);
        var root = XDocument.Parse(xml).Root!;

        if (string.Equals(root.Name.LocalName, "feed", StringComparison.Ordinal))
        {
            // Atom
            foreach (var entry in root.Elements(s_atomNs + "entry"))
            {
                var id = entry.Element(s_atomNs + "id")?.Value
                      ?? entry.Element(s_atomNs + "link")?.Attribute("href")?.Value;
                if (id is null) continue;

                var link = entry.Element(s_atomNs + "link")?.Attribute("href")?.Value ?? id;
                var updated = entry.Element(s_atomNs + "updated")?.Value;
                var capturedLink = link;

                yield return new FileEntry(
                    Id: id,
                    FileName: InferFileName(id),
                    OpenContentAsync: async ct =>
                    {
                        var response = await _httpClient.GetStreamAsync(capturedLink, ct).ConfigureAwait(false);
                        var buffer = new MemoryStream();
                        await response.CopyToAsync(buffer, ct).ConfigureAwait(false);
                        await response.DisposeAsync().ConfigureAwait(false);
                        buffer.Position = 0;
                        return (Stream)buffer;
                    },
                    ETag: updated);
            }
        }
        else
        {
            // RSS 2.0
            foreach (var item in root.Descendants("item"))
            {
                var guid = item.Element("guid")?.Value;
                var link = item.Element("link")?.Value;
                var id = guid ?? link;
                if (id is null) continue;

                var pubDate = item.Element("pubDate")?.Value;
                var capturedLink = link ?? id;

                yield return new FileEntry(
                    Id: id,
                    FileName: InferFileName(id),
                    OpenContentAsync: async ct =>
                    {
                        var response = await _httpClient.GetStreamAsync(capturedLink, ct).ConfigureAwait(false);
                        var buffer = new MemoryStream();
                        await response.CopyToAsync(buffer, ct).ConfigureAwait(false);
                        await response.DisposeAsync().ConfigureAwait(false);
                        buffer.Position = 0;
                        return (Stream)buffer;
                    },
                    ETag: pubDate);
            }
        }
    }

    private static string InferFileName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? "item";
            return string.IsNullOrEmpty(Path.GetExtension(segment)) ? segment + ".html" : segment;
        }
        catch (UriFormatException)
        {
            return "item.html";
        }
    }
}
