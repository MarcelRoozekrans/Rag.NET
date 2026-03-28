using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Confluence;

public sealed partial class ConfluenceDataProvider : FileContentProviderBase
{
    [GeneratedRegex("<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTagRegex();

    private readonly IConfluenceApi _api;
    private readonly ConfluenceOptions _options;

    internal ConfluenceDataProvider(IConfluenceApi api, ConfluenceOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await _api.GetPagesAsync(
                _options.SpaceKey, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < page.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToHandle(page.Results[i]);
            }

            cursor = ExtractCursor(page.Links.Next);
        }
        while (cursor is not null);
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cql = _options.SpaceKey is not null
            ? $"space=\"{_options.SpaceKey}\" AND lastModified>\"{_options.DeltaToken}\""
            : $"lastModified>\"{_options.DeltaToken}\"";

        string? cursor = null;
        do
        {
            var page = await _api.SearchPagesAsync(
                cql, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < page.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToHandle(page.Results[i]);
            }

            cursor = ExtractCursor(page.Links.Next);
        }
        while (cursor is not null);
    }

    private static FileHandle ToHandle(ConfluencePage p)
    {
        var markdown = ToMarkdown(p);
        return new FileHandle(
            Id:              p.Id,
            FileName:        $"{p.Title}.md",
            ETag:            p.Version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(ConfluencePage p)
    {
        var body = HtmlTagRegex().Replace(p.Body.Storage.Value, string.Empty);
        body = System.Net.WebUtility.HtmlDecode(body).Trim();
        return $"# {p.Title}\n\n{body}";
    }

    private static string? ExtractCursor(string? next)
    {
        if (next is null) return null;
        var idx = next.IndexOf("cursor=", StringComparison.Ordinal);
        return idx < 0 ? null : next[(idx + 7)..];
    }
}
