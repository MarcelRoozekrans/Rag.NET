using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Linear;

/// <summary>
/// Minimal Linear GraphQL API surface — a single POST to <c>/graphql</c>
/// (Notion's POST-with-<c>[Body]</c> pattern; no dedicated GraphQL client dependency).
/// </summary>
[ZeroAllocRestClient]
internal interface ILinearApi
{
    [Post("/graphql")]
    Task<Result<LinearGraphQlResponse, ZeroAlloc.Rest.HttpError>> QueryAsync(
        [Body] LinearGraphQlRequest body,
        CancellationToken cancellationToken = default);
}
