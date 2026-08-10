using System.Globalization;
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
/// <remarks>
/// Phase 4.10 Task 5: Atom's <c>&lt;published&gt;</c>/<c>&lt;updated&gt;</c> become the typed
/// <see cref="FileEntry.CreatedAt"/>/<see cref="FileEntry.UpdatedAt"/>, parsed via
/// <see cref="ConnectorTimestampParser"/>. RSS 2.0 has no <c>published</c>/<c>updated</c> split —
/// only <c>&lt;pubDate&gt;</c> — so only <c>CreatedAt</c> is set for RSS 2.0; <c>UpdatedAt</c>
/// stays unset rather than fabricated. The existing <c>published_at</c> metadata tag (see
/// <see cref="BuildAtomMetadata"/>/<see cref="BuildRssMetadata"/>) is unaffected.
/// </remarks>
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
        var xml = await _httpClient.GetStringWithCharsetFallbackAsync(_feedUrl, cancellationToken).ConfigureAwait(false);
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
                var published = entry.Element(s_atomNs + "published")?.Value;
                var updated = entry.Element(s_atomNs + "updated")?.Value;

                yield return Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId(id),
                    FileName: InferFileName(id),
                    OpenContentAsync: Fetch(link),
                    ETag: updated,
                    Metadata: BuildAtomMetadata(entry, link, updated),
                    CreatedAt: ConnectorTimestampParser.Parse(published),
                    UpdatedAt: ConnectorTimestampParser.Parse(updated)));
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
                    Metadata: BuildRssMetadata(item, link ?? id, pubDate),
                    CreatedAt: ConnectorTimestampParser.Parse(pubDate)));
                // RSS 2.0 has no published/updated split — only pubDate. UpdatedAt stays
                // unset rather than fabricated; Atom (above) sets both.
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
    private static Dictionary<string, MetadataValue>? BuildAtomMetadata(
        XElement entry, string url, string? updated)
    {
        var published = entry.Element(s_atomNs + "published")?.Value ?? updated;
        var author = entry.Element(s_atomNs + "author")?.Element(s_atomNs + "name")?.Value;
        return BuildMetadata(url, published, author);
    }

    /// <summary>
    /// Tags for an RSS 2.0 item: <c>&lt;pubDate&gt;</c> and <c>&lt;author&gt;</c> where present.
    /// </summary>
    private static Dictionary<string, MetadataValue>? BuildRssMetadata(
        XElement item, string url, string? pubDate)
        => BuildMetadata(url, pubDate, item.Element("author")?.Value);

    private static Dictionary<string, MetadataValue>? BuildMetadata(
        string url, string? publishedAt, string? author)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(url))    metadata["url"]    = url;
        if (!string.IsNullOrEmpty(author)) metadata["author"] = author;

        var normalised = NormaliseTimestamp(publishedAt);
        if (normalised is not null) metadata["published_at"] = normalised;

        return metadata.Count == 0 ? null : metadata;
    }

    /// <summary>
    /// Renders a feed timestamp as round-trip ISO-8601. The two feed shapes this provider handles
    /// disagree on format — Atom carries ISO-8601, RSS 2.0 carries RFC 822 <c>pubDate</c> — and
    /// writing both under one <c>published_at</c> key would make ordering and range filters
    /// meaningless within a single provider. Both parse with <see cref="DateTimeOffset.TryParse"/>.
    /// <para>
    /// A value that does not parse is passed through unchanged rather than dropped: a
    /// non-conforming feed is still better described by its own string than by nothing. This is
    /// deliberately unlike the sitemap's <c>lastmod</c>, which varies only in precision within a
    /// single format and is therefore sound as a pass-through.
    /// </para>
    /// </summary>
    private static string? NormaliseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToString("o", CultureInfo.InvariantCulture)
            : value;
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
