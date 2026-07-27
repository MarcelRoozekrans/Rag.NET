using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.PgVector;

public sealed partial class PgVectorStore : IVectorStore, ICollectionManageable, IDisposable
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

    /// <summary>
    /// Creates the <c>rag_chunks</c> table and its indexes if they do not already exist.
    /// Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chunk is keyed by <c>(document_id, chunk_index)</c>, enforced by a unique index, so
    /// <see cref="StoreAsync"/> replaces a chunk rather than duplicating it. If the table already
    /// contains duplicate keys (rows written before that index existed), this method
    /// <b>throws and changes nothing</b> — it never deletes rows to force the migration through.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The table already contains rows sharing a <c>(document_id, chunk_index)</c> pair, so the
    /// unique key cannot be created. The message carries the duplicate-key count and the query
    /// to inspect them.
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await EnableVectorExtensionAsync(conn, cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(conn, CreateTableSql("rag_chunks", _vectorDimensions), cancellationToken)
                .ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                conn,
                "CREATE INDEX IF NOT EXISTS idx_rag_chunks_document_id ON rag_chunks (document_id)",
                cancellationToken).ConfigureAwait(false);

            await EnsureChunkKeyIndexAsync(conn, "rag_chunks", "idx_rag_chunks_doc_chunk", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // The DO UPDATE SET list below is DELIBERATELY NOT a blanket "write every column".
    // Every column named there is destroyed by a dense re-store. In particular the sparse
    // vector column added by the sparse subtype MUST NOT appear: nulling a chunk's SPLADE
    // vector whenever its dense vector is re-stored is exactly the hazard
    // PineconeSparseVectorStore has to carry as an ORDERING CONTRACT ("calling StoreAsync
    // AFTER StoreSparseAsync silently drops the sparse vector"), and this store exists to
    // eliminate it rather than document it. Do not extend this SET list without deciding, in
    // writing, that a dense re-store SHOULD overwrite the new column.
    private const string StoreChunkSql = """
        INSERT INTO rag_chunks (document_id, chunk_index, text, metadata, embedding)
        VALUES ($1, $2, $3, $4, $5)
        ON CONFLICT (document_id, chunk_index) DO UPDATE SET
            -- Dense columns only. See the comment on StoreChunkSql before adding to this list.
            text = EXCLUDED.text,
            metadata = EXCLUDED.metadata,
            embedding = EXCLUDED.embedding
        """;

    /// <summary>
    /// Inserts the chunks, replacing any chunk already stored under the same
    /// <c>(document_id, chunk_index)</c> rather than duplicating it.
    /// </summary>
    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            foreach (var chunk in chunks)
            {
                var cmd = new NpgsqlCommand(StoreChunkSql, conn);
                await using (cmd.ConfigureAwait(false))
                {
                    cmd.Parameters.AddWithValue((string)chunk.Chunk.DocumentId);
                    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = chunk.Chunk.ChunkIndex });
                    cmd.Parameters.AddWithValue(chunk.Chunk.Text);
                    cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                        MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata));
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
                        MetadataSerializer.SerializeMetadata(options.MetadataFilter!));
                }

                var results = new List<SearchResult>();

                var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        results.Add(new SearchResult
                        {
                            Chunk = ReadChunk(reader),
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

    /// <summary>
    /// Creates a collection table with the same shape and indexes as <c>rag_chunks</c>:
    /// a unique key on <c>(document_id, chunk_index)</c> and a btree on <c>document_id</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a safe identifier, or is longer than
    /// <see cref="MaxCollectionNameLength"/> characters — see
    /// <see cref="ValidateCollectionNameLength"/> for why the shorter cap exists.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An existing table of that name already contains duplicate
    /// <c>(document_id, chunk_index)</c> pairs.
    /// </exception>
    public async Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        var quotedName = ValidateAndQuoteIdentifier(name);
        ValidateCollectionNameLength(name);

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await EnableVectorExtensionAsync(conn, cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(conn, CreateTableSql(quotedName, vectorDimensions), cancellationToken)
                .ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                conn,
                $"CREATE INDEX IF NOT EXISTS \"idx_{name}_document_id\" ON {quotedName} (document_id)",
                cancellationToken).ConfigureAwait(false);

            await EnsureChunkKeyIndexAsync(conn, quotedName, $"idx_{name}_doc_chunk", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>PostgreSQL truncates identifiers at 63 bytes (NAMEDATALEN - 1).</summary>
    private const int MaxIdentifierLength = 63;

    /// <summary>
    /// Longest decoration this store applies when deriving an index name from a collection
    /// name: <c>idx_</c> + <c>_document_id</c>.
    /// </summary>
    private const int IndexNameOverhead = 16;

    /// <summary>
    /// Longest collection name whose derived index names still fit in
    /// <see cref="MaxIdentifierLength"/> bytes.
    /// </summary>
    private const int MaxCollectionNameLength = MaxIdentifierLength - IndexNameOverhead;

    /// <summary>
    /// Rejects collection names too long to derive index names from.
    /// </summary>
    /// <remarks>
    /// <see cref="ValidateAndQuoteIdentifier"/> allows the full 63 characters PostgreSQL permits
    /// for a table name, but this store derives index names by decorating it
    /// (<c>idx_{name}_doc_chunk</c> and friends), and PostgreSQL <i>silently truncates</i> an
    /// over-long identifier instead of failing. Two collections whose names differ only past the
    /// truncation point would therefore share one index name, and the second
    /// <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> would be a no-op — leaving that collection with
    /// no <c>(document_id, chunk_index)</c> key and silently back in the duplicate-row bug. A
    /// loud rejection at creation time is the safe reading of that.
    /// </remarks>
    private static void ValidateCollectionNameLength(string name)
    {
        if (name.Length > MaxCollectionNameLength)
            throw new ArgumentException(
                $"Collection name '{name}' is {name.Length} characters; the maximum is {MaxCollectionNameLength}. " +
                $"Index names are derived from it (for example 'idx_{name}_document_id'), and PostgreSQL silently " +
                $"truncates identifiers at {MaxIdentifierLength} bytes, which would let two collections collide on " +
                "one index name and leave one of them without its unique (document_id, chunk_index) key.",
                nameof(name));
    }

    private static string CreateTableSql(string tableSql, int vectorDimensions) => $$"""
        CREATE TABLE IF NOT EXISTS {{tableSql}} (
            id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            document_id TEXT NOT NULL,
            chunk_index INTEGER NOT NULL,
            text TEXT NOT NULL,
            metadata JSONB NOT NULL DEFAULT '{}',
            embedding vector({{vectorDimensions}}) NOT NULL
        )
        """;

    private static string DuplicateChunkKeyQuery(string tableSql) => $"""
        SELECT count(*) FROM (
            SELECT document_id, chunk_index FROM {tableSql}
            GROUP BY document_id, chunk_index HAVING count(*) > 1
        ) d
        """;

    private static async Task EnableVectorExtensionAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(conn, "CREATE EXTENSION IF NOT EXISTS vector", cancellationToken)
            .ConfigureAwait(false);
        await conn.ReloadTypesAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        await using (cmd.ConfigureAwait(false))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the unique <c>(document_id, chunk_index)</c> index, failing fast — and without
    /// touching a single row — when the table already holds duplicate keys.
    /// </summary>
    /// <remarks>
    /// The probe is skipped once the index exists, so the aggregate scan is paid only on the
    /// migrating run rather than on every startup. Rows are never deleted to force the migration
    /// through: that would be silent data loss on a path the caller did not ask to migrate.
    /// </remarks>
    private static async Task EnsureChunkKeyIndexAsync(
        NpgsqlConnection conn,
        string tableSql,
        string indexName,
        CancellationToken cancellationToken)
    {
        if (!await IndexExistsAsync(conn, indexName, cancellationToken).ConfigureAwait(false))
        {
            var duplicateKeys = await CountDuplicateChunkKeysAsync(conn, tableSql, cancellationToken)
                .ConfigureAwait(false);

            if (duplicateKeys > 0)
                throw new InvalidOperationException(
                    $"Cannot key {tableSql} by (document_id, chunk_index): the table already contains " +
                    $"{duplicateKeys} duplicate key(s) — rows written before this store enforced the key. " +
                    "No rows were deleted; the database is unchanged. Inspect them with:\n\n" +
                    DuplicateChunkKeyQuery(tableSql) + "\n\n" +
                    "Decide which row of each pair to keep, remove the rest, then initialize again.");
        }

        await ExecuteNonQueryAsync(
            conn,
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{indexName}\" ON {tableSql} (document_id, chunk_index)",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IndexExistsAsync(
        NpgsqlConnection conn,
        string indexName,
        CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", conn);
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Parameters.AddWithValue(indexName);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is true;
        }
    }

    private static async Task<long> CountDuplicateChunkKeysAsync(
        NpgsqlConnection conn,
        string tableSql,
        CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand(DuplicateChunkKeyQuery(tableSql), conn);
        await using (cmd.ConfigureAwait(false))
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is long count ? count : 0L;
        }
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var quotedName = ValidateAndQuoteIdentifier(name);
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {quotedName}", conn);
            await using (cmd.ConfigureAwait(false))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[a-z_][a-z0-9_]{0,62}$",
        System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex SafeIdentifierRegex();

    /// <summary>
    /// Validates that <paramref name="name"/> is a safe PostgreSQL identifier
    /// (lowercase letters, digits, underscores; max 63 chars; must start with letter or underscore)
    /// and returns it double-quoted for safe use in DDL statements.
    /// </summary>
    private static string ValidateAndQuoteIdentifier(string name)
    {
        if (!SafeIdentifierRegex().IsMatch(name))
            throw new ArgumentException(
                $"Collection name '{name}' is invalid. Use only lowercase letters, digits, and underscores " +
                "(max 63 chars, must start with a letter or underscore).",
                nameof(name));
        return $"\"{name}\"";
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

    private static TextChunk ReadChunk(Npgsql.NpgsqlDataReader reader)
    {
        var metadataResult = MetadataSerializer.DeserializeMetadata(reader.GetString(3));
        var metadata = metadataResult.IsSuccess
            ? metadataResult.Value
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new TextChunk
        {
            DocumentId = new DocumentId(reader.GetString(0)),
            ChunkIndex = reader.GetInt32(1),
            Text = reader.GetString(2),
            Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
        };
    }

    public void Dispose() => _dataSource.Dispose();
}
