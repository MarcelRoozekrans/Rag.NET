using Refit;

namespace Rag.NET.DataProviders.Jira;

[Headers("Accept: application/json")]
internal interface IJiraApi
{
    [Get("/rest/api/3/search")]
    Task<JiraSearchResult> SearchAsync(
        [Query] string jql,
        [Query] int maxResults,
        [Query] int startAt,
        [Query] string fields = "summary,description,status,priority,assignee,comment,updated",
        CancellationToken cancellationToken = default);
}
