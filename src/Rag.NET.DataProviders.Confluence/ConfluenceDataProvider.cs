using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Confluence;

/// <summary>
/// Enumerates Confluence pages as Markdown documents via the Confluence REST API.
/// <para>
/// A full run fetches all pages, optionally filtered by
/// <see cref="ConfluenceOptions.SpaceKey"/>. A delta run appends a CQL
/// <c>lastModified&gt;</c> filter using <see cref="ConfluenceOptions.DeltaToken"/>.
/// When the Atlassian API returns HTTP 400 (stale or invalid token) the provider
/// falls back to a full traversal automatically.
/// </para>
/// <para>
/// Each page is emitted as a <c>.md</c> file with the HTML body stripped to plain text.
/// </para>
/// </summary>
public sealed partial class ConfluenceDataProvider : FileContentProviderBase
{
    [GeneratedRegex("<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-_]+$", RegexOptions.NonBacktracking)]
    private static partial Regex SpaceKeyRegex();

    [GeneratedRegex(@"^[A-Za-z0-9:\-\.TZ\+]+$", RegexOptions.NonBacktracking)]
    private static partial Regex DeltaTokenRegex();

    private readonly IConfluenceApi _api;
    private readonly ConfluenceOptions _options;

    internal ConfluenceDataProvider(IConfluenceApi api, ConfluenceOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (options.DeltaToken is not null && !DeltaTokenRegex().IsMatch(options.DeltaToken))
            throw new ArgumentException(
                $"DeltaToken contains invalid characters: '{options.DeltaToken}'.", nameof(options));
        if (options.SpaceKey is not null && !SpaceKeyRegex().IsMatch(options.SpaceKey))
            throw new ArgumentException(
                $"SpaceKey contains invalid characters: '{options.SpaceKey}'.", nameof(options));
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var result = await _api.GetPagesAsync(
                _options.SpaceKey, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));
                yield break;
            }

            var page = result.Value;
            for (int i = 0; i < page.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(page.Results[i]));
            }

            cursor = ExtractCursor(page.Links.Next);
        }
        while (cursor is not null);
    }

    /// <summary>
    /// Delta traversal using a CQL <c>lastModified&gt;</c> filter.
    /// Falls back to a full traversal when the Atlassian API returns HTTP 400,
    /// which indicates a stale or otherwise invalid <see cref="ConfluenceOptions.DeltaToken"/>.
    /// </summary>
    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cql = _options.SpaceKey is not null
            ? $"space=\"{_options.SpaceKey}\" AND lastModified>\"{_options.DeltaToken}\""
            : $"lastModified>\"{_options.DeltaToken}\"";

        var firstResult = await _api.SearchPagesAsync(
            cql, limit: 50, cursor: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Stale delta token — fall back to full traversal.
        if (firstResult.IsFailure &&
            firstResult.Error.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            await foreach (var h in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return h;
            yield break;
        }

        if (firstResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(firstResult.Error.StatusCode, firstResult.Error.Message));
            yield break;
        }

        // Emit results from the first (already-fetched) page and then continue paging.
        var firstPage = firstResult.Value;
        string? cursor = ExtractCursor(firstPage.Links.Next);
        for (int i = 0; i < firstPage.Results.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Result<FileHandle, RagError>.Success(ToHandle(firstPage.Results[i]));
        }

        while (cursor is not null)
        {
            var result = await _api.SearchPagesAsync(
                cql, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));
                yield break;
            }

            var page = result.Value;
            for (int i = 0; i < page.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(page.Results[i]));
            }

            cursor = ExtractCursor(page.Links.Next);
        }
    }

    // Instance rather than static so the configured space — the container context callers
    // filter on — is reachable from _options.
    private FileHandle ToHandle(ConfluencePage p)
    {
        var markdown = ToMarkdown(p);
        return new FileHandle(
            Id:              p.Id,
            FileName:        $"{FileNameSanitizer.Sanitize(p.Title, $"page-{p.Id}")}.md",
            ETag:            p.Version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata:        BuildMetadata(p),
            UpdatedAt:       ConnectorTimestampParser.Parse(p.Version.When));
    }

    /// <summary>
    /// The page's filterable fields. <c>space</c> comes from the configured
    /// <see cref="ConfluenceOptions.SpaceKey"/> and is omitted when the run is unscoped: the
    /// API response itself does not carry the space, because the request does not expand it.
    /// </summary>
    private Dictionary<string, MetadataValue> BuildMetadata(ConfluencePage p)
    {
        // version is always present, so the dictionary is never empty and never null.
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["version"] = p.Version.Number.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(p.Id))              metadata["page_id"] = p.Id;
        if (!string.IsNullOrEmpty(_options.SpaceKey)) metadata["space"]   = _options.SpaceKey;
        return metadata;
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
        if (idx < 0) return null;
        var end = next.IndexOf('&', idx + 7);
        return end < 0 ? next[(idx + 7)..] : next[(idx + 7)..end];
    }
}
