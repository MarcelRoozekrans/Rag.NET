using Microsoft.Data.Sqlite;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

/// <summary>
/// Every store in this assembly opens its database with SQLite's busy timeout set, and without WAL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/640">#640</see>.
/// SQLite's <c>busy_timeout</c> defaults to 0, so a connection that cannot take the lock gets
/// <c>SQLITE_BUSY</c> immediately instead of waiting. Twenty concurrent writers therefore spun in
/// <c>Microsoft.Data.Sqlite</c>'s command-level retry loop rather than queueing, and on
/// <c>windows-latest</c> exhausted its 30 seconds and threw after 31.5.
/// </para>
/// <para>
/// <b>A pragma is exactly the kind of fix that gets reverted by a tidy-up.</b> It is one line in a
/// helper, no caller visibly depends on it, and removing it breaks nothing any other test asserts —
/// the concurrency test that found the defect still passes on Linux either way. So the setting is
/// asserted directly rather than trusted to the symptom reappearing.
/// </para>
/// </remarks>
public sealed class SqliteBusyTimeoutTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ragnet-busytimeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The helper sets SQLite's own busy timeout, not just the provider's.</summary>
    /// <remarks>
    /// The distinction is the whole fix. <c>SqliteCommand.DefaultTimeout</c> is 30 seconds by
    /// default and retries in a loop above SQLite; this pragma makes SQLite block on the lock and
    /// wake when it is released. Both are 30 seconds, which is why the failure looked like a
    /// timeout working correctly.
    /// </remarks>
    [Fact]
    public void OpenConnection_SetsSqlitesOwnBusyTimeout()
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);

        Assert.Equal("30000", ReadPragma(conn, "busy_timeout"), StringComparer.Ordinal);
    }

    /// <summary>The default this fix overrides is 0 — asserted so the fix's premise stays checkable.</summary>
    /// <remarks>
    /// Without this, a future <c>Microsoft.Data.Sqlite</c> that began setting a non-zero
    /// <c>busy_timeout</c> itself would make the pragma above redundant with nothing to say so.
    /// This fails when that day comes, which is the moment to re-measure rather than to keep
    /// carrying a line whose reason has expired.
    /// </remarks>
    [Fact]
    public void WithoutTheHelper_SqliteBusyTimeoutIsZero()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();

        Assert.Equal("0", ReadPragma(conn, "busy_timeout"), StringComparer.Ordinal);
    }

    /// <summary>WAL is deliberately NOT enabled, and that is the measured choice.</summary>
    /// <remarks>
    /// <para>
    /// WAL is the obvious reach for SQLite write contention, so this asserts its absence rather
    /// than leaving a comment to be overruled by the next person who reaches for it. On this
    /// workload WAL alone measured 3.30s against 2.77s for changing nothing, and 1.70s alongside
    /// the busy timeout against 1.03s for the timeout alone — it costs more than it returns,
    /// because every store here opens a fresh unpooled connection per operation and so pays WAL's
    /// setup without ever holding a connection long enough to collect its benefit.
    /// </para>
    /// <para>
    /// It is also not free to undo: WAL is a persistent property of the file, so enabling it
    /// converts every existing user database and leaves sidecars beside it.
    /// </para>
    /// </remarks>
    [Fact]
    public void OpenConnection_DoesNotEnableWal()
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);

        Assert.Equal("delete", ReadPragma(conn, "journal_mode"), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads one pragma's current value.</summary>
    /// <param name="conn">An open connection.</param>
    /// <param name="name">The pragma name.</param>
    /// <returns>The value as SQLite reports it.</returns>
    private static string ReadPragma(SqliteConnection conn, string name)
    {
        using var command = conn.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }
}
