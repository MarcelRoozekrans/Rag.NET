using Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
// Pgvector ships its own SparseVector (the wire representation); the Rag.NET contract type keeps
// the plain name and the pgvector one is addressed through an alias.
using PgSparseVector = Pgvector.SparseVector;
using SparseVector = Rag.NET.Models.SparseVector;

namespace Rag.NET.PgVector;

/// <summary>
/// Sparse-capable <see cref="PgVectorStore"/>: serves <see cref="ISparseSearchable"/> through a
/// nullable <c>sparse_embedding sparsevec(N)</c> column on the same <c>rag_chunks</c> rows that
/// hold the dense vectors, searched server-side with pgvector's <c>&lt;#&gt;</c> inner-product
/// operator over an HNSW index.
/// <para>
/// This is a separate type (registered by <c>UsePgVector(..., enableSparseVectors: true)</c>)
/// rather than a flag on <see cref="PgVectorStore"/> so the pipelines' capability probe
/// (<c>store is ISparseSearchable</c>) stays honest — a dense-only store must never advertise
/// the capability, or <c>SparseEmbeddingBehavior</c> computes SPLADE vectors that nothing can
/// store.
/// </para>
/// <para>
/// <b>No ordering contract.</b> Sparse vectors live in their own column and the dense upsert's
/// <c>DO UPDATE SET</c> list deliberately excludes it, so — unlike Pinecone, where a whole
/// record is replaced — calling <see cref="PgVectorStore.StoreAsync"/> after
/// <see cref="StoreSparseAsync"/> for the same chunk does <i>not</i> drop its sparse vector.
/// Either order works.
/// </para>
/// </summary>
public sealed class PgVectorSparseVectorStore : PgVectorStore, ISparseSearchable
{
    /// <summary>
    /// BERT's WordPiece vocabulary size, which every SPLADE checkpoint in common use inherits.
    /// Sparse term ids are vocabulary indices, so this is the <c>sparsevec</c> column's dimension.
    /// </summary>
    public const int DefaultSparseVocabularySize = 30522;

    private const string SparseColumn = "sparse_embedding";
    private const string SparseIndexName = "idx_rag_chunks_sparse";

    private const string StoreSparseSql = """
        UPDATE rag_chunks SET sparse_embedding = $1
        WHERE document_id = $2 AND chunk_index = $3
        """;

