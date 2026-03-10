using System.Text.Json;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.PgVector;

public sealed class PgVectorStore : IVectorStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _vectorDimensions;

    public PgVectorStore(string connectionString, int vectorDimensions = 1536)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
        _vectorDimensions = vectorDimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var enableExt = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn);
            await using (enableExt.ConfigureAwait(false))
            {
                await enableExt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var createTableSql = $$"""
                CREATE TABLE IF NOT EXISTS rag_chunks (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    metadata JSONB NOT NULL DEFAULT '{}',
                    embedding vector({{_vectorDimensions}}) NOT NULL
                )
                """;

            var createTable = new NpgsqlCommand(createTableSql, conn);
            await using (createTable.ConfigureAwait(false))
            {
                await createTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var createIndex = new NpgsqlCommand(
                "CREATE INDEX IF NOT EXISTS idx_rag_chunks_document_id ON rag_chunks (document_id)", conn);
            await using (createIndex.ConfigureAwait(false))
            {
                await createIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            foreach (var chunk in chunks)
            {
                var cmd = new NpgsqlCommand(
                    """
                    INSERT INTO rag_chunks (document_id, chunk_index, text, metadata, embedding)
                    VALUES ($1, $2, $3, $4, $5)
                    """, conn);
                await using (cmd.ConfigureAwait(false))
                {
                    cmd.Parameters.AddWithValue(chunk.Chunk.DocumentId);
                    cmd.Parameters.AddWithValue(chunk.Chunk.ChunkIndex);
                    cmd.Parameters.AddWithValue(chunk.Chunk.Text);
                    cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                        JsonSerializer.Serialize(chunk.Chunk.Metadata));
                    cmd.Parameters.AddWithValue(new Vector(chunk.Embedding.ToArray()));

                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var cmd = new NpgsqlCommand(
                """
                SELECT document_id, chunk_index, text, metadata,
                       1 - (embedding <=> $1) AS score
                FROM rag_chunks
                WHERE 1 - (embedding <=> $1) >= $2
                ORDER BY embedding <=> $1
                LIMIT $3
                """, conn);
            await using (cmd.ConfigureAwait(false))
            {
                cmd.Parameters.AddWithValue(new Vector(queryEmbedding.ToArray()));
                cmd.Parameters.AddWithValue(options.MinScore);
                cmd.Parameters.AddWithValue(options.TopK);

                var results = new List<SearchResult>();

                var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            reader.GetString(3)) ?? [];

                        results.Add(new SearchResult
                        {
                            Chunk = new TextChunk
                            {
                                DocumentId = reader.GetString(0),
                                ChunkIndex = reader.GetInt32(1),
                                Text = reader.GetString(2),
                                Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                            },
                            Score = reader.GetDouble(4),
                        });
                    }
                }

                return results;
            }
        }
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var cmd = new NpgsqlCommand(
                "DELETE FROM rag_chunks WHERE document_id = $1", conn);
            await using (cmd.ConfigureAwait(false))
            {
                cmd.Parameters.AddWithValue(documentId);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _dataSource.Dispose();
}
