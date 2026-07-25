using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.Resilience;

/// <summary>
/// Deterministic <see cref="ICostLedger"/> fake: returns canned per-window spend, captures
/// recorded entries, optionally throws on read/write, and logs operations into a shared
/// call-order list.
/// </summary>
internal sealed class FakeCostLedger(List<string>? log = null) : ICostLedger
{
    public decimal DaySpend { get; set; }
    public decimal MonthSpend { get; set; }
    public Exception? ThrowOnRead { get; set; }
    public Exception? ThrowOnRecord { get; set; }
    public List<CostEntry> Recorded { get; } = [];

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        log?.Add("ledger-record");
        if (ThrowOnRecord is not null) throw ThrowOnRecord;
        Recorded.Add(entry);
        return Task.CompletedTask;
    }

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
    {
        log?.Add($"ledger-read:{window}");
        if (ThrowOnRead is not null) throw ThrowOnRead;
        return Task.FromResult(window == CostWindow.Day ? DaySpend : MonthSpend);
    }
}
