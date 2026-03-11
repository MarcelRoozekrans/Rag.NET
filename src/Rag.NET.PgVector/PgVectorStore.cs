using System.Text.Json;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.PgVector;

public sealed class PgVectorStore : IVectorStore, ICollectionManageable, IDisposable
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

            await conn.ReloadTypesAsync().ConfigureAwait(false);

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
                    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = chunk.Chunk.ChunkIndex });
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
            var hasFilter = options.MetadataFilter is { Count: > 0 };

            var sql = """
                SELECT document_id, chunk_index, text, metadata,
                       1 - (embedding <=> $1) AS score
                FROM rag_chunks
                WHERE 1 - (embedding <=> $1) >= $2
                """;

            if (hasFilter)
            {
                sql += "\n  AND metadata @> $4::jsonb";
            }

            sql += "\nORDER BY embedding <=> $1\nLIMIT $3";

            var cmd = new NpgsqlCommand(sql, conn);
            await using (cmd.ConfigureAwait(false))
            {
                cmd.Parameters.AddWithValue(new Vector(queryEmbedding.ToArray()));
                cmd.Parameters.AddWithValue(options.MinScore);
                cmd.Parameters.AddWithValue(options.TopK);

                if (hasFilter)
                {
                    cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                        JsonSerializer.Serialize(options.MetadataFilter));
                }

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

    public async Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var enableExt = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn);
            await using (enableExt.ConfigureAwait(false))
            {
                await enableExt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await conn.ReloadTypesAsync().ConfigureAwait(false);

            var sql = $$"""
                CREATE TABLE IF NOT EXISTS {{name}} (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    metadata JSONB NOT NULL DEFAULT '{}',
                    embedding vector({{vectorDimensions}}) NOT NULL
                )
                """;
            var cmd = new NpgsqlCommand(sql, conn);
            await using (cmd.ConfigureAwait(false))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var indexCmd = new NpgsqlCommand($"CREATE INDEX IF NOT EXISTS idx_{name}_document_id ON {name} (document_id)", conn);
            await using (indexCmd.ConfigureAwait(false))
            {
                await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {name}", conn);
            await using (cmd.ConfigureAwait(false))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = $1)", conn);
            await using (cmd.ConfigureAwait(false))
            {
                cmd.Parameters.AddWithValue(name);
                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return result is true;
            }
        }
    }

    public void Dispose() => _dataSource.Dispose();
}
