using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Asana;

[ZeroAllocRestClient]
internal interface IAsanaApi
{
    [Get("/api/1.0/tasks")]
    Task<Result<AsanaTaskList, ZeroAlloc.Rest.HttpError>> GetWorkspaceTasksAsync(
        [Query] string workspace,
        [Query(Name = "opt_fields")] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query(Name = "modified_since")] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/projects/{projectGid}/tasks")]
    Task<Result<AsanaTaskList, ZeroAlloc.Rest.HttpError>> GetProjectTasksAsync(
        string projectGid,
        [Query(Name = "opt_fields")] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query(Name = "modified_since")] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/tasks/{taskGid}/subtasks")]
    Task<Result<AsanaTaskList, ZeroAlloc.Rest.HttpError>> GetSubtasksAsync(
        string taskGid,
        [Query(Name = "opt_fields")] string opt_fields = "gid,name",
        CancellationToken cancellationToken = default);
}
