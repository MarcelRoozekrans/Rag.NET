using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>
/// Enumerates Zendesk Help Center articles as Markdown documents via the Zendesk REST API v2.
/// <para>
/// A full run uses <c>start_time=0</c> and paginates while <c>count == 1000</c> (the API
/// maximum per page). A delta run uses <see cref="ZendeskArticlesOptions.DeltaToken"/> as
/// the <c>start_time</c> Unix epoch.
/// </para>
/// <para>
/// Each article is emitted as a <c>.md</c> file with HTML tags stripped from the body.
/// </para>
/// </summary>
public sealed partial class ZendeskArticlesDataProvider : FileContentProviderBase
{
    private const int MaxPageSize = 1000;

    private readonly IZendeskApi _api;
    private readonly ZendeskArticlesOptions _options;

    internal ZendeskArticlesDataProvider(IZendeskApi api, ZendeskArticlesOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _options = options;
    }

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long startTime = _options.DeltaToken is not null
            ? long.Parse(_options.DeltaToken, CultureInfo.InvariantCulture)
            : 0;

        bool hasMore = true;

        while (hasMore)
        {
            var articlesResult = await _api.GetIncrementalArticlesAsync(
                startTime, cancellationToken).ConfigureAwait(false);

            if (articlesResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(articlesResult.Error.StatusCode, articlesResult.Error.Message));
                yield break;
            }

            var result = articlesResult.Value;

            for (int i = 0; i < result.Articles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(result.Articles[i]));
            }

            _lastEndTime = result.EndTime;

            // The incremental articles endpoint returns up to 1000 per page.
            // If count == 1000, there may be more pages.
            hasMore = result.Count >= MaxPageSize;
            startTime = result.EndTime;
        }
    }

    private long _lastEndTime;

    /// <summary>
    /// Returns the delta token from the last completed traversal.
    /// Callers can persist this value and pass it back via
    /// <see cref="ZendeskArticlesOptions.DeltaToken"/> for incremental runs.
    /// </summary>
    public string? GetDeltaToken() =>
        _lastEndTime > 0 ? _lastEndTime.ToString(CultureInfo.InvariantCulture) : null;

    // Instance rather than static so the Zendesk subdomain — the container context callers
    // filter on — is reachable from _options.
    private FileHandle ToHandle(ZendeskArticle article)
    {
        var markdown = ToMarkdown(article);
        return new FileHandle(
            Id: article.Id.ToString(CultureInfo.InvariantCulture),
            FileName: $"article-{article.Id}.md",
            ETag: article.UpdatedAt,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata: BuildMetadata(article),
            UpdatedAt: ConnectorTimestampParser.Parse(article.UpdatedAt));
    }

    /// <summary>
    /// The article's filterable fields. <c>section_id</c> is the Help Center section the article
    /// lives in — already parsed off the wire and, until now, used nowhere.
    /// </summary>
    private Dictionary<string, MetadataValue> BuildMetadata(ZendeskArticle article)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["article_id"] = article.Id.ToString(CultureInfo.InvariantCulture),
        };
        if (article.SectionId is { } sectionId)
            metadata["section_id"] = sectionId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(_options.Subdomain)) metadata["subdomain"] = _options.Subdomain;
        return metadata;
    }

    private static string ToMarkdown(ZendeskArticle article)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {article.Title}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(article.Body))
        {
            var stripped = HtmlTagRegex().Replace(article.Body, string.Empty);
            stripped = WebUtility.HtmlDecode(stripped);
            sb.AppendLine(stripped.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlTagRegex();
}
