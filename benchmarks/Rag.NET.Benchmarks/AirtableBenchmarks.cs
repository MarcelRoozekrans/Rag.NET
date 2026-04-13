using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Airtable;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Airtable-specific benchmarks: full traversal, records with attachments,
/// and delta traversal with filterByFormula.
/// </summary>
[MemoryDiagnoser]
public class AirtableBenchmarks
{
    private AirtableDataProvider _fullProvider = default!;
    private AirtableDataProvider _attachmentProvider = default!;
    private AirtableDataProvider _deltaProvider = default!;

    // Providers use NSubstitute mocks — recreate each iteration to prevent
    // call-record accumulation from inflating per-iteration latency.
    [IterationSetup]
    public void IterationSetup()
    {
        _fullProvider = ConnectorIngestionBenchmarks.CreateAirtableProvider(
            count: 20, attachmentsPerRecord: 0);
        _attachmentProvider = ConnectorIngestionBenchmarks.CreateAirtableProvider(
            count: 10, attachmentsPerRecord: 2);
        _deltaProvider = ConnectorIngestionBenchmarks.CreateAirtableProvider(
            count: 20, attachmentsPerRecord: 0, withDeltaFilter: true);
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
    public async Task<int> WithAttachments()
    {
        int count = 0;
        await foreach (var file in _attachmentProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> DeltaWithFilter()
    {
        int count = 0;
        await foreach (var file in _deltaProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }
}
