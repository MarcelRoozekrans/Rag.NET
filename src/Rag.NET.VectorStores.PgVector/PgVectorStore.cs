using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;

namespace Rag.NET.PgVector;

/// <summary>
/// PostgreSQL/pgvector-backed <see cref="IVectorStore"/> (dense vectors only). For SPLADE
/// sparse vector support use <see cref="PgVectorSparseVectorStore"/> — deliberately a separate
/// type so an <c>is ISparseSearchable</c> capability probe on the registered store is honest: a
/// dense-only PgVector store never advertises sparse support, and the ingestion/retrieval
/// pipelines skip sparse work entirely instead of computing SPLADE vectors that nothing could
/// store.
/// </summary>
public partial class PgVectorStore : IVectorStore, ICollectionManageable, IDisposable
{
    /// <summary>
    /// pgvector refuses to build an HNSW index on a column wider than this
    /// ("column cannot have more than 2000 dimensions for hnsw index"). Measured against
    /// pgvector 0.8.2: 2000 builds, 2001 fails.
    /// </summary>
    private const int HnswDimensionLimit = 2000;

    private const int IterativeScanUnknown = -1;
    private const int IterativeScanUnsupported = 0;
    private const int IterativeScanSupported = 1;

    private protected readonly NpgsqlDataSource _dataSource;
    private readonly int _vectorDimensions;

    /// <summary>
    /// Cached answer to "does this server's pgvector understand <c>hnsw.iterative_scan</c>?"
    /// Resolved on the first search and reused; a race just re-reads the same answer.
    /// </summary>
    private int _iterativeScanSupport = IterativeScanUnknown;

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
    /// <para>
    /// <b>Dense search becomes approximate.</b> This method builds an HNSW index on
    /// <c>embedding</c> (<c>vector_cosine_ops</c>, matching the <c>&lt;=&gt;</c> operator
    /// <see cref="SearchAsync"/> orders by). HNSW is an <i>approximate</i> nearest-neighbour
    /// index: results may differ from the exact sequential scan returned by versions that built
    /// no ANN index. The control that matters is <c>hnsw.iterative_scan</c>, which
    /// <see cref="SearchAsync"/> sets — see its documentation; raising <c>hnsw.ef_search</c> does
    /// <i>not</i> compensate for a filtered query truncating (measured: at
    /// <c>ef_search = 1000</c> a filtered search over 2,000 rows still returned 0 of its 5
    /// matches).
    /// </para>
    /// <para>
    /// <b>The index is skipped above <see cref="HnswDimensionLimit"/> dimensions.</b> pgvector
    /// cannot build HNSW on a wider column, and <c>text-embedding-3-large</c> (3072) is over the
    /// line. Rather than failing initialization for those models, this method leaves the index
    /// out and dense search stays an exact sequential scan — the behaviour such deployments
    /// already had. To get an index at those widths, store <c>halfvec</c> or reduce the embedding
    /// dimension at the provider.
    /// </para>
    /// <para>
    /// <b>This method can be long-running, and it blocks writers.</b> Building the HNSW index
    /// over a large existing table is slow and memory-hungry, and it happens inline here —
    /// callers that treat initialization as a quick startup step should budget for it on the
    /// first run after upgrading. <c>CREATE INDEX</c> takes a <c>ShareLock</c> on the table:
    /// concurrent <i>writes</i> block until it finishes, reads are unaffected.
    /// <c>CREATE INDEX CONCURRENTLY</c> is deliberately not used — it cannot run inside a
    /// transaction, and a failed run leaves an <c>INVALID</c> index that
    /// <c>IF NOT EXISTS</c> would then skip forever.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The table already contains rows sharing a <c>(document_id, chunk_index)</c> pair, so the
    /// unique key cannot be created — the message carries the duplicate-key count and the query
    /// to inspect them — or it already exists with an <c>embedding</c> column declared at a
    /// different dimension than this store is configured for (see
    /// <see cref="EnsureEmbeddingColumnDimensionAsync"/>).
    /// </exception>
    public virtual async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await EnableVectorExtensionAsync(conn, cancellationToken).ConfigureAwait(false);
            await EnsureEmbeddingColumnDimensionAsync(conn, "rag_chunks", _vectorDimensions, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteNonQueryAsync(conn, CreateTableSql("rag_chunks", _vectorDimensions), cancellationToken)
                .ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                conn,
                "CREATE INDEX IF NOT EXISTS idx_rag_chunks_document_id ON rag_chunks (document_id)",
                cancellationToken).ConfigureAwait(false);

