using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Resilience;

/// <summary>
/// In-memory <see cref="ICostLedger"/> for tests and development: same per-(UTC day, kind)
/// accumulation and day/month window semantics as <c>SqliteCostLedger</c>, without
/// persistence. Only cost is accumulated — token and page counts are accepted but not
/// retained (<see cref="GetSpendAsync"/> is the sole read surface here; the SQLite ledger
/// keeps the token and page columns as the queryable record). Thread-safe via a lock.
/// </summary>
public sealed class InMemoryCostLedger(TimeProvider? timeProvider = null) : ICostLedger
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<(DateOnly Day, CostKind Kind), decimal> _spend = [];
    private readonly Lock _lock = new();

    /// <summary>No-op: there is no storage to prepare.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var key = (Today(), entry.Kind);
        lock (_lock)
        {
            _spend[key] = _spend.TryGetValue(key, out var existing) ? existing + entry.Cost : entry.Cost;
        }

        return Task.CompletedTask;
    }

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
    {
        var today = Today();
        var lower = window == CostWindow.Day ? today : new DateOnly(today.Year, today.Month, 1);

        decimal total = 0m;
        lock (_lock)
        {
            foreach (var (key, cost) in _spend)
            {
                if (key.Day >= lower && key.Day <= today)
                {
                    total += cost;
                }
            }
        }

        return Task.FromResult(total);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
}
