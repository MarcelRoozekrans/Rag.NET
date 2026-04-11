using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Asana;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Asana-specific benchmarks: full traversal and tasks with many subtasks.
/// </summary>
[MemoryDiagnoser]
public class AsanaBenchmarks
{
    private AsanaDataProvider _fullProvider = default!;
    private AsanaDataProvider _manySubtasksProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Full traversal: 20 tasks with empty subtasks
        _fullProvider = ConnectorIngestionBenchmarks.CreateAsanaProvider();

        // Many subtasks: 5 tasks, 20 subtasks each
        _manySubtasksProvider = MakeManySubtasksProvider(5, 20);
    }

    [Benchmark]
    public async Task<int> FullTraversal()
    {
        int count = 0;
        await foreach (var file in _fullProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> ManySubtasks()
    {
        int count = 0;
        await foreach (var file in _manySubtasksProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static AsanaDataProvider MakeManySubtasksProvider(int taskCount, int subtaskCount)
    {
        var tasksJson = ConnectorIngestionBenchmarks.BuildAsanaTasksJson(taskCount);
        var subtasksJson = BuildSubtasksJson(subtaskCount);

        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/1.0/tasks"] = tasksJson,
            ["/subtasks"]      = subtasksJson
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
        return new AsanaDataProvider(http, new StaticTokenProvider("bench-token"),
            new AsanaOptions { WorkspaceGid = "ws-bench" });
    }

    private static string BuildSubtasksJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "data": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""{ "gid": "sub-{{i:D3}}", "name": "Subtask {{i}}" }""");
        }
        sb.Append("] }");
        return sb.ToString();
    }
}
