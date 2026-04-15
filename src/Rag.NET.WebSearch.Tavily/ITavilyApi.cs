using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.WebSearch.Tavily;

[ZeroAllocRestClient]
internal interface ITavilyApi
{
    [Post("/search")]
    Task<Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError>> SearchAsync(
        [Body] TavilySearchRequest body,
        CancellationToken cancellationToken = default);
}
