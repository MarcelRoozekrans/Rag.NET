using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Search;

namespace Rag.NET.Storage;

/// <summary>
/// Write-through SQLite-backed BM25 index. Wraps <see cref="InMemoryBm25Index"/>.
/// Lazy-initialises on first use: creates tables, applies stale guard, loads persisted data.
/// </summary>
public sealed class SqliteBm25Index : IBm25Index
{
    private readonly InMemoryBm25Index _memory = new();
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;
    private bool _disposed;

    public SqliteBm25Index(string dbPath, string? collectionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
    }

    public void Add(int docId, TextChunk chunk)
    {
        EnsureInitialised();
        _memory.Add(docId, chunk);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO bm25_docs
                (doc_id, document_id, chunk_index, start_position, end_position, chunk_text, metadata_json, token_length)
            VALUES
                ($docId, $documentId, $chunkIndex, $startPos, $endPos, $text, $meta, $len)
            """;
        cmd.Parameters.AddWithValue("$docId", docId);
        cmd.Parameters.AddWithValue("$documentId", chunk.DocumentId);
        cmd.Parameters.AddWithValue("$chunkIndex", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("$startPos", chunk.StartPosition);
        cmd.Parameters.AddWithValue("$endPos", chunk.EndPosition);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(chunk.Metadata));
        cmd.Parameters.AddWithValue("$len", Tokenize(chunk.Text).Count);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string documentId)
    {
        EnsureInitialised();
        _memory.Remove(documentId);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bm25_docs WHERE document_id = $docId";
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
    {
        EnsureInitialised();
        return _memory.Search(query, topK);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqliteConnection.ClearAllPools();
        _memory.Dispose();
        _initLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
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
            {
                ClearData(conn);
            }
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
            CREATE TABLE IF NOT EXISTS bm25_docs (
                doc_id         INTEGER NOT NULL PRIMARY KEY,
                document_id    TEXT NOT NULL,
                chunk_index    INTEGER NOT NULL,
                start_position INTEGER NOT NULL DEFAULT 0,
                end_position   INTEGER NOT NULL DEFAULT 0,
                chunk_text     TEXT NOT NULL,
                metadata_json  TEXT NOT NULL DEFAULT '{}',
                token_length   INTEGER NOT NULL
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
        cmd.CommandText = "DELETE FROM bm25_docs; DELETE FROM rag_metadata;";
        cmd.ExecuteNonQuery();
    }

    private void LoadIntoMemory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT doc_id, document_id, chunk_index, start_position, end_position, chunk_text, metadata_json FROM bm25_docs";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(int docId, TextChunk chunk)>();
        while (reader.Read())
        {
            var docId = reader.GetInt32(0);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6))
                           ?? new Dictionary<string, string>(StringComparer.Ordinal);

            var chunk = new TextChunk
            {
                DocumentId = reader.GetString(1),
                ChunkIndex = reader.GetInt32(2),
                StartPosition = reader.GetInt32(3),
                EndPosition = reader.GetInt32(4),
                Text = reader.GetString(5),
                Metadata = metadata,
            };
            rows.Add((docId, chunk));
        }

        foreach (ref readonly var row in CollectionsMarshal.AsSpan(rows))
            _memory.Add(row.docId, row.chunk);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    // Minimal tokenizer matching InMemoryBm25Index.Tokenize for token_length calculation
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var lower = text.ToLowerInvariant();
        var start = -1;
        for (var i = 0; i <= lower.Length; i++)
        {
            var isAlnum = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (isAlnum && start == -1) start = i;
            else if (!isAlnum && start != -1)
            {
                tokens.Add(lower[start..i]);
                start = -1;
            }
        }
        return tokens;
    }
}
