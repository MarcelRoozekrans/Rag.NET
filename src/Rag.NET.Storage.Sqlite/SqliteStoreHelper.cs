using Microsoft.Data.Sqlite;

namespace Rag.NET.Storage;

/// <summary>
/// Shared SQLite plumbing for the stores in this assembly: opening a connection, and reading,
/// writing and creating the <c>rag_metadata</c> table that each store stamps its collection name
/// into.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>Rag</c> in <see cref="ReadRagMetadata"/>, <see cref="WriteRagMetadata"/> and
/// <see cref="EnsureRagMetadataTable"/> is load-bearing — do not "tidy" it away.</b> These are
/// extension methods on <see cref="SqliteConnection"/>, which is Microsoft's type, not ours. C#
/// resolves an instance method before it ever considers an extension method, so if
/// <c>Microsoft.Data.Sqlite</c> ever ships an instance <c>ReadMetadata</c>, every call site here
/// would silently rebind to it: no compiler error, no warning, different behaviour. The
/// <c>Rag</c> prefix makes that collision essentially impossible, and it names the table these
/// methods actually touch.
/// </para>
/// <para>
/// <see cref="OpenConnection"/> deliberately stays a plain static: its first parameter is a
/// <see cref="string"/> path, and it is a factory rather than an operation on a receiver.
/// </para>
/// </remarks>
internal static class SqliteStoreHelper
{
    /// <summary>Opens an unpooled connection with SQLite's busy timeout set.</summary>
    /// <param name="dbPath">The database file path.</param>
    /// <returns>An open connection.</returns>
    /// <remarks>
    /// <para>
    /// <b>The busy timeout is here because of #640.</b> SQLite's <c>busy_timeout</c> defaults to
    /// <b>0</b>: a connection that cannot take the lock gets <c>SQLITE_BUSY</c> back immediately
    /// rather than waiting for it. The 30 seconds that looked like a timeout was
    /// <c>SqliteCommand.DefaultTimeout</c>, which <c>Microsoft.Data.Sqlite</c> implements as its own
    /// retry loop above SQLite. So twenty concurrent writers were not queueing — they were spinning,
    /// each retry an independent race for a lock the others were also grabbing, and on
    /// <c>windows-latest</c> enough of them lost often enough to exhaust the 30 seconds and throw
    /// <c>SQLite Error 5: 'database is locked'</c> after 31.5 seconds.
    /// </para>
    /// <para>
    /// Setting the pragma moves the waiting into SQLite, which blocks on the lock and wakes when it
    /// is released instead of re-racing on a timer. Measured on
    /// <c>SqliteCostLedgerTests.RecordAsync_ConcurrentWritesSameBucket_AccumulateExactly</c>, five
    /// runs each with the build outside the timing: <b>2.77s median before, 1.03s after</b>.
    /// </para>
    /// <para>
    /// <b>WAL was tried here and deliberately rejected — do not add it back without measuring.</b>
    /// It is the obvious reach for SQLite write contention and it is the wrong one for this
    /// workload: WAL alone measured <b>3.30s</b> (worse than changing nothing), and combined with
    /// the busy timeout it measured <b>1.70s</b> — that is, it takes back most of what the timeout
    /// wins. Every store here opens a fresh unpooled connection per operation, so WAL's benefit —
    /// readers not blocking writers across a long-lived connection — never materialises, while its
    /// per-connection index setup cost is paid on every single call. It also converts the database
    /// file permanently, adds <c>-wal</c> and <c>-shm</c> sidecars, and does not work over a network
    /// filesystem.
    /// </para>
    /// <para>
    /// <b>What this does not fix.</b> Writes still serialise; SQLite permits one writer at a time by
    /// design. This makes contention wait properly instead of spinning, which is what moves it off
    /// the timeout. An application with genuinely concurrent write load wants a different store, not
    /// a different pragma.
    /// </para>
    /// </remarks>
    internal static SqliteConnection OpenConnection(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=30000;";
        _ = pragma.ExecuteNonQuery();

        return conn;
    }

    /// <summary>Reads <paramref name="key"/> from <c>rag_metadata</c>, or <c>null</c> when absent.</summary>
    internal static string? ReadRagMetadata(this SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM rag_metadata WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Inserts or replaces <paramref name="key"/> in <c>rag_metadata</c>.</summary>
    internal static void WriteRagMetadata(this SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO rag_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates the <c>rag_metadata</c> key/value table when it does not already exist.</summary>
    internal static void EnsureRagMetadataTable(this SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
