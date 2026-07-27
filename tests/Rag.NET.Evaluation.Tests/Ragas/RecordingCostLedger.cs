using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>An <see cref="ICostLedger"/> that keeps every entry written to it.</summary>
internal sealed class RecordingCostLedger : ICostLedger
{
    private readonly Lock _gate = new();
    private readonly List<CostEntry> _entries = [];

    /// <summary>Everything recorded so far, in write order.</summary>
    public IReadOnlyList<CostEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
        => Task.FromResult(0m);
}
