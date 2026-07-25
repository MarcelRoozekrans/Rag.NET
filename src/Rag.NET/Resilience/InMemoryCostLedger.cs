using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Resilience;

/// <summary>
/// In-memory <see cref="ICostLedger"/> for tests and development: same per-(UTC day, kind)
/// accumulation and day/month window semantics as <c>SqliteCostLedger</c>, without
/// persistence. Thread-safe via a lock.
/// </summary>
public sealed class InMemoryCostLedger(TimeProvider? timeProvider = null) : ICostLedger
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<(DateOnly Day, CostKind Kind), Bucket> _buckets = [];
    private readonly Lock _lock = new();

    /// <summary>No-op: there is no storage to prepare.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var key = (Today(), entry.Kind);
        lock (_lock)
        {
            var bucket = _buckets.TryGetValue(key, out var existing) ? existing : new Bucket();
            _buckets[key] = new Bucket
            {
                TokensIn = bucket.TokensIn + entry.InputTokens,
                TokensOut = bucket.TokensOut + entry.OutputTokens,
                Cost = bucket.Cost + entry.Cost,
            };
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
            foreach (var (key, bucket) in _buckets)
            {
                if (key.Day >= lower && key.Day <= today)
                {
                    total += bucket.Cost;
                }
            }
        }

        return Task.FromResult(total);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

    private readonly record struct Bucket
    {
        public long TokensIn { get; init; }
        public long TokensOut { get; init; }
        public decimal Cost { get; init; }
    }
}
