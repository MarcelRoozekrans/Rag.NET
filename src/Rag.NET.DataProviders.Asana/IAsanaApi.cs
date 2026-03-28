using Refit;

namespace Rag.NET.DataProviders.Asana;

[Headers("Accept: application/json")]
internal interface IAsanaApi
{
    [Get("/api/1.0/tasks")]
    Task<AsanaTaskList> GetWorkspaceTasksAsync(
        [Query] string workspace,
        [Query] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/projects/{projectGid}/tasks")]
    Task<AsanaTaskList> GetProjectTasksAsync(
        string projectGid,
        [Query] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/tasks/{taskGid}/subtasks")]
    Task<AsanaTaskList> GetSubtasksAsync(
        string taskGid,
        [Query] string opt_fields = "gid,name",
        CancellationToken cancellationToken = default);
}