    private readonly int _sparseVocabularySize;

    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="vectorDimensions">Dense embedding dimensions.</param>
    /// <param name="sparseVocabularySize">
    /// Dimension of the <c>sparsevec</c> column: the sparse encoder's vocabulary size, which must
    /// be strictly greater than every term id it emits. Defaults to
    /// <see cref="DefaultSparseVocabularySize"/>.
    /// </param>
    public PgVectorSparseVectorStore(
        string connectionString,
        int vectorDimensions = 1536,
        int sparseVocabularySize = DefaultSparseVocabularySize)
        : base(connectionString, vectorDimensions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sparseVocabularySize, 1);
        _sparseVocabularySize = sparseVocabularySize;
    }

    /// <summary>
    /// Creates everything <see cref="PgVectorStore.InitializeAsync"/> does, then adds the
    /// <c>sparse_embedding</c> column and its HNSW index. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// The column hangs off the table the base method creates, so <c>base.InitializeAsync</c>
    /// runs first. The index uses <c>sparsevec_ip_ops</c> because SPLADE similarity is a dot
    /// product — matching the <c>&lt;#&gt;</c> operator <see cref="SearchSparseAsync"/> orders
    /// by. All the HNSW caveats documented on <see cref="PgVectorStore.InitializeAsync"/> apply
    /// here too.
    /// </remarks>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(
                conn,
                $"ALTER TABLE rag_chunks ADD COLUMN IF NOT EXISTS {SparseColumn} sparsevec({_sparseVocabularySize})",
                cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                conn,
                $"CREATE INDEX IF NOT EXISTS {SparseIndexName} ON rag_chunks " +
                $"USING hnsw ({SparseColumn} sparsevec_ip_ops)",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Updates the existing row for each <c>(document_id, chunk_index)</c> — the unique key makes
    /// that at most one row — so the chunk's dense vector, text and metadata are untouched.
    /// Chunks whose sparse vector has no terms are skipped, per the interface contract.
    /// </remarks>
    public async Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var updates = BuildSparseUpdates(items);
        if (updates.Length == 0)
            return;

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            foreach (var (documentId, chunkIndex, vector) in updates)
            {
                var cmd = new NpgsqlCommand(StoreSparseSql, conn);
                await using (cmd.ConfigureAwait(false))
                {
                    // Positional parameters bind in Add order ($1 vector, $2 document id,
                    // $3 chunk index). A mismatch here is silent.
                    cmd.Parameters.AddWithValue(vector);
                    cmd.Parameters.AddWithValue(documentId);
                    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = chunkIndex });

                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Scores are the <b>raw dot product</b> of matching term weights, as
    /// <see cref="ISparseSearchable"/> specifies — unbounded above and <i>not</i> comparable to
    /// the cosine similarities <see cref="PgVectorStore.SearchAsync"/> returns.
    /// <see cref="SearchOptions.MinScore"/> therefore means something different here than it does
    /// on the dense path: the same option name, two scales.
    /// </para>
    /// <para>
    /// Chunks sharing no term with the query are <b>absent</b> rather than present with score 0,
    /// mirroring the in-memory reference implementation, which only ever scores chunks sharing at
    /// least one term. Every SPLADE weight is &gt; 0, so "score &gt; 0" is exactly "shares at
    /// least one term".
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (query.Count == 0)
            return [];

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            var hasFilter = options.MetadataFilter is { Count: > 0 };
            var cmd = new NpgsqlCommand(SearchSparseSql(hasFilter), conn);
            await using (cmd.ConfigureAwait(false))
            {
                AddSparseSearchParameters(cmd, ToPgSparseVector(query), options, hasFilter);
                return await ReadSearchResultsAsync(cmd, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <remarks>
    /// <c>&lt;#&gt;</c> returns the <b>negative</b> inner product, so the selected score negates
    /// it — but the <c>ORDER BY</c> deliberately does not. Ordering by the raw operator ascending
    /// (most negative first) is both the correct ranking and the only form pgvector can answer
    /// from the HNSW index; ordering by the negated expression silently falls back to a scan.
    /// </remarks>
    private static string SearchSparseSql(bool hasFilter)
    {
        var sql = $"""
            SELECT document_id, chunk_index, text, metadata,
                   -({SparseColumn} <#> $1) AS score
            FROM rag_chunks
            WHERE {SparseColumn} IS NOT NULL
              AND -({SparseColumn} <#> $1) > 0
              AND -({SparseColumn} <#> $1) >= $2
            """;

        if (hasFilter)
        {
            sql += "\n  AND metadata @> $4::jsonb";
        }

        return sql + $"\nORDER BY {SparseColumn} <#> $1\nLIMIT $3";
    }

    // Positional parameters are bound in Add order ($1 sparse vector, $2 MinScore, $3 TopK,
    // $4 filter), matching SearchSparseSql. A mismatch here is silent, so it stays in one place.
    private static void AddSparseSearchParameters(
        NpgsqlCommand cmd,
        PgSparseVector query,
        SearchOptions options,
        bool hasFilter)
    {
        cmd.Parameters.AddWithValue(query);
        cmd.Parameters.AddWithValue(options.MinScore);
        cmd.Parameters.AddWithValue(options.TopK);

        if (hasFilter)
        {
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                MetadataSerializer.SerializeMetadata(options.MetadataFilter!));
        }
    }

    /// <summary>
    /// Converts the items into the values the <c>UPDATE</c> binds, dropping the chunks with no
    /// terms. Synchronous on purpose: the conversion loop belongs outside the async state
    /// machine.
    /// </summary>
    private (string DocumentId, int ChunkIndex, PgSparseVector Vector)[] BuildSparseUpdates(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items)
    {
        var updates = new List<(string, int, PgSparseVector)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var (chunk, sparse) = items[i];
            if (sparse.Count == 0)
                continue; // no terms — nothing to store

            updates.Add((
                (string)chunk.Chunk.DocumentId,
                chunk.Chunk.ChunkIndex,
                ToPgSparseVector(sparse)));
        }

        return [.. updates];
    }

    /// <summary>
    /// Wraps the contract's sparse vector in pgvector's wire type. <b>The index bases differ:</b>
    /// SPLADE term ids are 0-based vocabulary indices and a <c>sparsevec</c> literal is 1-based
    /// (<c>{i:v,...}/dim</c>). <see cref="PgSparseVector"/> owns that shift — its
    /// <c>Indices</c> stay 0-based on the .NET side and it adds/removes the 1 when formatting and
    /// parsing — so nothing here shifts them a second time. A round-trip test pins it.
    /// </summary>
    private PgSparseVector ToPgSparseVector(SparseVector sparse) =>
        new(_sparseVocabularySize, sparse.Indices, sparse.Values);
}
