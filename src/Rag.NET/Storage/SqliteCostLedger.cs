using System.Globalization;
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="ICostLedger"/>. Usage accumulates into one
/// row per (UTC day, kind) in a <c>cost_ledger</c> table, so daily/monthly budget windows
/// survive restarts.
/// </summary>
/// <remarks>
/// Cost is stored as an invariant-culture TEXT decimal (SQLite has no decimal type).
/// Token counters accumulate in SQL via the ON CONFLICT upsert; the cost total is
/// accumulated in C# <see langword="decimal"/> inside an immediate transaction instead,
/// because SQLite's <c>cost + excluded.cost</c> would coerce the TEXT to REAL and
/// introduce binary floating-point drift (e.g. 0.1 + 0.2). The injected
/// <see cref="TimeProvider"/> supplies the UTC day key, making window rollover testable.
/// </remarks>
public sealed class SqliteCostLedger : ICostLedger
{
    private const string DayFormat = "yyyy-MM-dd";

    private readonly string _dbPath;
    private readonly TimeProvider _timeProvider;

    public SqliteCostLedger(string dbPath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        using var conn = SqliteStoreHelper.OpenConnection(dbPath);
        EnsureTable(conn);
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cost_ledger (
                day        TEXT,
                kind       TEXT,
                tokens_in  INTEGER,
                tokens_out INTEGER,
                cost       TEXT,
                PRIMARY KEY (day, kind)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Idempotent; the table is already ensured by the constructor.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        EnsureTable(conn);
        return Task.CompletedTask;
    }

    public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string day = Today().ToString(DayFormat, CultureInfo.InvariantCulture);
        string kind = entry.Kind.ToString();

        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        // Immediate transaction: the read-accumulate-write of the decimal cost must not
        // interleave with another writer (see class remarks for why cost is not summed in SQL).
        using var transaction = conn.BeginTransaction(deferred: false);

        decimal existingCost = ReadCost(conn, transaction, day, kind);
        Upsert(conn, transaction, day, kind, entry, existingCost + entry.Cost);

        transaction.Commit();
        return Task.CompletedTask;
    }

    private static decimal ReadCost(SqliteConnection conn, SqliteTransaction transaction, string day, string kind)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT cost FROM cost_ledger WHERE day = $day AND kind = $kind";
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$kind", kind);
        return cmd.ExecuteScalar() is string text
            ? decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
            : 0m;
    }

    private static void Upsert(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string day,
        string kind,
        CostEntry entry,
        decimal totalCost)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO cost_ledger (day, kind, tokens_in, tokens_out, cost)
            VALUES ($day, $kind, $in, $out, $cost)
            ON CONFLICT (day, kind) DO UPDATE SET
                tokens_in  = tokens_in + excluded.tokens_in,
                tokens_out = tokens_out + excluded.tokens_out,
                cost       = excluded.cost
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$in", entry.InputTokens);
        cmd.Parameters.AddWithValue("$out", entry.OutputTokens);
        // The pre-accumulated decimal total (never SQL arithmetic on the TEXT column).
        cmd.Parameters.AddWithValue("$cost", totalCost.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
    {
        var today = Today();
        string upper = today.ToString(DayFormat, CultureInfo.InvariantCulture);
        string lower = window == CostWindow.Day
            ? upper
            : new DateTime(today.Year, today.Month, 1).ToString(DayFormat, CultureInfo.InvariantCulture);

        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        // Day keys are sortable "yyyy-MM-dd" strings, so string range comparison is date
        // comparison. Summed in C# decimal — SQLite SUM() would coerce TEXT to REAL.
        cmd.CommandText = "SELECT cost FROM cost_ledger WHERE day >= $lower AND day <= $upper";
        cmd.Parameters.AddWithValue("$lower", lower);
        cmd.Parameters.AddWithValue("$upper", upper);

        decimal total = 0m;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            total += decimal.Parse(reader.GetString(0), NumberStyles.Number, CultureInfo.InvariantCulture);
        }

        return Task.FromResult(total);
    }

    private DateTime Today() => _timeProvider.GetUtcNow().UtcDateTime.Date;
}
