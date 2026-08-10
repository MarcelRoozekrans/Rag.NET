using System.Runtime.InteropServices;
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
    private readonly InMemoryBm25Index _memory;
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialised;
    private bool _disposed;

    public SqliteBm25Index(string dbPath, string? collectionName = null, SynonymMap? synonymMap = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
        _memory = new InMemoryBm25Index(synonymMap);
    }

    public void Add(int docId, TextChunk chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        _memory.Add(docId, chunk);
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO bm25_docs
                (doc_id, document_id, chunk_index, start_position, end_position, chunk_text, metadata_json)
            VALUES
                ($docId, $documentId, $chunkIndex, $startPos, $endPos, $text, $meta)
            """;
        cmd.Parameters.AddWithValue("$docId", docId);
        cmd.Parameters.AddWithValue("$documentId", (string)chunk.DocumentId);
        cmd.Parameters.AddWithValue("$chunkIndex", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("$startPos", chunk.StartPosition);
        cmd.Parameters.AddWithValue("$endPos", chunk.EndPosition);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        cmd.Parameters.AddWithValue("$meta", MetadataSerializer.SerializeMetadata(chunk.Metadata));
        cmd.ExecuteNonQuery();
    }

    public void Remove(string documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        _memory.Remove(documentId);
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bm25_docs WHERE document_id = $docId";
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        return _memory.Search(query, topK);
    }

    /// <summary>
    /// Explicitly initialises the SQLite backing store. Call this during application startup
    /// (e.g. from a hosted service or DI setup) to avoid blocking thread-pool threads
    /// on the first <see cref="Add"/> or <see cref="Search"/> call.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialised) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _initLock.Wait(cancellationToken);
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
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        await _memory.ClearAsync(cancellationToken).ConfigureAwait(false);
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bm25_docs";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
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
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        CreateSchema(conn);

        if (_collectionName is not null)
        {
            var storedName = SqliteStoreHelper.ReadMetadata(conn, "bm25_collection_name");
            if (storedName is not null && !string.Equals(storedName, _collectionName, StringComparison.Ordinal))
            {
                ClearData(conn);
            }
            SqliteStoreHelper.WriteMetadata(conn, "bm25_collection_name", _collectionName);
        }

        LoadIntoMemory(conn);
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        SqliteStoreHelper.EnsureMetadataTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS bm25_docs (
                doc_id         INTEGER NOT NULL PRIMARY KEY,
                document_id    TEXT NOT NULL,
                chunk_index    INTEGER NOT NULL,
                start_position INTEGER NOT NULL DEFAULT 0,
                end_position   INTEGER NOT NULL DEFAULT 0,
                chunk_text     TEXT NOT NULL,
                metadata_json  TEXT NOT NULL DEFAULT '{}'
            );

            -- Remove() deletes by document_id, and StorageBehavior calls it before every ingest
            -- (including first-time ingests, which match nothing). Without this index that is a
            -- full table scan per ingested document.
            CREATE INDEX IF NOT EXISTS ix_bm25_docs_document_id ON bm25_docs(document_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ClearData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bm25_docs; DELETE FROM rag_metadata WHERE key = 'bm25_collection_name';";
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
            var metadataResult = MetadataSerializer.DeserializeMetadata(reader.GetString(6));
            var metadata = metadataResult.IsSuccess
                           ? metadataResult.Value
                           : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

            var chunk = new TextChunk
            {
                DocumentId = new DocumentId(reader.GetString(1)),
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
}