            await EnsureChunkKeyIndexAsync(conn, "rag_chunks", "idx_rag_chunks_doc_chunk", cancellationToken)
                .ConfigureAwait(false);

            await EnsureHnswIndexAsync(
                conn, "rag_chunks", "idx_rag_chunks_embedding", _vectorDimensions, cancellationToken)
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
    /// <exception cref="InvalidOperationException">
    /// The table has no unique index on <c>(document_id, chunk_index)</c>, so the upsert has
    /// nothing to conflict on — almost always because <see cref="InitializeAsync"/> was never
    /// called against it.
    /// </exception>
    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", "rag_chunks");
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            try
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
            catch (PostgresException ex)
                when (string.Equals(ex.SqlState, PostgresErrorCodes.InvalidColumnReference, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PgVectorStore.StoreAsync upserts on (document_id, chunk_index), but table rag_chunks has no " +
                    "unique index on exactly those columns, so PostgreSQL rejected the ON CONFLICT clause " +
                    $"(SQLSTATE {PostgresErrorCodes.InvalidColumnReference}). Call InitializeAsync() on this store " +
                    "before storing — it creates the key — or create it by hand:\n\n" +
                    "CREATE UNIQUE INDEX idx_rag_chunks_doc_chunk ON rag_chunks (document_id, chunk_index)",
                    ex);
            }
        }
    }

    /// <summary>
    /// Returns the chunks nearest <paramref name="queryEmbedding"/> by cosine similarity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the HNSW index exists the scan is <b>approximate</b>, and both
    /// <see cref="SearchOptions.MinScore"/> and <see cref="SearchOptions.MetadataFilter"/> are
    /// applied <i>after</i> the index has picked its candidates. With pgvector's default
    /// <c>hnsw.iterative_scan = off</c> a selective filter can therefore discard every candidate
    /// and the search returns <b>nothing</b> even though matching rows exist — measured: 2,000
    /// rows, 5 matching the filter, index scan, 0 returned. This method sets
    /// <c>hnsw.iterative_scan = relaxed_order</c> for the query, which makes pgvector keep
    /// scanning until it has enough surviving rows (all 5 in that same measurement). Raising
    /// <c>hnsw.ef_search</c> does not fix it — at 1000 the same query still returned 0.
    /// </para>
    /// <para>
    /// <b><c>relaxed_order</c> relaxes the ordering</b>, not only the scan: that recall is bought
    /// by letting pgvector return the surviving rows slightly out of distance order. Callers never
    /// see it — <see cref="ReadSearchResultsAsync"/> sorts by <see cref="SearchResult.Score"/>
    /// descending before returning, so results come back in descending score order under every
    /// iterative-scan mode. That is load-bearing rather than tidy: <c>RrfMerger</c> ranks the
    /// hits it fuses by <i>list position</i>.
    /// </para>
    /// <para>
    /// Even with iterative scanning a filtered search may return <b>fewer than</b>
    /// <see cref="SearchOptions.TopK"/> results: pgvector stops at <c>hnsw.max_scan_tuples</c>
    /// (20,000 by default) rather than degrading into a full scan.
    /// </para>
    /// <para>
    /// <c>hnsw.iterative_scan</c> arrived in pgvector 0.8. Against an older extension the setting
    /// is not issued at all (the store reads <c>pg_extension.extversion</c> once and caches the
    /// answer), so on 0.7 and below a filtered search over an HNSW index keeps the truncating
    /// behaviour described above — upgrading pgvector is the only fix.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", "rag_chunks");

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await ApplyIterativeScanAsync(conn, cancellationToken).ConfigureAwait(false);

            var hasFilter = options.MetadataFilter is { Count: > 0 };
            var cmd = new NpgsqlCommand(SearchSql(hasFilter), conn);
            await using (cmd.ConfigureAwait(false))
            {
                AddSearchParameters(cmd, queryEmbedding, options, hasFilter);
                var results = await ReadSearchResultsAsync(cmd, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("vectorstore.result.count", results.Count);
                return results;
            }
        }
    }

    private static string SearchSql(bool hasFilter)
    {
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

        return sql + "\nORDER BY embedding <=> $1\nLIMIT $3";
    }

    // Positional parameters are bound in Add order ($1 embedding, $2 MinScore, $3 TopK,
    // $4 filter). A mismatch here is silent, so this stays in one place.
    private static void AddSearchParameters(
        NpgsqlCommand cmd,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        bool hasFilter)
    {
        cmd.Parameters.AddWithValue(new Vector(queryEmbedding.ToArray()));
        cmd.Parameters.AddWithValue(options.MinScore);
        cmd.Parameters.AddWithValue(options.TopK);

        if (hasFilter)
        {
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                MetadataSerializer.SerializeMetadata(options.MetadataFilter!));
        }
    }

    /// <summary>
    /// Materialises the rows and returns them in <b>descending score order</b> — the single
    /// funnel both <see cref="SearchAsync"/> and
    /// <see cref="PgVectorSparseVectorStore.SearchSparseAsync"/> read through.
    /// </summary>
    /// <remarks>
    /// The sort is not redundant with the <c>ORDER BY</c>. Both searches set
    /// <c>hnsw.iterative_scan = relaxed_order</c>, which is by definition the mode that trades
    /// exact distance ordering for recall: pgvector may return the surviving rows slightly out of
    /// order. Downstream that is not cosmetic — <c>RrfMerger</c> ranks by <i>list position</i>, so
    /// an out-of-order row would feed a wrong rank into ensemble fusion. Sorting here makes
    /// "descending by score" a guarantee of both methods rather than a caveat, and the list is at
    /// most <see cref="SearchOptions.TopK"/> long so it costs nothing.
    /// </remarks>
    private protected static async Task<IReadOnlyList<SearchResult>> ReadSearchResultsAsync(
        NpgsqlCommand cmd,
        CancellationToken cancellationToken)
    {
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

        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>
    /// Turns on iterative index scanning for this connection, so a post-index filter cannot
    /// truncate the result set to nothing. No-op when there is no HNSW index to iterate (the
    /// dimension is over <see cref="HnswDimensionLimit"/>) or when pgvector predates 0.8.
    /// </summary>
    private async Task ApplyIterativeScanAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        if (_vectorDimensions > HnswDimensionLimit)
            return;

        await ApplyIterativeScanCoreAsync(conn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues the <c>hnsw.iterative_scan</c> setting when the server understands it, with no
    /// dense-dimension guard — for callers that search a different HNSW index than
    /// <c>embedding</c> (the sparse subtype's <c>sparsevec</c> index, which exists regardless of
    /// the dense width).
    /// </summary>
    private protected async Task ApplyIterativeScanCoreAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        if (!await SupportsIterativeScanAsync(conn, cancellationToken).ConfigureAwait(false))
            return;

        await ExecuteNonQueryAsync(conn, "SET hnsw.iterative_scan = relaxed_order", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> SupportsIterativeScanAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _iterativeScanSupport);
        if (cached != IterativeScanUnknown)
            return cached == IterativeScanSupported;

        var version = await ReadExtensionVersionAsync(conn, cancellationToken).ConfigureAwait(false);
        var supported = SupportsIterativeScan(version);
        Volatile.Write(
            ref _iterativeScanSupport,
            supported ? IterativeScanSupported : IterativeScanUnsupported);
        return supported;
    }

    /// <summary>
    /// Reads <c>pg_extension.extversion</c> for the <c>vector</c> extension, or
    /// <see langword="null"/> when it is not installed in this database.
    /// </summary>
    private protected static async Task<string?> ReadExtensionVersionAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", conn);
        await using (cmd.ConfigureAwait(false))
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result as string;
        }
    }

    /// <summary>
    /// True when <paramref name="extensionVersion"/> is a pgvector version that defines
    /// <c>hnsw.iterative_scan</c> (0.8.0 and above). An unreadable or absent version is treated
    /// as unsupported: issuing the <c>SET</c> against an older extension errors, and losing
    /// iterative scanning is far cheaper than failing every search.
    /// </summary>
    internal static bool SupportsIterativeScan(string? extensionVersion) =>
        AtLeastVersion(extensionVersion, major: 0, minor: 8);

    /// <summary>
    /// True when <paramref name="extensionVersion"/> parses as a <c>major.minor[.patch]</c>
    /// version at or above <paramref name="major"/>.<paramref name="minor"/>. An absent or
    /// unreadable version reads as <see langword="false"/> — every caller gates a capability on
    /// this, and "assume present" would trade a clear failure for an obscure one.
    /// </summary>
    internal static bool AtLeastVersion(string? extensionVersion, int major, int minor)
    {
        if (string.IsNullOrWhiteSpace(extensionVersion))
            return false;

        var parts = extensionVersion.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var actualMajor)
            || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var actualMinor))
        {
            return false;
        }

        return actualMajor > major || (actualMajor == major && actualMinor >= minor);
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", "rag_chunks");

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
    /// a unique key on <c>(document_id, chunk_index)</c>, a btree on <c>document_id</c>, and an
    /// HNSW index on <c>embedding</c>. The HNSW caveats documented on
    /// <see cref="InitializeAsync"/> apply here too.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a safe identifier, or is longer than
    /// <see cref="MaxCollectionNameLength"/> characters — see
    /// <see cref="ValidateCollectionNameLength"/> for why the shorter cap exists.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An existing table of that name already contains duplicate
    /// <c>(document_id, chunk_index)</c> pairs, or its <c>embedding</c> column is declared at a
    /// different dimension than <paramref name="vectorDimensions"/>.
    /// </exception>
    public async Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        var quotedName = ValidateAndQuoteIdentifier(name);
        ValidateCollectionNameLength(name);

        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await EnableVectorExtensionAsync(conn, cancellationToken).ConfigureAwait(false);
            await EnsureEmbeddingColumnDimensionAsync(conn, quotedName, vectorDimensions, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteNonQueryAsync(conn, CreateTableSql(quotedName, vectorDimensions), cancellationToken)
                .ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                conn,
                $"CREATE INDEX IF NOT EXISTS \"idx_{name}_document_id\" ON {quotedName} (document_id)",
                cancellationToken).ConfigureAwait(false);

            await EnsureChunkKeyIndexAsync(conn, quotedName, $"idx_{name}_doc_chunk", cancellationToken)
                .ConfigureAwait(false);

            await EnsureHnswIndexAsync(
                conn, quotedName, $"idx_{name}_embedding", vectorDimensions, cancellationToken)
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
    /// <para>
    /// <see cref="ValidateAndQuoteIdentifier"/> allows the full 63 characters PostgreSQL permits
    /// for a table name, but this store derives <b>three</b> index names from it by decorating
    /// it — <c>idx_{name}_document_id</c>, <c>idx_{name}_doc_chunk</c> and
    /// <c>idx_{name}_embedding</c> — and PostgreSQL <i>silently truncates</i> an over-long
    /// identifier rather than failing.
    /// </para>
    /// <para>
    /// The dangerous case is a <b>single</b> collection, not two colliding ones. Two names
    /// differing at, say, character 55 still produce distinct truncations and are fine. Nor does
    /// the damage arrive all at once: just past this cap only the longest decoration,
    /// <c>_document_id</c>, is truncated, so the index simply exists under a name the caller never
    /// asked for. It is from roughly 58 characters that all three derived names truncate onto the
    /// <i>same</i> 63 bytes, and that is the case worth rejecting.
    /// </para>
    /// <para>
    /// What such a name actually does — verified against pgvector 0.8.2 with a 60-character
    /// collection — is fail <b>loudly, in the wrong place</b>. The btree is created first and
    /// takes the truncated name. <see cref="EnsureChunkKeyIndexAsync"/> then runs, and because it
    /// resolves the key by <i>shape</i> against <c>pg_index</c> rather than by name it correctly
    /// reports no key present, finds no duplicate rows, reaches
    /// <see cref="RelationExistsAsync"/>, sees the btree squatting on the name it was about to
    /// use, and throws "a relation named 'X' already exists but is not a unique index".
    /// Creation aborts there — the HNSW index is never attempted, and had the key somehow been in
    /// place that <c>CREATE INDEX IF NOT EXISTS</c> would have seen the same taken name and
    /// silently skipped, leaving the collection with no ANN index. Rejecting up front still earns
    /// its keep: that runtime exception names a truncated identifier the caller never typed and
    /// advises dropping it, when the real fix is a shorter collection name.
    /// </para>
    /// <para>
    /// This cap covers only the names <i>this store derives</i>. The plain
    /// <c>CREATE INDEX IF NOT EXISTS</c> statements — <c>idx_rag_chunks_document_id</c>,
    /// <c>idx_rag_chunks_embedding</c> and the sparse subtype's <c>idx_rag_chunks_sparse</c> —
    /// still match on name alone, so a hand-made relation already holding one of those names
    /// makes the corresponding statement do nothing and the store quietly runs without that
    /// index. Unlike the unique key, that costs performance rather than correctness, which is why
    /// it is documented here rather than probed.
    /// </para>
    /// </remarks>
    private static void ValidateCollectionNameLength(string name)
    {
        if (name.Length > MaxCollectionNameLength)
            throw new ArgumentException(
                $"Collection name '{name}' is {name.Length} characters; the maximum is {MaxCollectionNameLength}. " +
                "Three index names are derived from it — " +
                $"'idx_{name}_document_id', 'idx_{name}_doc_chunk' and 'idx_{name}_embedding' — and PostgreSQL " +
                $"silently truncates identifiers at {MaxIdentifierLength} bytes rather than failing. The cap is the " +
                "longest name that leaves all three intact: past it they are truncated, and from about 58 " +
                "characters all three collapse onto the same identifier, so creating the collection fails partway " +
                "through on an index name you never wrote. Use a shorter name.",
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

    /// <summary>
    /// Fails fast when the table already exists with an <c>embedding</c> column declared at a
    /// different dimension than the store is configured for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CREATE TABLE IF NOT EXISTS</c> matches on the table <i>name</i> only: pointed at a
    /// <c>rag_chunks</c> whose <c>embedding</c> is <c>vector(768)</c>, a store configured for 1536
    /// skips the statement, reports initialization successful, and keeps the old typmod. Every
    /// later write then fails with pgvector's "expected 768 dimensions, not 1536" and every search
    /// with "different vector dimensions" — loudly, since the dense <see cref="StoreAsync"/> is
    /// not behind <c>StorageBehavior</c>'s catch, but at a distance from the cause that names
    /// neither the table nor the option that set it.
    /// </para>
    /// <para>
    /// This is the fourth instance in this store of "IF NOT EXISTS matches on name, not shape",
    /// after <see cref="EnsureChunkKeyIndexAsync"/>, <see cref="ValidateCollectionNameLength"/>
    /// and the sparse subtype's <c>sparse_embedding</c> probe, and reuses the last one's
    /// <c>atttypmod</c> lookup. An absent table or column (no row) is the ordinary first-run path.
    /// A dimensionless <c>vector</c> column reads back as <c>atttypmod = -1</c> and is left alone:
    /// it predates this probe, the store already tolerates it, and inventing a failure for it is
    /// not this gate's job.
    /// </para>
    /// </remarks>
    private static async Task EnsureEmbeddingColumnDimensionAsync(
        NpgsqlConnection conn,
        string tableSql,
        int vectorDimensions,
        CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand(
            """
            SELECT a.atttypmod
            FROM pg_attribute a
            WHERE a.attrelid = to_regclass($1) AND a.attname = 'embedding'
              AND NOT a.attisdropped
            """, conn);

        await using (cmd.ConfigureAwait(false))
        {
            cmd.Parameters.AddWithValue(tableSql);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not int existingDimension
                || existingDimension <= 0
                || existingDimension == vectorDimensions)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Table {tableSql} already has an 'embedding' column of vector({existingDimension}), but this " +
                $"store is configured for vector({vectorDimensions}). CREATE TABLE IF NOT EXISTS matches on the " +
                "table name alone, so it would leave the existing dimension in place and report initialization " +
                "successful — after which every store and search against it fails on the dimension mismatch, far " +
                "from the setting that caused it. Either construct the store with " +
                $"vectorDimensions: {existingDimension} to match the table, or drop {tableSql} and re-ingest the " +
                "affected documents with the embedding model this store is configured for. Dropping the table " +
                "discards every chunk already stored, and re-ingestion is what recomputes them.");
        }
    }

    /// <summary>
    /// Builds the dense ANN index, or skips it when the column is too wide for HNSW.
    /// </summary>
    /// <remarks>
    /// Skipping rather than throwing is deliberate: <c>text-embedding-3-large</c> is 3072
    /// dimensions, such deployments worked before this index existed (as an exact sequential
    /// scan), and failing their startup to announce a missing optimisation would be a
    /// regression. <c>vector_cosine_ops</c> matches the <c>&lt;=&gt;</c> operator
    /// <see cref="SearchAsync"/> orders by; <c>m</c> / <c>ef_construction</c> are left at
    /// pgvector's defaults, since tuning them is out of scope.
    /// </remarks>
    private static async Task EnsureHnswIndexAsync(
        NpgsqlConnection conn,
        string tableSql,
        string indexName,
        int vectorDimensions,
        CancellationToken cancellationToken)
    {
        if (vectorDimensions > HnswDimensionLimit)
            return;

        await ExecuteNonQueryAsync(
            conn,
            $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON {tableSql} USING hnsw (embedding vector_cosine_ops)",
            cancellationToken).ConfigureAwait(false);
    }

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

    private protected static async Task ExecuteNonQueryAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken)
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
    /// <para>
    /// The duplicate probe runs whenever a <i>correct</i> key is absent, and is skipped once one
    /// exists — so the aggregate scan is paid on the migrating run rather than on every startup.
    /// Rows are never deleted to force the migration through: that would be silent data loss on
    /// a path the caller did not ask to migrate.
    /// </para>
    /// <para>
    /// "Correct" is checked against <c>pg_index</c> — unique, valid, non-partial, and keyed on
    /// exactly <c>(document_id, chunk_index)</c> — not by looking up the index <i>name</i>. A
    /// name lookup would accept any relation that happened to hold the name (an ordinary btree,
    /// say), skip the duplicate probe, and then let <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> do
    /// nothing at all, leaving the store back in the duplicate-row bug with
    /// <see cref="StoreAsync"/> failing on every call.
    /// </para>
    /// </remarks>
    private static async Task EnsureChunkKeyIndexAsync(
        NpgsqlConnection conn,
        string tableSql,
        string indexName,
        CancellationToken cancellationToken)
    {
        if (await UniqueChunkKeyIndexExistsAsync(conn, tableSql, cancellationToken).ConfigureAwait(false))
            return;

        var duplicateKeys = await CountDuplicateChunkKeysAsync(conn, tableSql, cancellationToken)
            .ConfigureAwait(false);

        if (duplicateKeys > 0)
            throw new InvalidOperationException(
                $"Cannot key {tableSql} by (document_id, chunk_index): the table already contains " +
                $"{duplicateKeys} duplicate key(s) — rows written before this store enforced the key. " +
                "No rows were deleted; the database is unchanged. Inspect them with:\n\n" +
                DuplicateChunkKeyQuery(tableSql) + "\n\n" +
                "Decide which row of each pair to keep, remove the rest, then initialize again.");

        if (await RelationExistsAsync(conn, indexName, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Cannot key {tableSql} by (document_id, chunk_index): a relation named '{indexName}' already " +
                "exists but is not a unique index on exactly those columns. CREATE UNIQUE INDEX IF NOT EXISTS " +
                "would silently do nothing and every StoreAsync call would then fail with SQLSTATE " +
                $"{PostgresErrorCodes.InvalidColumnReference}. Drop or rename it, then initialize again.");

        await ExecuteNonQueryAsync(
            conn,
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{indexName}\" ON {tableSql} (document_id, chunk_index)",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the table carries a unique, valid, non-partial index keyed on exactly
    /// <c>(document_id, chunk_index)</c> — that is, one <c>ON CONFLICT</c> can infer.
    /// </summary>
    private static async Task<bool> UniqueChunkKeyIndexExistsAsync(
        NpgsqlConnection conn,
        string tableSql,
        CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_index i
                WHERE i.indrelid = to_regclass($1)
                  AND i.indisunique
                  AND i.indisvalid
                  AND i.indpred IS NULL
                  AND i.indexprs IS NULL
                  AND i.indnkeyatts = 2
                  AND i.indkey[0] = (SELECT a.attnum FROM pg_attribute a
                                     WHERE a.attrelid = i.indrelid AND a.attname = 'document_id'
                                       AND NOT a.attisdropped)
                  AND i.indkey[1] = (SELECT a.attnum FROM pg_attribute a
                                     WHERE a.attrelid = i.indrelid AND a.attname = 'chunk_index'
                                       AND NOT a.attisdropped)
            )
            """, conn);
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Parameters.AddWithValue(tableSql);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is true;
        }
    }

    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection conn,
        string relationName,
        CancellationToken cancellationToken)
    {
        var cmd = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", conn);
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Parameters.AddWithValue(relationName);
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
            : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

        return new TextChunk
        {
            DocumentId = new DocumentId(reader.GetString(0)),
            ChunkIndex = reader.GetInt32(1),
            Text = reader.GetString(2),
            Metadata = metadata,
        };
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            _dataSource.Dispose();
    }
}
