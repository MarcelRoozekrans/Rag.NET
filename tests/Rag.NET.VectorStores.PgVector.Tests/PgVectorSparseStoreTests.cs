using Npgsql;
using Pgvector.Npgsql;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Testcontainers.PostgreSql;
using Xunit;
using PgSparseVector = Pgvector.SparseVector;

namespace Rag.NET.PgVector.Tests;

/// <summary>
/// Schema-level coverage of the <c>sparsevec</c> column: the DDL the sparse subtype adds, the
/// 0-based/1-based index shift between SPLADE term ids and <c>sparsevec</c> literals, and
/// whether the sparse <c>ORDER BY</c> genuinely reaches the HNSW index.
/// </summary>
public class PgVectorSparseStoreTests : IAsyncLifetime
{
    private const int VocabularySize = 100;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .Build();

    private PgVectorSparseVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _sut = new PgVectorSparseVectorStore(
            _postgres.GetConnectionString(), vectorDimensions: 3, sparseVocabularySize: VocabularySize);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_AddsTheSparseColumnAndItsHnswIndex()
    {
        var columnType = await ScalarAsync<string>(
            """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            WHERE a.attrelid = 'rag_chunks'::regclass AND a.attname = 'sparse_embedding'
              AND NOT a.attisdropped
            """);

        var indexDef = await ScalarAsync<string>(
            """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'rag_chunks' AND indexname = 'idx_rag_chunks_sparse'
            """);

        Assert.Equal($"sparsevec({VocabularySize})", columnType);
        Assert.NotNull(indexDef);
        Assert.Contains("USING hnsw", indexDef, StringComparison.OrdinalIgnoreCase);
        // Dot product, because that is what SPLADE similarity is and what <#> answers.
        Assert.Contains("sparsevec_ip_ops", indexDef, StringComparison.Ordinal);

        // The base class's work still happened — this is an override, not a replacement.
        Assert.NotNull(await ScalarAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE indexname = 'idx_rag_chunks_doc_chunk'"));
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_rag_chunks_sparse'"));
    }

    [Fact]
    public async Task SparseLiteral_ShiftsFromZeroBasedToOneBased()
    {
        // SPLADE term ids are 0-based vocabulary indices; a sparsevec literal is 1-based. Term
        // id 0 is the case where getting that wrong is unmissable — it has no valid 1-based
        // spelling of its own.
        var ct = TestContext.Current.CancellationToken;
        var chunk = MakeChunk("shift-probe", 0, "term zero");

        await _sut.StoreAsync([chunk], ct);
        await _sut.StoreSparseAsync(
            [(chunk, Sparse([0, 7], [1.5f, 2.5f]))], ct);

        // On the wire the literal is 1-based...
        var literal = await ScalarAsync<string>(
            "SELECT sparse_embedding::text FROM rag_chunks WHERE document_id = 'shift-probe'");
        Assert.Equal($"{{1:1.5,8:2.5}}/{VocabularySize}", literal);

        // ...and it comes back into .NET 0-based, unchanged from what went in.
        var roundTripped = await ReadSparseAsync(
            "SELECT sparse_embedding FROM rag_chunks WHERE document_id = 'shift-probe'");
        Assert.NotNull(roundTripped);
        Assert.Equal([0, 7], roundTripped.Indices.ToArray());
        Assert.Equal([1.5f, 2.5f], roundTripped.Values.ToArray());

        // And the shift is applied consistently, so a query on term id 0 still matches.
        var results = await _sut.SearchSparseAsync(
            Sparse([0], [2.0f]), new SearchOptions { TopK = 10 }, ct);
        Assert.Single(results);
        Assert.Equal(3.0, results[0].Score, precision: 3);
    }

    [Fact]
    public async Task SparseOrderBy_UsesTheSparseHnswIndex()
    {
        // The un-negated ORDER BY is what lets pgvector answer from the index. Ordering by the
        // negated score expression silently falls back to a sort over a full scan — the whole
        // reason SearchSparseSql selects -(...) but orders by the raw operator.
        var ct = TestContext.Current.CancellationToken;
        var chunk = MakeChunk("explain-probe", 0, "hay");
        await _sut.StoreAsync([chunk], ct);
        await _sut.StoreSparseAsync([(chunk, Sparse([5], [1.0f]))], ct);
        await ExecuteAsync("ANALYZE rag_chunks");

        // The full predicate set SearchSparseSql always emits — not a reduced form, so a future
        // predicate that defeated the index would fail this test rather than slip through.
        var indexed = await ExplainAsync(SparseSearchSql(hasFilter: false, negatedOrderBy: false));
        var filtered = await ExplainAsync(SparseSearchSql(hasFilter: true, negatedOrderBy: false));
        var negated = await ExplainAsync(SparseSearchSql(hasFilter: false, negatedOrderBy: true));

        Assert.Contains("Index Scan using idx_rag_chunks_sparse", indexed, StringComparison.Ordinal);
        Assert.Contains("Index Scan using idx_rag_chunks_sparse", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("idx_rag_chunks_sparse", negated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape <c>PgVectorSparseVectorStore.SearchSparseSql</c> builds, with the parameters
    /// inlined as literals so EXPLAIN can plan it. <paramref name="negatedOrderBy"/> produces the
    /// wrong variant the production query deliberately avoids.
    /// </summary>
    private static string SparseSearchSql(bool hasFilter, bool negatedOrderBy)
    {
        var query = "'{6:1}/" + VocabularySize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'";

        var sql = $"""
            SELECT document_id, chunk_index, text, metadata,
                   -(sparse_embedding <#> {query}) AS score
            FROM rag_chunks
            WHERE sparse_embedding IS NOT NULL
              AND -(sparse_embedding <#> {query}) > 0
              AND -(sparse_embedding <#> {query}) >= 0
            """;

        if (hasFilter)
        {
            sql += "\n  AND metadata @> '{\"tag\":\"needle\"}'::jsonb";
        }

        var order = negatedOrderBy
            ? $"-(sparse_embedding <#> {query})"
            : $"sparse_embedding <#> {query}";

        return sql + $"\nORDER BY {order}\nLIMIT 10";
    }

    [Fact]
    public async Task InitializeAsync_ExistingSparseColumnOfADifferentDimension_FailsFast()
    {
        // ADD COLUMN IF NOT EXISTS matches on the column NAME: without the typmod probe this
        // second store initializes "successfully" against the sparsevec(100) column the fixture
        // created, and then every sparse write and read fails on the dimension mismatch — both
        // of which StorageBehavior and EnsembleBehavior swallow, leaving a silently dense-only
        // deployment behind a single log line.
        using var mismatched = new PgVectorSparseVectorStore(
            _postgres.GetConnectionString(),
            vectorDimensions: 3,
            sparseVocabularySize: PgVectorSparseVectorStore.DefaultSparseVocabularySize);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mismatched.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("rag_chunks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sparse_embedding", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"sparsevec({VocabularySize})", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"sparsevec({PgVectorSparseVectorStore.DefaultSparseVocabularySize})",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains($"sparseVocabularySize: {VocabularySize}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN sparse_embedding", ex.Message, StringComparison.Ordinal);

        // Re-ingestion, NOT ReindexStaleAsync: that walks the embedding version store and skips
        // every document whose model and dimension still match, so dropping the sparse column
        // makes nothing stale and it would regenerate nothing. Directing the user at a call that
        // silently does nothing is the exact failure mode this store keeps designing out.
        Assert.Contains("re-ingest", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RegenerateSparseAsync", ex.Message, StringComparison.Ordinal);

        // Nothing was altered: the column the fixture created is untouched.
        Assert.Equal($"sparsevec({VocabularySize})", await ScalarAsync<string>(
            """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            WHERE a.attrelid = to_regclass('rag_chunks') AND a.attname = 'sparse_embedding'
              AND NOT a.attisdropped
            """));
    }

    private static EmbeddedChunk MakeChunk(string docId, int chunkIndex, string text) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
        Embedding = new float[] { 1.0f, 0.0f, 0.0f },
    };

    private static SparseVector Sparse(int[] indices, float[] values) =>
        new() { Indices = indices, Values = values };

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> ExplainAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using (var settings = new NpgsqlCommand("SET enable_seqscan = off", conn))
        {
            await settings.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var cmd = new NpgsqlCommand("EXPLAIN " + sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var plan = new System.Text.StringBuilder();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }

    private async Task<PgSparseVector?> ReadSparseAsync(string sql)
    {
        var builder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
        builder.UseVector();
        await using var dataSource = builder.Build();
        await using var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await conn.ReloadTypesAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken) as PgSparseVector;
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is T typed ? typed : default;
    }
}
