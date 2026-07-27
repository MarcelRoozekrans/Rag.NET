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
/// <para>
/// Cost is stored as an invariant-culture TEXT decimal (SQLite has no decimal type).
/// Token and page counters accumulate in SQL via the ON CONFLICT upsert; the cost total is
/// accumulated in C# <see langword="decimal"/> inside an immediate transaction instead,
/// because SQLite's <c>cost + excluded.cost</c> would coerce the TEXT to REAL and
/// introduce binary floating-point drift (e.g. 0.1 + 0.2). The injected
/// <see cref="TimeProvider"/> supplies the UTC day key, making window rollover testable.
/// </para>
/// <para>
/// <b>Schema migration.</b> The <c>pages</c> column (for per-page kinds such as
/// <see cref="CostKind.Ocr"/>) was added after the initial release. A <c>cost_ledger</c>
/// table created by an earlier version does not have it, and <c>CREATE TABLE IF NOT
/// EXISTS</c> will not add it — so this ledger probes the table and, when the column is
/// absent, runs <c>ALTER TABLE cost_ledger ADD COLUMN pages INTEGER NOT NULL DEFAULT 0</c>
/// against the caller's database. <b>That statement is executed automatically, from the
/// constructor and from <see cref="InitializeAsync"/>.</b> It is additive and
/// non-destructive: no row is rewritten, no column is dropped, and the default of 0 is
/// exactly right for pre-existing chat and embedding rows, which were never billed pages.
/// Failing fast instead would break every existing deployment on upgrade for a change that
/// cannot lose data; silently dropping the value would defeat the purpose of the column.
/// </para>
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
        // Column ORDER differs between a table created here (pages before cost) and one
        // migrated by EnsurePagesColumn (ALTER appends, so pages lands last). Harmless only
        // because every statement in this class names its columns explicitly — never
        // introduce SELECT * or an ordinal-indexed read against cost_ledger.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS cost_ledger (
                    day        TEXT,
                    kind       TEXT,
                    tokens_in  INTEGER,
                    tokens_out INTEGER,
                    pages      INTEGER NOT NULL DEFAULT 0,
                    cost       TEXT,
                    PRIMARY KEY (day, kind)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        EnsurePagesColumn(conn);
    }

    /// <summary>
    /// Adds the <c>pages</c> column to a <c>cost_ledger</c> table created before it existed.
    /// See the class remarks: this ALTERs the caller's table, deliberately and non-destructively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so the column list is probed first — and
    /// the ALTER is <i>also</i> guarded, because the probe alone is not enough. Both halves are
    /// needed: SQLite serialises the two ALTER statements but nothing guards the gap between
    /// this process reading "column absent" and issuing its own ALTER, so two processes opening
    /// the same ledger file concurrently (scaled-out workers on a shared volume during a rolling
    /// restart) both probe absent, both ALTER, and the loser gets "duplicate column name" — out
    /// of the constructor, i.e. as a host startup crash, on the first start after upgrade.
    /// </para>
    /// <para>
    /// The exception filter re-probes rather than matching on the message: an unrelated DDL
    /// failure still propagates, because in that case the column genuinely will not be there.
    /// That is the property the probe-first approach was defending, and it survives intact.
    /// </para>
    /// </remarks>
    private static void EnsurePagesColumn(SqliteConnection conn)
    {
        if (HasPagesColumn(conn))
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE cost_ledger ADD COLUMN pages INTEGER NOT NULL DEFAULT 0";
        try
        {
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) when (HasPagesColumn(conn))
        {
            // Lost a race with another process that migrated between our probe and this ALTER.
            // The re-probe in the filter is what keeps unrelated failures fatal.
        }
    }

    private static bool HasPagesColumn(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        // COLLATE NOCASE: SQLite column names are case-insensitive, so a table carrying PAGES
        // must not probe as absent and send the ALTER into a guaranteed failure.
        cmd.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('cost_ledger') WHERE name = 'pages' COLLATE NOCASE";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Idempotent; the table — and the <c>pages</c>-column migration described in the class
    /// remarks — is already ensured by the constructor.
    /// </summary>
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
            INSERT INTO cost_ledger (day, kind, tokens_in, tokens_out, pages, cost)
            VALUES ($day, $kind, $in, $out, $pages, $cost)
            ON CONFLICT (day, kind) DO UPDATE SET
                tokens_in  = tokens_in + excluded.tokens_in,
                tokens_out = tokens_out + excluded.tokens_out,
                pages      = pages + excluded.pages,
                cost       = excluded.cost
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$in", entry.InputTokens);
        cmd.Parameters.AddWithValue("$out", entry.OutputTokens);
        cmd.Parameters.AddWithValue("$pages", entry.Pages);
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
