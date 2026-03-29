using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Asana;

/// <summary>
/// Enumerates Asana tasks as Markdown documents via the Asana REST API.
/// <para>
/// Tasks are scoped to <see cref="AsanaOptions.ProjectGid"/> when set, otherwise to
/// <see cref="AsanaOptions.WorkspaceGid"/>. Subtasks are fetched per task.
/// The bearer token is refreshed on every enumeration via the registered
/// <see cref="ITokenProvider"/>.
/// </para>
/// <para>
/// Delta support uses the <c>modified_since</c> query parameter through
/// <see cref="AsanaOptions.DeltaToken"/>.
/// </para>
/// </summary>
public sealed class AsanaDataProvider : FileContentProviderBase
{
    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly AsanaOptions _options;

    private const string OptFields =
        "gid,name,notes,due_on,completed,assignee.name,modified_at";

    internal AsanaDataProvider(HttpClient http, ITokenProvider tokenProvider, AsanaOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _http          = http;
        _tokenProvider = tokenProvider;
        _options       = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Resolve token per-call so expiring tokens are always fresh.
        // Note: DefaultRequestHeaders.Authorization is mutated here, so concurrent enumeration
        // on the same provider instance is not safe. Provider is registered as singleton and
        // consumers are expected to enumerate sequentially.
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        var api = RestService.For<IAsanaApi>(_http);

        string? offset = null;
        var modifiedSince = _options.DeltaToken;

        do
        {
            AsanaTaskList result;
            if (_options.ProjectGid is not null)
                result = await api.GetProjectTasksAsync(
                    _options.ProjectGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);
            else
                result = await api.GetWorkspaceTasksAsync(
                    _options.WorkspaceGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < result.Data.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = result.Data[i];
                var subtasks = await api.GetSubtasksAsync(task.Gid,
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
