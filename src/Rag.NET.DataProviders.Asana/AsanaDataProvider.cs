using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Asana;

/// <summary>
/// Enumerates Asana tasks as Markdown documents via the Asana REST API.
/// <para>
/// Tasks are scoped to <see cref="AsanaOptions.ProjectGid"/> when set, otherwise to
/// <see cref="AsanaOptions.WorkspaceGid"/>. Subtasks are fetched per task.
/// The bearer token is refreshed on every enumeration via the registered
/// <see cref="AsanaTokenHandler"/>.
/// </para>
/// <para>
/// Delta support uses the <c>modified_since</c> query parameter through
/// <see cref="AsanaOptions.DeltaToken"/>.
/// </para>
/// </summary>
public sealed class AsanaDataProvider : FileContentProviderBase
{
    private readonly IAsanaApi _api;
    private readonly AsanaOptions _options;

    private const string OptFields =
        "gid,name,notes,due_on,completed,assignee.name,modified_at";

    internal AsanaDataProvider(IAsanaApi api, AsanaOptions options)
        : base(options)
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
        string? offset = null;
        var modifiedSince = _options.DeltaToken;

        do
        {
            Result<AsanaTaskList, ZeroAlloc.Rest.HttpError> result;
            if (_options.ProjectGid is not null)
                result = await _api.GetProjectTasksAsync(
                    _options.ProjectGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);
            else
                result = await _api.GetWorkspaceTasksAsync(
                    _options.WorkspaceGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));
                yield break;
            }

            var taskList = result.Value;
            for (int i = 0; i < taskList.Data.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = taskList.Data[i];

                var subtasksResult = await _api.GetSubtasksAsync(task.Gid,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (subtasksResult.IsFailure)
                {
                    yield return Result<FileHandle, RagError>.Failure(
                        new RagError.HttpFailed(subtasksResult.Error.StatusCode, subtasksResult.Error.Message));
                    yield break;
                }

                yield return Result<FileHandle, RagError>.Success(
                    ToHandle(task, subtasksResult.Value.Data));
            }

            offset = taskList.NextPage?.Offset;
        }
        while (offset is not null);
    }

    // Instance rather than static so the workspace and project the request was scoped to — the
    // container context callers filter on — are reachable from _options.
    private FileHandle ToHandle(AsanaTask task, IReadOnlyList<AsanaTask> subtasks)
    {
        var markdown = ToMarkdown(task, subtasks);
        return new FileHandle(
            Id:               task.Gid,
            FileName:         $"{FileNameSanitizer.Sanitize(task.Name, $"task-{task.Gid}")}.md",
            ETag:             task.ModifiedAt ?? string.Empty,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata:         BuildMetadata(task),
            UpdatedAt:        ConnectorTimestampParser.Parse(task.ModifiedAt));
    }

    /// <summary>
    /// The task's filterable fields. These are <i>also</i> rendered into the Markdown body by
    /// <see cref="ToMarkdown"/> — the body drives semantic recall, the tags drive filtering, and
    /// removing either would silently degrade the other.
    /// <para>
    /// <c>completed</c> is written as the literal <c>"true"</c>/<c>"false"</c>, deliberately
    /// unlike the Markdown line's <c>bool.ToString()</c> rendering (<c>"True"</c>), which would
    /// not match ordinally in <c>HasTagSpec</c>.
    /// </para>
    /// <para>
    /// <c>workspace</c> is unconditional because <see cref="AsanaOptions.WorkspaceGid"/> is
    /// <c>required</c> and every request is scoped by it; <c>project</c> appears only when
    /// <see cref="AsanaOptions.ProjectGid"/> narrowed the enumeration to one project.
    /// </para>
    /// </summary>
    private Dictionary<string, MetadataValue> BuildMetadata(AsanaTask task)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["completed"] = task.Completed ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(task.Assignee?.Name)) metadata["assignee"]   = task.Assignee.Name;
        if (!string.IsNullOrEmpty(task.DueOn))          metadata["due_on"]     = task.DueOn;
        if (!string.IsNullOrEmpty(_options.WorkspaceGid)) metadata["workspace"] = _options.WorkspaceGid;
        if (!string.IsNullOrEmpty(_options.ProjectGid))   metadata["project"]   = _options.ProjectGid;
        return metadata;
    }

    private static string ToMarkdown(AsanaTask task, IReadOnlyList<AsanaTask> subtasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {task.Name}");
        sb.AppendLine();
        if (task.DueOn is not null)    sb.Append($"**Due:** {task.DueOn}  ");
        if (task.Assignee is not null) sb.Append($"**Assignee:** {task.Assignee.Name}  ");
        sb.AppendLine($"**Completed:** {task.Completed}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            sb.AppendLine(task.Notes);
            sb.AppendLine();
        }
        if (subtasks.Count > 0)
        {
            sb.AppendLine("## Subtasks");
            for (int i = 0; i < subtasks.Count; i++)
                sb.AppendLine($"- {subtasks[i].Name}");
        }
        return sb.ToString().TrimEnd();
    }
}
