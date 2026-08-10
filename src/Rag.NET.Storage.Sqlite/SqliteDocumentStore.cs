using System.Globalization;
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Storage;

/// <summary>
/// SQLite-backed sidecar tracking document metadata and chunks ingested via <c>DocumentIngestor</c>.
/// Lazy-initialises on first use: creates tables, applies stale guard — matching <see cref="SqliteBm25Index"/> patterns.
/// </summary>
public sealed class SqliteDocumentStore : IRagDataManager
{
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialised;
    private bool _disposed;

    public SqliteDocumentStore(string dbPath, string? collectionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
    }

    public void Add(DocumentMetadata metadata, IReadOnlyList<TextChunk> chunks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var tx = conn.BeginTransaction();

        using var docCmd = conn.CreateCommand();
        docCmd.CommandText = """
            INSERT OR REPLACE INTO rag_documents
                (doc_id, file_name, content_type, tags_json, ingested_at, chunk_count)
            VALUES
                ($docId, $fileName, $contentType, $tagsJson, $ingestedAt, $chunkCount)
            """;
        docCmd.Parameters.AddWithValue("$docId",       (string)metadata.DocumentId);
        docCmd.Parameters.AddWithValue("$fileName",    metadata.FileName);
        docCmd.Parameters.AddWithValue("$contentType", (object?)metadata.ContentType ?? DBNull.Value);
        docCmd.Parameters.AddWithValue("$tagsJson",    MetadataSerializer.SerializeTags(metadata.Tags));
        docCmd.Parameters.AddWithValue("$ingestedAt",  now);
        docCmd.Parameters.AddWithValue("$chunkCount",  chunks.Count);
        docCmd.ExecuteNonQuery();

        using var chunkCmd = conn.CreateCommand();
        chunkCmd.CommandText = """
            INSERT OR REPLACE INTO rag_chunks
                (doc_id, chunk_index, start_pos, end_pos, text, metadata_json)
            VALUES
                ($docId, $chunkIdx, $startPos, $endPos, $text, $meta)
            """;
        var pChunkDocId   = chunkCmd.Parameters.Add("$docId",    SqliteType.Text);
        var pChunkIdx     = chunkCmd.Parameters.Add("$chunkIdx", SqliteType.Integer);
        var pStartPos     = chunkCmd.Parameters.Add("$startPos", SqliteType.Integer);
        var pEndPos       = chunkCmd.Parameters.Add("$endPos",   SqliteType.Integer);
        var pText         = chunkCmd.Parameters.Add("$text",     SqliteType.Text);
        var pMeta         = chunkCmd.Parameters.Add("$meta",     SqliteType.Text);
        foreach (var chunk in chunks)
        {
            pChunkDocId.Value = (string)chunk.DocumentId;
            pChunkIdx.Value   = chunk.ChunkIndex;
            pStartPos.Value   = chunk.StartPosition;
            pEndPos.Value     = chunk.EndPosition;
            pText.Value       = chunk.Text;
            pMeta.Value       = MetadataSerializer.SerializeMetadata(chunk.Metadata);
            chunkCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Remove(string documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM rag_documents WHERE doc_id = $docId;
            DELETE FROM rag_chunks     WHERE doc_id = $docId;
            """;
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public Task<IReadOnlyList<DocumentSummary>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText =
                "SELECT doc_id, file_name, content_type, tags_json, ingested_at, chunk_count " +
                "FROM rag_documents";
            using var reader = cmd.ExecuteReader();
            var results = new List<DocumentSummary>();
            while (reader.Read())
            {
                var tagsResult = MetadataSerializer.DeserializeTags(reader.GetString(3));
                var tags = tagsResult.IsSuccess
                           ? tagsResult.Value
                           : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
                results.Add(new DocumentSummary
                {
                    DocumentId  = new DocumentId(reader.GetString(0)),
                    FileName    = reader.GetString(1),
                    ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Tags        = tags,
                    IngestedAt  = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    ChunkCount  = reader.GetInt32(5),
                });
            }
            return (IReadOnlyList<DocumentSummary>)results;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TextChunk>> GetChunksAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                SELECT chunk_index, start_pos, end_pos, text, metadata_json
                FROM rag_chunks
                WHERE doc_id = $docId
                ORDER BY chunk_index
                """;
            cmd.Parameters.AddWithValue("$docId", documentId);
            using var reader = cmd.ExecuteReader();
            var results = new List<TextChunk>();
            while (reader.Read())
            {
                var metadataResult = MetadataSerializer.DeserializeMetadata(reader.GetString(4));
                var metadata = metadataResult.IsSuccess
                               ? metadataResult.Value
                               : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
                results.Add(new TextChunk
                {
                    DocumentId    = new DocumentId(documentId),
                    ChunkIndex    = reader.GetInt32(0),
                    StartPosition = reader.GetInt32(1),
                    EndPosition   = reader.GetInt32(2),
                    Text          = reader.GetString(3),
                    Metadata      = metadata,
                });
            }
            return (IReadOnlyList<TextChunk>)results;
        }, cancellationToken);
    }

    public Task<DataManagerStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(chunk_count), 0) FROM rag_documents";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new DataManagerStats
            {
                DocumentCount   = reader.GetInt32(0),
                TotalChunkCount = reader.GetInt32(1),
            };
        }, cancellationToken);
    }

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

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();

        return Task.Run(() =>
        {
            using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
            using var tx   = conn.BeginTransaction();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rag_documents; DELETE FROM rag_chunks;";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }, cancellationToken);
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
        using var conn = SqliteStoreHelper.OpenConnection(_dbPath);
        CreateSchema(conn);

        if (_collectionName is not null)
        {
            var storedName = SqliteStoreHelper.ReadMetadata(conn, "doc_store_collection_name");
            if (storedName is not null &&
                !string.Equals(storedName, _collectionName, StringComparison.Ordinal))
            {
                ClearData(conn);
            }
            SqliteStoreHelper.WriteMetadata(conn, "doc_store_collection_name", _collectionName);
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        SqliteStoreHelper.EnsureMetadataTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_documents (
                doc_id       TEXT    NOT NULL PRIMARY KEY,
                file_name    TEXT    NOT NULL,
                content_type TEXT,
                tags_json    TEXT    NOT NULL DEFAULT '{}',
                ingested_at  TEXT    NOT NULL,
                chunk_count  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rag_chunks (
                doc_id        TEXT    NOT NULL,
                chunk_index   INTEGER NOT NULL,
                start_pos     INTEGER NOT NULL DEFAULT 0,
                end_pos       INTEGER NOT NULL DEFAULT 0,
                text          TEXT    NOT NULL,
                metadata_json TEXT    NOT NULL DEFAULT '{}',
                PRIMARY KEY (doc_id, chunk_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ClearData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM rag_documents;
            DELETE FROM rag_chunks;
            DELETE FROM rag_metadata WHERE key = 'doc_store_collection_name';
            """;
        cmd.ExecuteNonQuery();
    }
}
