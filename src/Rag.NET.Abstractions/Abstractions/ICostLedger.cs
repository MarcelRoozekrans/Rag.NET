using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Accumulates LLM usage (tokens and cost) into per-day buckets and answers spend
/// queries over calendar windows, so budget enforcement can survive restarts.
/// Days are UTC calendar days.
/// </summary>
public interface ICostLedger
{
    /// <summary>Prepares the underlying storage (idempotent).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a usage record to the current UTC day's bucket (accumulating).</summary>
    /// <param name="entry">The usage record to add.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Total recorded cost within <paramref name="window"/> ending today (inclusive):
    /// <see cref="CostWindow.Day"/> is today's bucket, <see cref="CostWindow.Month"/>
    /// covers the first of the current UTC month through today.
    /// </summary>
    /// <param name="window">The aggregation window.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default);
}
