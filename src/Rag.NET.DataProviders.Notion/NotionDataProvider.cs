using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Notion;

/// <summary>
/// Enumerates Notion pages as Markdown documents via the Notion API.
/// <para>
/// Uses <c>POST /v1/search</c> to discover pages and
/// <c>GET /v1/blocks/{id}/children</c> to fetch block content.
/// A delta run sorts results descending by <c>last_edited_time</c> and applies a
/// client-side time filter using <see cref="NotionOptions.DeltaToken"/>, stopping
/// pagination once older pages are encountered.
/// </para>
/// <para>
/// Supports paragraphs, headings, bulleted and numbered lists, code blocks, and quotes.
/// </para>
/// </summary>
public sealed class NotionDataProvider : FileContentProviderBase
{
    private readonly INotionApi _api;
    private readonly NotionOptions _options;

    internal NotionDataProvider(INotionApi api, NotionOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NotionSort? sort = _options.DeltaToken is not null
            ? new NotionSort("descending", "last_edited_time")
            : null;

        string? cursor = null;
        bool stopPaging = false;
        do
        {
            var result = await FetchPageAsync(sort, cursor, cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));
                yield break;
            }

            var searchResult = result.Value;
            for (int i = 0; i < searchResult.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = searchResult.Results[i];

                // Delta: results are sorted descending by last_edited_time.
                // Once we encounter a page that is not newer than DeltaToken, stop paging.
                if (_options.DeltaToken is not null
                    && string.Compare(page.LastEditedTime, _options.DeltaToken,
                        StringComparison.Ordinal) <= 0)
                {
                    stopPaging = true;
                    break;
                }

                        var handle = await BuildHandleAsync(page, cancellationToken).ConfigureAwait(false);
                if (handle.IsFailure) { yield return Result<FileHandle, RagError>.Failure(handle.Error); yield break; }
                yield return Result<FileHandle, RagError>.Success(handle.Value);
            }

            cursor = (!stopPaging && searchResult.HasMore) ? searchResult.NextCursor : null;
        }
        while (cursor is not null);
    }

    /// <summary>
    /// One page of results, from whichever endpoint the configuration selects.
    /// <para>
    /// <see cref="NotionOptions.DatabaseId"/> scopes ingestion to a single database.
    /// <c>/v1/search</c> cannot express that — it accepts no <c>database_id</c> filter and returns
    /// every page the integration can see — so scoping means querying the database endpoint
    /// instead, which is why the property was unread for as long as this provider only searched
    /// (issue #108).
    /// </para>
    /// <para>
    /// Both endpoints return the same list envelope, so paging, delta and handle-building are
    /// shared below rather than duplicated per endpoint; only the request differs, and the two
    /// request shapes differ enough to be separate records — see
    /// <see cref="NotionDatabaseQueryRequest"/>.
    /// </para>
    /// </summary>
    /// <param name="sort">The delta sort, or <see langword="null"/> for a full traversal.</param>
    /// <param name="cursor">The pagination cursor, or <see langword="null"/> for the first page.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page of results, or the HTTP failure.</returns>
    private Task<Result<NotionSearchResult, ZeroAlloc.Rest.HttpError>> FetchPageAsync(
        NotionSort? sort, string? cursor, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(_options.DatabaseId)
            ? _api.SearchAsync(
                body: new NotionSearchRequest(new NotionFilter("object", "page"), 100, cursor, sort),
                cancellationToken)
            : _api.QueryDatabaseAsync(
                _options.DatabaseId,
                body: new NotionDatabaseQueryRequest(100, cursor, sort is null ? null : [sort]),
                cancellationToken);

    private async Task<Result<FileHandle, RagError>> BuildHandleAsync(
        NotionPage page, CancellationToken cancellationToken)
    {
        var blocksResult = await FetchBlocksAsync(page.Id, cancellationToken).ConfigureAwait(false);
        if (blocksResult.IsFailure)
            return Result<FileHandle, RagError>.Failure(blocksResult.Error);

        var title    = GetTitle(page);
        var markdown = BlocksToMarkdown(title, blocksResult.Value);
        return Result<FileHandle, RagError>.Success(new FileHandle(
            Id:               page.Id,
            FileName:         $"{FileNameSanitizer.Sanitize(title, $"page-{page.Id}")}.md",
            ETag:             page.LastEditedTime,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata:         BuildMetadata(page),
            UpdatedAt:        ConnectorTimestampParser.Parse(page.LastEditedTime)));
    }

    /// <summary>
    /// The page's filterable fields.
    /// <para>
    /// <c>database_id</c> is written <b>only</b> when <see cref="NotionOptions.DatabaseId"/>
    /// scoped the enumeration, and that condition is the whole point. Under <c>/v1/search</c> the
    /// results are every page the integration can see and carry no parent object, so tagging them
    /// with a database id would write it onto documents provably not in that database —
    /// <c>HasTagSpec("database_id", …)</c> would then return wrong documents with no signal that
    /// anything was off. Under <c>/v1/databases/{id}/query</c> every result is by construction a
    /// page of that database, so the key is true of every document it is written to.
    /// </para>
    /// </summary>
    private Dictionary<string, MetadataValue>? BuildMetadata(NotionPage page)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(page.Id)) metadata["page_id"] = page.Id;
        if (!string.IsNullOrWhiteSpace(_options.DatabaseId)) metadata["database_id"] = _options.DatabaseId;
        return metadata.Count == 0 ? null : metadata;
    }

    private async Task<Result<IReadOnlyList<NotionBlock>, RagError>> FetchBlocksAsync(
        string pageId, CancellationToken cancellationToken)
    {
        var all = new List<NotionBlock>();
        string? cursor = null;
        do
        {
            var result = await _api.GetBlockChildrenAsync(pageId, start_cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
                return Result<IReadOnlyList<NotionBlock>, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));

            all.AddRange(result.Value.Results);
            cursor = result.Value.HasMore ? result.Value.NextCursor : null;
        }
        while (cursor is not null);
        return Result<IReadOnlyList<NotionBlock>, RagError>.Success(all);
    }

    private static string GetTitle(NotionPage page)
    {
        foreach (var prop in page.Properties.Values)
        {
            if (prop.Title is { Count: > 0 })
                return ConcatRichText(prop.Title);
        }
        return page.Id;
    }

    private static string ConcatRichText(IReadOnlyList<NotionRichText> richText)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < richText.Count; i++)
            sb.Append(richText[i].PlainText);
        return sb.ToString();
    }

    private static string BlocksToMarkdown(string title, IReadOnlyList<NotionBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var text = GetRichText(block);
            sb.AppendLine(block.Type switch
            {
                "heading_1"          => $"# {text}",
                "heading_2"          => $"## {text}",
                "heading_3"          => $"### {text}",
                "bulleted_list_item" => $"- {text}",
                "numbered_list_item" => $"1. {text}",
                "code"               => $"```{block.Code?.Language ?? string.Empty}\n{text}\n```",
                "quote"              => $"> {text}",
                _                    => text
            });
        }
        return sb.ToString().TrimEnd();
    }

    private static string GetRichText(NotionBlock block)
    {
        var content = block.Type switch
        {
            "paragraph"          => block.Paragraph,
            "heading_1"          => block.Heading1,
            "heading_2"          => block.Heading2,
            "heading_3"          => block.Heading3,
            "bulleted_list_item" => block.BulletedListItem,
            "numbered_list_item" => block.NumberedListItem,
            "code"               => block.Code,
            "quote"              => block.Quote,
            _                    => null
        };
        return content?.RichText is null ? string.Empty
            : ConcatRichText(content.RichText);
    }
}
