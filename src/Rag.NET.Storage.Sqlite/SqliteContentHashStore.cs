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
                PRIMARY KEY (provider_id, entry_id)
            );
            """;
        cmd.ExecuteNonQuery();
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
