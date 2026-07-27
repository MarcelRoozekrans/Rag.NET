using Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rag.NET.PgVector.Tests;

public class PgVectorStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .Build();

    private PgVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _sut = new PgVectorStore(_postgres.GetConnectionString(), vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("cats are great", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = new DocumentId("doc-to-delete"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
                },
                Embedding = new float[] { 0.9f, 0.1f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("engineering doc", results[0].Chunk.Text);
    }

    [Fact]
    public async Task Search_RespectsMinScore()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "close match", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "far match", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 0.0f, 1.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10, MinScore = 0.9 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("close match", results[0].Chunk.Text);
    }

    [Fact]
    public async Task StoreAsync_SameChunkTwice_ReplacesInsteadOfDuplicating()
    {
        // Isolated from the other facts on this shared table by a unique metadata marker.
        var marker = new Dictionary<string, string>(StringComparer.Ordinal) { ["upsert_probe"] = "a" };

        await _sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "original text",
                        DocumentId = new DocumentId("doc-upsert"),
                        ChunkIndex = 0,
                        Metadata = marker,
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        await _sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "replacement text",
                        DocumentId = new DocumentId("doc-upsert"),
                        ChunkIndex = 0,
                        Metadata = marker,
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10, MetadataFilter = marker },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("replacement text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = _sut;

        await manageable.CreateCollectionAsync("temp_collection", 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync("temp_collection", TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync("temp_collection", TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync("temp_collection", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foo; DROP TABLE rag_chunks; --")]
    [InlineData("FOO")]
    [InlineData("1starts_with_digit")]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    public async Task CreateCollectionAsync_InvalidName_ThrowsArgumentException(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateCollectionAsync(name, 3, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foo; DROP TABLE rag_chunks; --")]
    [InlineData("1bad")]
    public async Task DeleteCollectionAsync_InvalidName_ThrowsArgumentException(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.DeleteCollectionAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCollectionAsync_NameTooLongToDeriveIndexNames_ThrowsArgumentException()
    {
        // A legal 48-char identifier, but "idx_{name}_document_id" would be 64 bytes and
        // PostgreSQL would silently truncate it.
        var name = new string('a', 48);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateCollectionAsync(name, 3, TestContext.Current.CancellationToken));

        Assert.Contains("the maximum is 47", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_CreatesHnswIndexOnEmbedding()
    {
        var indexDef = await ScalarAsync<string>(
            _postgres.GetConnectionString(),
            """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'rag_chunks' AND indexname = 'idx_rag_chunks_embedding'
            """);

        Assert.NotNull(indexDef);
        Assert.Contains("USING hnsw", indexDef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vector_cosine_ops", indexDef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCollectionAsync_CreatesUniqueChunkKeyAndHnswIndex()
    {
        await _sut.CreateCollectionAsync("indexed_collection", 3, TestContext.Current.CancellationToken);

        var uniqueDef = await ScalarAsync<string>(
            _postgres.GetConnectionString(),
            """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'indexed_collection' AND indexname = 'idx_indexed_collection_doc_chunk'
            """);
        var hnswDef = await ScalarAsync<string>(
            _postgres.GetConnectionString(),
            """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'indexed_collection' AND indexname = 'idx_indexed_collection_embedding'
            """);

        Assert.NotNull(uniqueDef);
        Assert.Contains("CREATE UNIQUE INDEX", uniqueDef, StringComparison.Ordinal);
        Assert.Contains("document_id, chunk_index", uniqueDef, StringComparison.Ordinal);

        Assert.NotNull(hnswDef);
        Assert.Contains("USING hnsw", hnswDef, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_TableWithPreExistingDuplicates_FailsFast()
    {
        // The pre-fix schema (no unique key) cannot be recreated in the fixture's database,
        // which this class already migrated — so build it in a database of its own.
        var legacyConnectionString = await CreateLegacyDuplicateDatabaseAsync("legacy_duplicates");

        using var store = new PgVectorStore(legacyConnectionString, vectorDimensions: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("1 duplicate key", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            "GROUP BY document_id, chunk_index HAVING count(*) > 1",
            ex.Message,
            StringComparison.Ordinal);

        // Both rows survive: the migration refuses, it does not repair by deleting data.
        Assert.Equal(2L, await ScalarAsync<long>(legacyConnectionString, "SELECT count(*) FROM rag_chunks"));
        Assert.Equal(
            "first write,second write",
            await ScalarAsync<string>(
                legacyConnectionString,
                "SELECT string_agg(text, ',' ORDER BY id) FROM rag_chunks"));
    }

    private async Task<string> CreateLegacyDuplicateDatabaseAsync(string database)
    {
        await ExecuteAsync(_postgres.GetConnectionString(), $"CREATE DATABASE {database}");

        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = database,
        }.ConnectionString;

        await ExecuteAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS vector");
        await ExecuteAsync(
            connectionString,
            """
            CREATE TABLE rag_chunks (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                metadata JSONB NOT NULL DEFAULT '{}',
                embedding vector(3) NOT NULL
            )
            """);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO rag_chunks (document_id, chunk_index, text, embedding) VALUES
                ('legacy-doc', 0, 'first write', '[1,0,0]'),
                ('legacy-doc', 0, 'second write', '[0,1,0]')
            """);

        return connectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is T typed ? typed : default;
    }
}
