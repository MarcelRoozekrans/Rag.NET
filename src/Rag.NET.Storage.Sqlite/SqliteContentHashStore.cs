using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IContentHashStore"/>.
/// Stores ETag + SHA-256 hash per (providerId, entryId) pair in a <c>content_hashes</c> table.
/// </summary>
public sealed class SqliteContentHashStore : IContentHashStore
{
    private readonly string _dbPath;

    public SqliteContentHashStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        using var conn = SqliteStoreHelper.OpenConnection(dbPath);
        EnsureTable(conn);
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS content_hashes (
                provider_id TEXT NOT NULL,
                entry_id    TEXT NOT NULL,
                etag        TEXT,
                hash        TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                checked_at  TEXT,
                PRIMARY KEY (provider_id, entry_id)
            );
            """;
        cmd.ExecuteNonQuery();

        AddCheckedAtIfMissing(conn);
    }

    /// <summary>
    /// Adds <c>checked_at</c> to a table created before it existed.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> above is a no-op on an existing database, so a store
    /// written by an earlier version keeps its old shape and every <c>TouchAsync</c> would fail on
    /// a missing column. The column is nullable and carries no default: a row that predates this
    /// has genuinely never been observed being checked, and writing an invented timestamp would
    /// claim otherwise.
    /// </remarks>
    private static void AddCheckedAtIfMissing(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('content_hashes') WHERE name = 'checked_at'";
        if (Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE content_hashes ADD COLUMN checked_at TEXT";
        alter.ExecuteNonQuery();
    }

    public Task<string?> GetETagAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT etag FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        cmd.Parameters.AddWithValue("$eid", entryId.Value);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task<string?> GetHashAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        cmd.Parameters.AddWithValue("$eid", entryId.Value);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task SetAsync(ProviderId providerId, EntryId entryId, string? etag, string hash, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO content_hashes (provider_id, entry_id, etag, hash, updated_at)
            VALUES ($pid, $eid, $etag, $hash, $now)
            """;
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        cmd.Parameters.AddWithValue("$eid", entryId.Value);
        cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<EntryId>> GetAllIdsAsync(ProviderId providerId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_id FROM content_hashes WHERE provider_id = $pid";
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        var ids = new HashSet<EntryId>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(new EntryId(reader.GetString(0)));
        return Task.FromResult<IReadOnlySet<EntryId>>(ids);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Updates <c>checked_at</c> only, leaving <c>updated_at</c>, the hash and the ETag alone —
    /// the row's content did not change, and saying otherwise is the confusion this method exists
    /// to remove. An entry the store has never seen is not inserted: a check of something that was
    /// never ingested records nothing, and <c>UPDATE</c> matching no row is the correct no-op.
    /// </remarks>
    public Task TouchAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE content_hashes SET checked_at = $now
            WHERE provider_id = $pid AND entry_id = $eid
            """;
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        cmd.Parameters.AddWithValue("$eid", entryId.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>When an entry was last examined and found unchanged, or null if never.</summary>
    /// <remarks>
    /// Not on <see cref="IContentHashStore"/>: the pipeline writes this value and never reads it,
    /// so putting a reader on the interface would oblige every implementation to support a query
    /// nothing in the library makes. Callers who want the value use this store directly.
    /// </remarks>
    public Task<DateTimeOffset?> GetCheckedAtAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT checked_at FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        cmd.Parameters.AddWithValue("$eid", entryId.Value);

        return Task.FromResult(cmd.ExecuteScalar() is string raw
            ? DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
            : (DateTimeOffset?)null);
    }

    public Task RemoveAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId.Value);
        cmd.Parameters.AddWithValue("$eid", entryId.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
