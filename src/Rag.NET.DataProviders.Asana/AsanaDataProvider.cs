using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaDataProvider : FileContentProviderBase
{
    private readonly IAsanaApi _api;
    private readonly AsanaOptions _options;

    private const string OptFields =
        "gid,name,notes,due_on,completed,assignee.name,modified_at";

    internal AsanaDataProvider(IAsanaApi api, AsanaOptions options) : base(options)
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
        string? offset = null;
        var modifiedSince = _options.DeltaToken;

        do
        {
            AsanaTaskList result;
            if (_options.ProjectGid is not null)
                result = await _api.GetProjectTasksAsync(
                    _options.ProjectGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);
            else
                result = await _api.GetWorkspaceTasksAsync(
                    _options.WorkspaceGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < result.Data.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = result.Data[i];
                var subtasks = await _api.GetSubtasksAsync(task.Gid,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                yield return ToHandle(task, subtasks.Data);
            }

            offset = result.NextPage?.Offset;
        }
        while (offset is not null);
    }

    private static FileHandle ToHandle(AsanaTask task, List<AsanaTask> subtasks)
    {
        var markdown = ToMarkdown(task, subtasks);
        return new FileHandle(
            Id:               task.Gid,
            FileName:         $"{task.Name}.md",
            ETag:             task.ModifiedAt ?? string.Empty,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(AsanaTask task, List<AsanaTask> subtasks)
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
