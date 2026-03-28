using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

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

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NotionSort? sort = _options.DeltaToken is not null
            ? new NotionSort("descending", "last_edited_time")
            : null;

        string? cursor = null;
        do
        {
            var filter = new NotionFilter("object", "page");

            var result = await _api.SearchAsync(
                new NotionSearchRequest(filter, 100, cursor, sort),
                cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < result.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = result.Results[i];

                // Delta: skip pages not modified after DeltaToken
                if (_options.DeltaToken is not null
                    && string.Compare(page.LastEditedTime, _options.DeltaToken,
                        StringComparison.Ordinal) <= 0)
                    continue;

                var blocks   = await FetchBlocksAsync(page.Id, cancellationToken).ConfigureAwait(false);
                var title    = GetTitle(page);
                var markdown = BlocksToMarkdown(title, blocks);

                yield return new FileHandle(
                    Id:               page.Id,
                    FileName:         $"{title}.md",
                    ETag:             page.LastEditedTime,
                    OpenContentAsync: _ => Task.FromResult<Stream>(
                        new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
            }

            cursor = result.HasMore ? result.NextCursor : null;
        }
        while (cursor is not null);
    }

    private async Task<List<NotionBlock>> FetchBlocksAsync(
        string pageId, CancellationToken cancellationToken)
    {
        var all = new List<NotionBlock>();
        string? cursor = null;
        do
        {
            var page = await _api.GetBlockChildrenAsync(pageId, start_cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Results);
            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null);
        return all;
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

    private static string ConcatRichText(List<NotionRichText> richText)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < richText.Count; i++)
            sb.Append(richText[i].PlainText);
        return sb.ToString();
    }

    private static string BlocksToMarkdown(string title, List<NotionBlock> blocks)
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
