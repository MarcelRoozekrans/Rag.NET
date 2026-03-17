using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;

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

    public Task<string?> GetETagAsync(string providerId, string entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT etag FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task<string?> GetHashAsync(string providerId, string entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task SetAsync(string providerId, string entryId, string? etag, string hash, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO content_hashes (provider_id, entry_id, etag, hash, updated_at)
            VALUES ($pid, $eid, $etag, $hash, $now)
            """;
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetAllIdsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_id FROM content_hashes WHERE provider_id = $pid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return Task.FromResult<IReadOnlySet<string>>(ids);
    }

    public Task RemoveAsync(string providerId, string entryId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM content_hashes WHERE provider_id = $pid AND entry_id = $eid";
        cmd.Parameters.AddWithValue("$pid", providerId);
        cmd.Parameters.AddWithValue("$eid", entryId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
