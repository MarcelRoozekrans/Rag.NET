using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>An <see cref="ICostLedger"/> that is down, to prove billing never fails a run.</summary>
internal sealed class ThrowingCostLedger : ICostLedger
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("ledger is down");

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
        => Task.FromResult(0m);
}
