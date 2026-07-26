using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

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

    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
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

                yield return Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId(id),
                    FileName: InferFileName(id),
                    OpenContentAsync: Fetch(link),
                    ETag: updated,
                    Metadata: BuildAtomMetadata(entry, link, updated)));
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

                yield return Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId(id),
                    FileName: InferFileName(id),
                    OpenContentAsync: Fetch(link ?? id),
                    ETag: pubDate,
                    Metadata: BuildRssMetadata(item, link ?? id, pubDate)));
            }
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into a buffered stream when the entry is actually
    /// ingested. Extracted so both feed shapes share one body and each loop stays short.
    /// </summary>
    private Func<CancellationToken, Task<Stream>> Fetch(string url)
        => async ct =>
        {
            var response = await _httpClient.GetStreamAsync(url, ct).ConfigureAwait(false);
            var buffer = new MemoryStream();
            await response.CopyToAsync(buffer, ct).ConfigureAwait(false);
            await response.DisposeAsync().ConfigureAwait(false);
            buffer.Position = 0;
            return (Stream)buffer;
        };

    /// <summary>
    /// Tags for an Atom entry. <c>published_at</c> prefers <c>&lt;published&gt;</c> and falls
    /// back to <c>&lt;updated&gt;</c>; <c>author</c> is the first <c>&lt;author&gt;/&lt;name&gt;</c>.
    /// Both are omitted when the feed does not carry them.
    /// </summary>
    private static Dictionary<string, string>? BuildAtomMetadata(
        XElement entry, string url, string? updated)
    {
        var published = entry.Element(s_atomNs + "published")?.Value ?? updated;
        var author = entry.Element(s_atomNs + "author")?.Element(s_atomNs + "name")?.Value;
        return BuildMetadata(url, published, author);
    }

    /// <summary>
    /// Tags for an RSS 2.0 item: <c>&lt;pubDate&gt;</c> and <c>&lt;author&gt;</c> where present.
    /// </summary>
    private static Dictionary<string, string>? BuildRssMetadata(
        XElement item, string url, string? pubDate)
        => BuildMetadata(url, pubDate, item.Element("author")?.Value);

    private static Dictionary<string, string>? BuildMetadata(
        string url, string? publishedAt, string? author)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(url))         metadata["url"]          = url;
        if (!string.IsNullOrEmpty(publishedAt)) metadata["published_at"] = publishedAt;
        if (!string.IsNullOrEmpty(author))      metadata["author"]       = author;
        return metadata.Count == 0 ? null : metadata;
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
