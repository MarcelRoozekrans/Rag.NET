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
    public async Task SearchAsync_ReturnsResultsInDescendingScoreOrder()
    {
        // Not a restatement of the ORDER BY: the store runs searches under
        // hnsw.iterative_scan = relaxed_order, the mode that trades exact distance ordering for
        // recall, so the rows may arrive slightly out of order. RrfMerger ranks by list position,
        // which makes descending score order a contract of this method rather than a side effect.
        // Its own database, so rows written by the other tests in this class cannot join the
        // unfiltered TopK and make the expected order depend on execution order.
        var connectionString = await CreateDatabaseAsync("search_ordering");
        using var store = new PgVectorStore(connectionString, vectorDimensions: 3);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk { Text = "far", DocumentId = new DocumentId("order-doc"), ChunkIndex = 0 },
                    Embedding = new float[] { 0.0f, 1.0f, 0.0f },
                },
                new EmbeddedChunk
                {
                    Chunk = new TextChunk { Text = "near", DocumentId = new DocumentId("order-doc"), ChunkIndex = 1 },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "middling",
                        DocumentId = new DocumentId("order-doc"),
                        ChunkIndex = 2,
                    },
                    Embedding = new float[] { 0.7071f, 0.7071f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 3 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal("near", results[0].Chunk.Text);
        Assert.Equal("middling", results[1].Chunk.Text);
        Assert.Equal("far", results[2].Chunk.Text);
        for (var i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score);
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
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" },
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
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("engineering doc", results[0].Chunk.Text);
    }

    [Fact]
    public async Task StoreAndSearch_TypedMetadata_KindsSurviveRoundTrip()
    {
        // A number reading back as the string "3" is the flattening bug the typed metadata
        // design removes (#91) — so the assertion is on Kind, not on textual form.
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        await _sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "typed metadata chunk", DocumentId = new DocumentId("doc-typed"), ChunkIndex = 0,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                        {
                            ["page"] = 3,
                            ["rating"] = 4.5,
                            ["published"] = true,
                            ["reviewed_at"] = reviewedAt,
                            ["source"] = "unit",
                        },
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        var metadata = Assert.Single(results).Chunk.Metadata;
        Assert.Equal(MetadataValueKind.Number, metadata["page"].Kind);
        Assert.Equal(3d, metadata["page"].NumberValue);
        Assert.Equal(4.5, metadata["rating"].NumberValue);
        Assert.Equal(MetadataValueKind.Boolean, metadata["published"].Kind);
        Assert.True(metadata["published"].BooleanValue);
        Assert.Equal(MetadataValueKind.DateTimeOffset, metadata["reviewed_at"].Kind);
        Assert.Equal(reviewedAt, metadata["reviewed_at"].DateTimeOffsetValue);
        Assert.Equal(MetadataValueKind.String, metadata["source"].Kind);
    }

    [Fact]
    public async Task Search_NumericMetadataFilter_Filters()
    {
        // The chunk nearest the query vector is on page 4 and TopK = 1, so only server-side
        // jsonb containment with a native JSON number ({"page": 3}) can return the farther
        // page-3 chunk.
        await _sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "page four chunk", DocumentId = new DocumentId("doc-p4"), ChunkIndex = 0,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 4 },
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "page three chunk", DocumentId = new DocumentId("doc-p3"), ChunkIndex = 0,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 },
                    },
                    Embedding = new float[] { 0.8f, 0.6f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 1,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 },
            },
            TestContext.Current.CancellationToken);

        var hit = Assert.Single(results);
        Assert.Equal("page three chunk", hit.Chunk.Text);
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
        var marker = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["upsert_probe"] = "a" };

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

    [Theory]
    [InlineData("0.8.0", true)]
    [InlineData("0.8.2", true)]
    [InlineData("0.9.1", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.7.4", false)]
    [InlineData("0.5.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-a-version", false)]
    [InlineData("0", false)]
    public void SupportsIterativeScan_GatesOnPgvector08(string? extensionVersion, bool expected)
    {
        // hnsw.iterative_scan arrived in pgvector 0.8; issuing the SET against an older
        // extension errors, so an unreadable version must read as unsupported.
        Assert.Equal(expected, PgVectorStore.SupportsIterativeScan(extensionVersion));
    }

    [Fact]
    public async Task InitializeAsync_DimensionsAboveHnswLimit_SkipsTheIndexInsteadOfFailing()
    {
        // pgvector refuses HNSW above 2000 dimensions; text-embedding-3-large is 3072.
        // Initialization must still succeed — those deployments worked before the index existed.
        var connectionString = await CreateDatabaseAsync("wide_dimensions");

        using var store = new PgVectorStore(connectionString, vectorDimensions: 3072);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ScalarAsync<string>(
            connectionString,
            "SELECT indexname FROM pg_indexes WHERE indexname = 'idx_rag_chunks_embedding'"));

        // The unique key is not optional, and is unaffected by the width.
        Assert.NotNull(await ScalarAsync<string>(
            connectionString,
            "SELECT indexname FROM pg_indexes WHERE indexname = 'idx_rag_chunks_doc_chunk'"));

        // And the store is still usable at that width.
        await store.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk { Text = "wide", DocumentId = new DocumentId("wide-1"), ChunkIndex = 0 },
                    Embedding = new float[3072],
                },
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM rag_chunks"));
    }

    [Fact]
    public async Task InitializeAsync_ExistingEmbeddingColumnOfADifferentDimension_FailsFast()
    {
        // CREATE TABLE IF NOT EXISTS matches on the table NAME: without the typmod probe this
        // store initializes "successfully" against the vector(768) column below, keeps the old
        // typmod, and then fails every write and search on a dimension mismatch far from the
        // setting that caused it. Same hazard the sparse column's ADD COLUMN probe closes.
        var connectionString = await CreateDatabaseAsync("dense_dimension_drift");
        await CreateLegacySchemaAsync(connectionString, vectorDimensions: 768);

        using var mismatched = new PgVectorStore(connectionString, vectorDimensions: 1536);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mismatched.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("rag_chunks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vector(768)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vector(1536)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vectorDimensions: 768", ex.Message, StringComparison.Ordinal);

        // Nothing was altered: the existing column is untouched.
        Assert.Equal("vector(768)", await ScalarAsync<string>(
            connectionString,
            """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            WHERE a.attrelid = to_regclass('rag_chunks') AND a.attname = 'embedding'
              AND NOT a.attisdropped
            """));

        // And the probe only fires on a genuine mismatch — a store that agrees with the table
        // still initializes, which is what makes this a gate rather than a blanket refusal.
        using var matching = new PgVectorStore(connectionString, vectorDimensions: 768);
        await matching.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await ScalarAsync<string>(
            connectionString,
            "SELECT indexname FROM pg_indexes WHERE indexname = 'idx_rag_chunks_doc_chunk'"));
    }

    [Fact]
    public async Task CreateCollectionAsync_ExistingTableOfADifferentDimension_FailsFast()
    {
        // The collection path issues the same CREATE TABLE IF NOT EXISTS, so it needs the same
        // gate — against its vectorDimensions argument rather than the store's.
        var connectionString = await CreateDatabaseAsync("collection_dimension_drift");
        await ExecuteAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS vector");
        await ExecuteAsync(
            connectionString,
            """
            CREATE TABLE drifted_collection (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                metadata JSONB NOT NULL DEFAULT '{}',
                embedding vector(768) NOT NULL
            )
            """);

        using var store = new PgVectorStore(connectionString, vectorDimensions: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateCollectionAsync("drifted_collection", 1536, TestContext.Current.CancellationToken));

        Assert.Contains("vector(768)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vector(1536)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_MetadataFilterOverTheHnswIndex_StillReturnsItsMatches()
    {
        // pgvector applies the metadata filter AFTER the index has chosen its candidates, and
        // hnsw.iterative_scan defaults to off — so a selective filter can discard every candidate
        // and return nothing. The planner only picks the index for this shape at much larger
        // scales, so the database forces it: the point under test is what happens WHEN the index
        // is used, not when the planner decides to use it.
        var connectionString = await CreateDatabaseAsync("hnsw_filter");
        await ExecuteAsync(_postgres.GetConnectionString(), "ALTER DATABASE hnsw_filter SET enable_seqscan = off");

        using var store = new PgVectorStore(connectionString, vectorDimensions: 3);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        // 2,000 rows clustered near the query vector, plus 5 tagged rows deliberately far from
        // it — far enough that they fall outside the default ef_search = 40 candidate set. The
        // control assertion below fails loudly if this stops reproducing.
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO rag_chunks (document_id, chunk_index, text, metadata, embedding)
            SELECT 'hay-' || i, 0, 'hay', '{}'::jsonb,
                   ('[1,' || ((i % 97)::numeric / 4000) || ',' || ((i % 89)::numeric / 4000) || ']')::vector(3)
            FROM generate_series(1, 2000) i
            """);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO rag_chunks (document_id, chunk_index, text, metadata, embedding)
            SELECT 'needle-' || i, 0, 'needle', '{"tag":"needle"}'::jsonb,
                   ('[1,' || (0.9 + i::numeric / 100) || ',' || (0.9 + i::numeric / 100) || ']')::vector(3)
            FROM generate_series(1, 5) i
            """);
        await ExecuteAsync(connectionString, "ANALYZE rag_chunks");

        // The test is worthless unless the index is genuinely in the plan.
        var plan = await ExplainAsync(connectionString, FilteredSearchSql);
        Assert.Contains("Index Scan using idx_rag_chunks_embedding", plan, StringComparison.Ordinal);

        // Control: the same query on a connection without the store's setting truncates to zero.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM ({FilteredSearchSql}) q"));

        var results = await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tag"] = "needle" },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("needle", r.Chunk.Text));
    }

    private const string FilteredSearchSql = """
        SELECT document_id FROM rag_chunks
        WHERE 1 - (embedding <=> '[1,0,0]') >= 0 AND metadata @> '{"tag":"needle"}'
        ORDER BY embedding <=> '[1,0,0]' LIMIT 10
        """;

    [Fact]
    public async Task InitializeAsync_NonUniqueIndexHoldingTheKeyName_StillRunsTheDuplicateProbe()
    {
        // A name-based existence check would see idx_rag_chunks_doc_chunk, skip the probe, and
        // let CREATE UNIQUE INDEX IF NOT EXISTS quietly do nothing — leaving the store back in
        // the duplicate-row bug with every StoreAsync failing on 42P10.
        var connectionString = await CreateLegacyDuplicateDatabaseAsync("impostor_index");
        await ExecuteAsync(
            connectionString,
            "CREATE INDEX idx_rag_chunks_doc_chunk ON rag_chunks (document_id, chunk_index)");

        using var store = new PgVectorStore(connectionString, vectorDimensions: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("1 duplicate key", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM rag_chunks"));
    }

    [Fact]
    public async Task StoreAsync_OnATableThatWasNeverInitialized_ThrowsNamingInitializeAsync()
    {
        var connectionString = await CreateDatabaseAsync("uninitialized");
        await CreateLegacySchemaAsync(connectionString);

        using var store = new PgVectorStore(connectionString, vectorDimensions: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.StoreAsync(
                [
                    new EmbeddedChunk
                    {
                        Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 },
                        Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                    },
                ],
                TestContext.Current.CancellationToken));

        Assert.Contains("InitializeAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("42P10", ex.Message, StringComparison.Ordinal);
        Assert.IsType<PostgresException>(ex.InnerException);
    }

    private async Task<string> CreateDatabaseAsync(string database)
    {
        await ExecuteAsync(_postgres.GetConnectionString(), $"CREATE DATABASE {database}");
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = database,
        }.ConnectionString;
    }

    /// <summary>The pre-fix schema: no unique key on (document_id, chunk_index).</summary>
    private static async Task CreateLegacySchemaAsync(string connectionString, int vectorDimensions = 3)
    {
        await ExecuteAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS vector");
        await ExecuteAsync(
            connectionString,
            $$"""
            CREATE TABLE rag_chunks (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                metadata JSONB NOT NULL DEFAULT '{}',
                embedding vector({{vectorDimensions}}) NOT NULL
            )
            """);
    }

    private async Task<string> CreateLegacyDuplicateDatabaseAsync(string database)
    {
        var connectionString = await CreateDatabaseAsync(database);
        await CreateLegacySchemaAsync(connectionString);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO rag_chunks (document_id, chunk_index, text, embedding) VALUES
                ('legacy-doc', 0, 'first write', '[1,0,0]'),
                ('legacy-doc', 0, 'second write', '[0,1,0]')
            """);

        return connectionString;
    }

    private static async Task<string> ExplainAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand("EXPLAIN " + sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var plan = new System.Text.StringBuilder();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
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
