using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Jira;

[ZeroAllocRestClient]
internal interface IJiraApi
{
    [Get("/rest/api/3/search")]
    Task<Result<JiraSearchResult, ZeroAlloc.Rest.HttpError>> SearchAsync(
        [Query] string jql,
        [Query] int maxResults,
        [Query] int startAt,
        [Query(Name = "fields")] string fields = "summary,description,status,priority,assignee,comment,updated",
        CancellationToken cancellationToken = default);
}
