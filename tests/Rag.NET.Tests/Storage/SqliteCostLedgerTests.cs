using Microsoft.Data.Sqlite;
using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public sealed class SqliteCostLedgerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-costledger-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset s_midJuly = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SqliteCostLedger CreateLedger(FakeUtcTimeProvider time) => new(_dbPath, time);

    private static CostEntry Entry(CostKind kind, long tokensIn, long tokensOut, decimal cost) => new()
    {
        Kind = kind,
        InputTokens = tokensIn,
        OutputTokens = tokensOut,
        Cost = cost,
    };

    // ── Accumulation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_SameDayAndKind_AccumulatesIntoOneBucket()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(Entry(CostKind.Chat, 100, 50, 0.25m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(Entry(CostKind.Chat, 200, 100, 0.75m), TestContext.Current.CancellationToken);

        Assert.Equal(1.00m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_DifferentKindsSameDay_BothCountTowardsSpend()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(Entry(CostKind.Chat, 100, 50, 0.30m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(Entry(CostKind.Embedding, 500, 0, 0.20m), TestContext.Current.CancellationToken);

        Assert.Equal(0.50m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSpendAsync_Day_ExcludesYesterday()
    {
        var time = new FakeUtcTimeProvider(s_midJuly);
        var sut = CreateLedger(time);
        await sut.RecordAsync(Entry(CostKind.Chat, 10, 5, 1.00m), TestContext.Current.CancellationToken);

        time.UtcNow = s_midJuly.AddDays(1);
        await sut.RecordAsync(Entry(CostKind.Chat, 10, 5, 0.10m), TestContext.Current.CancellationToken);

        Assert.Equal(0.10m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(1.10m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSpendAsync_MonthBoundary_JulySpendInvisibleOnAugustFirst()
    {
        var time = new FakeUtcTimeProvider(new DateTimeOffset(2026, 7, 31, 23, 0, 0, TimeSpan.Zero));
        var sut = CreateLedger(time);
        await sut.RecordAsync(Entry(CostKind.Chat, 10, 5, 5.00m), TestContext.Current.CancellationToken);

        time.UtcNow = new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));

        await sut.RecordAsync(Entry(CostKind.Chat, 10, 5, 0.40m), TestContext.Current.CancellationToken);
        Assert.Equal(0.40m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_ConcurrentWritesSameBucket_AccumulateExactly()
    {
        // Pins the BEGIN IMMEDIATE read-modify-write: interleaved writers must neither lose
        // an update nor drift the decimal total.
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 20),
            TestContext.Current.CancellationToken,
            async (_, ct) => await sut.RecordAsync(Entry(CostKind.Chat, 3, 2, 0.001m), ct));

        Assert.Equal(0.020m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));

        // Token counters (SQL-accumulated) must be exact too — read the row directly.
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tokens_in, tokens_out FROM cost_ledger WHERE kind = 'Chat'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(60, reader.GetInt64(0));
        Assert.Equal(40, reader.GetInt64(1));
        Assert.False(reader.Read()); // exactly one accumulated row
    }

    // ── Precision ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_TinyDecimals_RoundTripExactly()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(Entry(CostKind.Chat, 1, 1, 0.000123m), TestContext.Current.CancellationToken);

        Assert.Equal(0.000123m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_BinaryUnfriendlyDecimals_AccumulateWithoutFloatDrift()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        // 0.1 + 0.2 is the classic double-drift case (0.30000000000000004); decimal TEXT
        // accumulation must stay exact.
        await sut.RecordAsync(Entry(CostKind.Chat, 1, 1, 0.1m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(Entry(CostKind.Chat, 1, 1, 0.2m), TestContext.Current.CancellationToken);

        Assert.Equal(0.3m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SurvivesRestart_SpendPersistedToSqlite()
    {
        var time = new FakeUtcTimeProvider(s_midJuly);
        var first = CreateLedger(time);
        await first.RecordAsync(Entry(CostKind.Chat, 100, 50, 2.50m), TestContext.Current.CancellationToken);

        // Simulate restart — new instance, same db file.
        var second = CreateLedger(time);

        Assert.Equal(2.50m, await second.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(2.50m, await second.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));
        await sut.RecordAsync(Entry(CostKind.Chat, 1, 1, 0.10m), TestContext.Current.CancellationToken);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0.10m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSpendAsync_EmptyLedger_ReturnsZero()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0m, await sut.GetSpendAsync(CostWindow.Month, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Ctor_BlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SqliteCostLedger(" "));
    }
}
