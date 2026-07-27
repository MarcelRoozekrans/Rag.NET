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

    /// <summary>Per-page entry: pages and no tokens, which is the point of <see cref="CostKind.Ocr"/>.</summary>
    private static CostEntry OcrEntry(int pages, decimal cost) => new()
    {
        Kind = CostKind.Ocr,
        Pages = pages,
        Cost = cost,
    };

    private (long TokensIn, long TokensOut, long Pages) ReadCounters(string kind)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tokens_in, tokens_out, pages FROM cost_ledger WHERE kind = $kind";
        cmd.Parameters.AddWithValue("$kind", kind);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var row = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        Assert.False(reader.Read()); // exactly one accumulated row
        return row;
    }

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
        var (tokensIn, tokensOut, _) = ReadCounters("Chat");
        Assert.Equal(60, tokensIn);
        Assert.Equal(40, tokensOut);
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

    // ── Per-page (OCR) entries ───────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_OcrEntry_PagesSurvivePersistenceWithZeroTokens()
    {
        var time = new FakeUtcTimeProvider(s_midJuly);
        var first = CreateLedger(time);

        await first.RecordAsync(OcrEntry(12, 0.018m), TestContext.Current.CancellationToken);

        // Simulate restart — the pages column is the queryable record, not in-process state.
        var second = CreateLedger(time);
        Assert.Equal(0.018m, await second.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));

        var (tokensIn, tokensOut, pages) = ReadCounters("Ocr");
        Assert.Equal(12, pages);
        Assert.Equal(0, tokensIn);  // never fabricate tokens for a per-page API
        Assert.Equal(0, tokensOut);
    }

    [Fact]
    public async Task RecordAsync_OcrEntriesSameDay_PagesAccumulate()
    {
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(OcrEntry(3, 0.005m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(OcrEntry(7, 0.010m), TestContext.Current.CancellationToken);

        Assert.Equal(0.015m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(10, ReadCounters("Ocr").Pages);
    }

    [Fact]
    public async Task RecordAsync_OcrAlongsideTokenKinds_SeparateBucketsOneBudget()
    {
        // OCR is its own (day, kind) row, but GetSpendAsync filters by day only — so OCR
        // spend counts toward the same window UseCostBudgeting enforces. That is deliberate.
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        await sut.RecordAsync(Entry(CostKind.Chat, 100, 50, 0.30m), TestContext.Current.CancellationToken);
        await sut.RecordAsync(OcrEntry(5, 0.20m), TestContext.Current.CancellationToken);

        Assert.Equal(0.50m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));

        // The OCR pages must not leak into the chat row, nor chat tokens into the OCR row.
        Assert.Equal((100L, 50L, 0L), ReadCounters("Chat"));
        Assert.Equal((0L, 0L, 5L), ReadCounters("Ocr"));
    }

    // ── Schema migration ─────────────────────────────────────────────────────

    [Fact]
    public async Task Ctor_LegacyTableWithoutPagesColumn_MigratesAndPreservesExistingSpend()
    {
        // A cost_ledger created before the pages column existed. CREATE TABLE IF NOT EXISTS
        // would leave it alone and every INSERT naming `pages` would then fail, so the ledger
        // ALTERs it — additively, per its class remarks.
        CreateLegacyTableWithRow(_dbPath, day: "2026-07-15", kind: "Chat", cost: "1.50");

        var time = new FakeUtcTimeProvider(s_midJuly);
        var sut = CreateLedger(time);

        // Pre-existing spend is intact — the migration rewrites no rows.
        Assert.Equal(1.50m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));

        // And the migrated table accepts the new kind.
        await sut.RecordAsync(OcrEntry(4, 0.50m), TestContext.Current.CancellationToken);
        Assert.Equal(2.00m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
        Assert.Equal(4, ReadCounters("Ocr").Pages);

        // The legacy row defaults to zero pages, which is exactly true: it was never billed any.
        Assert.Equal(0, ReadCounters("Chat").Pages);
    }

    [Fact]
    public async Task InitializeAsync_AfterMigration_DoesNotReapplyIt()
    {
        CreateLegacyTableWithRow(_dbPath, day: "2026-07-15", kind: "Chat", cost: "1.50");
        var sut = CreateLedger(new FakeUtcTimeProvider(s_midJuly));

        // A second ALTER TABLE ADD COLUMN pages would throw "duplicate column name".
        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1.50m, await sut.GetSpendAsync(CostWindow.Day, TestContext.Current.CancellationToken));
    }

    /// <summary>Writes the pre-<c>pages</c> schema verbatim, with one row already in it.</summary>
    private static void CreateLegacyTableWithRow(string dbPath, string day, string kind, string cost)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE cost_ledger (
                    day        TEXT,
                    kind       TEXT,
                    tokens_in  INTEGER,
                    tokens_out INTEGER,
                    cost       TEXT,
                    PRIMARY KEY (day, kind)
                );
                """;
            create.ExecuteNonQuery();
        }

        using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO cost_ledger (day, kind, tokens_in, tokens_out, cost) VALUES ($day, $kind, 10, 5, $cost)";
        insert.Parameters.AddWithValue("$day", day);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$cost", cost);
        insert.ExecuteNonQuery();
    }
}
