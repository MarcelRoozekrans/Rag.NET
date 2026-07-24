using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;

namespace Rag.NET.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IEmbeddingVersionStore"/>.
/// Stores one embedding version stamp per document in an <c>embedding_versions</c> table.
/// </summary>
public sealed class SqliteEmbeddingVersionStore : IEmbeddingVersionStore
{
    private readonly string _dbPath;

    public SqliteEmbeddingVersionStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS embedding_versions (
                doc_id      TEXT PRIMARY KEY,
                model_id    TEXT NOT NULL,
                dimension   INTEGER NOT NULL,
                embedded_at TEXT NOT NULL
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

    public Task SetAsync(string documentId, string modelId, int dimension, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO embedding_versions (doc_id, model_id, dimension, embedded_at)
            VALUES ($doc, $model, $dim, $now)
            """;
        cmd.Parameters.AddWithValue("$doc", documentId);
        cmd.Parameters.AddWithValue("$model", modelId);
        cmd.Parameters.AddWithValue("$dim", dimension);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string DocumentId, string ModelId, int Dimension)>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT doc_id, model_id, dimension FROM embedding_versions";
        var rows = new List<(string DocumentId, string ModelId, int Dimension)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return Task.FromResult<IReadOnlyList<(string DocumentId, string ModelId, int Dimension)>>(rows);
    }

    public Task RemoveAsync(string documentId, CancellationToken cancellationToken = default)
    {
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM embedding_versions WHERE doc_id = $doc";
        cmd.Parameters.AddWithValue("$doc", documentId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
