using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;

namespace Rag.NET.Storage;

/// <summary>
/// Write-through SQLite-backed parent chunk store. Wraps <see cref="InMemoryParentChunkStore"/>.
/// Uses the same <c>rag_metadata</c> and collection-name stale guard as <see cref="SqliteBm25Index"/>.
/// The two stores can share a database file.
/// </summary>
public sealed class SqliteParentChunkStore : IParentChunkStore
{
    private readonly InMemoryParentChunkStore _memory = new();
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialised;
    private bool _disposed;

    public SqliteParentChunkStore(string dbPath, string? collectionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
    }

    public void Add(string documentId, int parentChunkIndex, string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        _memory.Add(documentId, parentChunkIndex, text);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO parent_chunks (document_id, parent_chunk_index, text)
            VALUES ($docId, $idx, $text)
            """;
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.Parameters.AddWithValue("$idx", parentChunkIndex);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.ExecuteNonQuery();
    }

    public bool TryGet(string documentId, int parentChunkIndex, out string? text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        return _memory.TryGet(documentId, parentChunkIndex, out text);
    }

    public void Remove(string documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        _memory.Remove(documentId);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM parent_chunks WHERE document_id = $docId";
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureInitialised()
    {
        if (_initialised) return;
        _initLock.Wait();
        try
        {
            if (_initialised) return;
            InitialiseCore();
            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void InitialiseCore()
    {
        using var conn = OpenConnection();
        CreateSchema(conn);

        if (_collectionName is not null)
        {
            var storedName = ReadMetadata(conn, "collection_name");
            if (storedName is not null && !string.Equals(storedName, _collectionName, StringComparison.Ordinal))
                ClearData(conn);
            WriteMetadata(conn, "collection_name", _collectionName);
        }

        LoadIntoMemory(conn);
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS parent_chunks (
                document_id        TEXT NOT NULL,
                parent_chunk_index INTEGER NOT NULL,
                text               TEXT NOT NULL,
                PRIMARY KEY (document_id, parent_chunk_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMetadata(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM rag_metadata WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMetadata(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO rag_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static void ClearData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM parent_chunks; DELETE FROM rag_metadata;";
        cmd.ExecuteNonQuery();
    }

    private void LoadIntoMemory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT document_id, parent_chunk_index, text FROM parent_chunks";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            _memory.Add(reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        return conn;
    }
}
