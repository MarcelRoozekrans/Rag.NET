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

    [GlobalSetup]
    public void Setup()
    {
        // 20 records, no attachments
        _fullProvider = ConnectorIngestionBenchmarks.CreateAirtableProvider(
            count: 20, attachmentsPerRecord: 0);

        // 10 records with 2 attachments each
        _attachmentProvider = ConnectorIngestionBenchmarks.CreateAirtableProvider(
            count: 10, attachmentsPerRecord: 2);

        // 20 records via filterByFormula (delta)
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
