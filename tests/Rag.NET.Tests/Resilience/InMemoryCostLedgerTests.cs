using Rag.NET.Models;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

/// <summary>
/// Parity subset of <c>SqliteCostLedgerTests</c>: same day/month window semantics, in memory.
/// </summary>
public sealed class InMemoryCostLedgerTests
{
    private static readonly DateTimeOffset s_midJuly = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static CostEntry Entry(decimal cost, CostKind kind = CostKind.Chat) => new()
    {
        Kind = kind,
        InputTokens = 10,
        OutputTokens = 5,
        Cost = cost,
    };

    [Fact]
    public async Task RecordAsync_SameDayAndKind_Accumulates()
    {
        var sut = new InMemoryCostLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(Entry(0.25m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(Entry(0.75m), TestContext.Current.CancellationToken);

        Assert.Equal(1.00m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSpendAsync_DayExcludesYesterday_MonthIncludesIt()
    {
        var time = new FakeUtcTimeProvider(s_midJuly);
        var sut = new InMemoryCostLedger(time);
        await sut.RecordAsync(Entry(1.00m), TestContext.Current.CancellationToken);

        time.UtcNow = s_midJuly.AddDays(1);
        await sut.RecordAsync(Entry(0.10m, CostKind.Embedding), TestContext.Current.CancellationToken);

        Assert.Equal(0.10m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(1.10m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSpendAsync_MonthBoundary_PreviousMonthInvisible()
    {
        var time = new FakeUtcTimeProvider(new DateTimeOffset(2026, 7, 31, 23, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryCostLedger(time);
        await sut.RecordAsync(Entry(5.00m), TestContext.Current.CancellationToken);

        time.UtcNow = new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSpendAsync_EmptyLedger_ReturnsZero()
    {
        var sut = new InMemoryCostLedger(new FakeUtcTimeProvider(s_midJuly));

        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSpendAsync_OcrSpend_CountsTowardTheSameWindowAsTokenKinds()
    {
        // Parity with SqliteCostLedger: Ocr is its own (day, kind) bucket, but the window
        // aggregation filters on day alone, so OCR spend is part of the one budget.
        var sut = new InMemoryCostLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(Entry(0.30m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(
            new CostEntry { Kind = CostKind.Ocr, Pages = 8, Cost = 0.20m },
            TestContext.Current.CancellationToken);

        Assert.Equal(0.50m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0.50m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_IsANoOp()
    {
        var sut = new InMemoryCostLedger(new FakeUtcTimeProvider(s_midJuly));
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }
}
