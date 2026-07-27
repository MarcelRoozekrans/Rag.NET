using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// Hand-written <see cref="ICostLedger"/> (the repo's parser suites use hand-written fakes, not
/// a mocking framework). It records verbatim and prices nothing — pricing is the caller's job,
/// which is exactly the behaviour these tests assert.
/// </summary>
internal sealed class FakeCostLedger : ICostLedger
{
    private readonly List<CostEntry> _entries = [];
    private readonly Exception? _throwOnRecord;

    public FakeCostLedger(Exception? throwOnRecord = null) => _throwOnRecord = throwOnRecord;

    public IReadOnlyList<CostEntry> Entries => _entries;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        if (_throwOnRecord is not null)
        {
            return Task.FromException(_throwOnRecord);
        }

        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
    {
        decimal total = 0m;
        for (int i = 0; i < _entries.Count; i++)
        {
            total += _entries[i].Cost;
        }

        return Task.FromResult(total);
    }
}
