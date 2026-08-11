using System.Globalization;
using System.Runtime.InteropServices;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using StackExchange.Redis;
using SearchResult = Rag.NET.Models.SearchResult;

namespace Rag.NET.VectorStores.Redis;

/// <summary>
/// Redis-backed <see cref="IVectorStore"/> using RediSearch's vector similarity, for teams already
/// operating Redis — the same argument that justifies PgVector: reuse the datastore you run.
/// <para>
/// Chunks are stored as hashes under <c>{prefix}{documentId}:{chunkIndex}</c> and queried with
/// <c>*=&gt;[KNN k @embedding $vec AS vector_score]</c> over an HNSW index.
/// </para>
/// <para>
/// <b>RediSearch returns a distance, and this store returns a similarity.</b> With
/// <c>DISTANCE_METRIC COSINE</c>, <c>vector_score</c> is cosine <i>distance</i> in <c>[0, 2]</c>:
/// 0 is identical and larger is worse — the opposite direction from every score in this library.
/// Publishing it unconverted would invert every ranking and quietly break <c>MinScore</c>, which
/// is the defect issue #56 was opened with. It is converted to <c>1 - distance</c> here, giving
/// ordinary cosine similarity, which is why this type deliberately does <b>not</b> implement
/// <see cref="IScoreScaleAware"/>: its scores are on the scale every threshold already assumes,
/// exactly as PgVector's <c>1 - (embedding &lt;=&gt; $1)</c> is.
/// </para>
/// <para>
/// <b>Hybrid search is declined rather than approximated.</b> RediSearch can run a text query
/// alongside the vector one, but its text scoring is TF-IDF-shaped and not the BM25 the hybrid
/// arm expects, so a store advertising <see cref="IHybridSearchable"/> here would be fusing a
/// score it cannot describe. Not implementing the interface makes the pipeline fall back to dense
/// retrieval, which is honest; issue #86 asked for exactly that judgement rather than an
/// approximation.
/// </para>
/// </summary>
public sealed class RedisVectorStore : IVectorStore, ICollectionManageable, IDisposable
{
    /// <summary>The field the KNN clause aliases its distance into.</summary>
    private const string ScoreField = "vector_score";

    private const string EmbeddingField = "embedding";
    private const string TextField = "text";
    private const string DocumentIdField = "document_id";
    private const string ChunkIndexField = "chunk_index";

    private readonly IConnectionMultiplexer _redis;
    private readonly bool _ownsConnection;
    private readonly string _indexName;
    private readonly string _keyPrefix;
    private readonly int _vectorDimensions;

    /// <summary>Creates a store against a Redis connection string.</summary>
    /// <param name="configuration">A StackExchange.Redis configuration string, e.g. <c>localhost:6379</c>.</param>
    /// <param name="indexName">The RediSearch index to create and query.</param>
    /// <param name="vectorDimensions">The embedding width; must match the generator's.</param>
    public RedisVectorStore(string configuration, string indexName = "ragnet-idx", int vectorDimensions = 1536)
        : this(ConnectionMultiplexer.Connect(configuration), indexName, vectorDimensions, ownsConnection: true)
    {
    }

    /// <summary>Creates a store over a connection the caller owns and disposes.</summary>
    /// <param name="redis">An existing multiplexer — the common case when Redis is already used for caching.</param>
    /// <param name="indexName">The RediSearch index to create and query.</param>
    /// <param name="vectorDimensions">The embedding width; must match the generator's.</param>
    public RedisVectorStore(IConnectionMultiplexer redis, string indexName = "ragnet-idx", int vectorDimensions = 1536)
        : this(redis, indexName, vectorDimensions, ownsConnection: false)
    {
    }

    private RedisVectorStore(
        IConnectionMultiplexer redis, string indexName, int vectorDimensions, bool ownsConnection)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorDimensions, 1);

        _redis = redis;
        _indexName = indexName;
        _keyPrefix = indexName + ":";
        _vectorDimensions = vectorDimensions;
        _ownsConnection = ownsConnection;
    }

    private IDatabase Database => _redis.GetDatabase();

    /// <summary>
    /// Creates the HNSW index if it is absent. Idempotent: an existing index is left alone rather
    /// than dropped, because dropping it would discard every stored vector.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await CollectionExistsAsync(_indexName, cancellationToken).ConfigureAwait(false))
        {
            await CreateCollectionAsync(_indexName, _vectorDimensions, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task CreateCollectionAsync(
        string collectionName, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorDimensions, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var schema = new Schema()
            .AddTagField(DocumentIdField)
            .AddNumericField(ChunkIndexField)
            .AddTextField(TextField)
            .AddVectorField(
                EmbeddingField,
                Schema.VectorField.VectorAlgo.HNSW,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["TYPE"] = "FLOAT32",
                    ["DIM"] = vectorDimensions.ToString(CultureInfo.InvariantCulture),
                    ["DISTANCE_METRIC"] = "COSINE",
                });

        _ = await Database.FT().CreateAsync(
            collectionName,
            new FTCreateParams().On(IndexDataType.HASH).Prefix(PrefixFor(collectionName)),
            schema).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>FT.DROPINDEX</c> without <c>DD</c> removes the index and leaves the hashes, which would
    /// strand every stored chunk as unsearchable keys nothing later cleans up. <c>DD</c> deletes
    /// the documents too, which is what "deletes a collection and all its records" means.
    /// </remarks>
    public async Task DeleteCollectionAsync(
        string collectionName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        cancellationToken.ThrowIfCancellationRequested();

        if (!await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _ = await Database.FT().DropIndexAsync(collectionName, dd: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CollectionExistsAsync(
        string collectionName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _ = await Database.FT().InfoAsync(collectionName).ConfigureAwait(false);
            return true;
        }
        catch (RedisServerException)
        {
            // "Unknown index name" is how RediSearch reports absence; there is no probe that
            // answers without throwing.
            return false;
        }
    }

    /// <summary>The key prefix an index owns.</summary>
    /// <param name="collectionName">The index.</param>
    /// <returns>Its prefix.</returns>
    private static string PrefixFor(string collectionName) => collectionName + ":";

    /// <inheritdoc />
    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", nameof(RedisVectorStore));
        activity?.SetTag("chunk.count", chunks.Count);

        var database = Database;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var entries = new HashEntry[]
            {
                new(DocumentIdField, chunk.Chunk.DocumentId.Value),
                new(ChunkIndexField, chunk.Chunk.ChunkIndex),
                new(TextField, chunk.Chunk.Text),
                new(EmbeddingField, ToBytes(chunk.Embedding.Span)),
            };

            await database.HashSetAsync(KeyFor(chunk.Chunk.DocumentId.Value, chunk.Chunk.ChunkIndex), entries)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.TopK <= 0)
        {
            return [];
        }

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", nameof(RedisVectorStore));
        activity?.SetTag("top.k", options.TopK);

        var query = new Query($"*=>[KNN {options.TopK.ToString(CultureInfo.InvariantCulture)} @{EmbeddingField} $vec AS {ScoreField}]")
            .AddParam("vec", ToBytes(queryEmbedding.Span))
            .SetSortBy(ScoreField)
            .ReturnFields(DocumentIdField, ChunkIndexField, TextField, ScoreField)
            .Dialect(2);
        query.Limit(0, options.TopK);

        var response = await Database.FT().SearchAsync(_indexName, query).ConfigureAwait(false);

        var results = new List<SearchResult>(response.Documents.Count);
        foreach (ref readonly var document in CollectionsMarshal.AsSpan(response.Documents))
        {
            var score = ToSimilarity(document[ScoreField]);
            if (score < options.MinScore)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = document[TextField].ToString(),
                    DocumentId = new DocumentId(document[DocumentIdField].ToString()),
                    ChunkIndex = (int)document[ChunkIndexField],
                },
                Score = score,
            });
        }

        activity?.SetTag("result.count", results.Count);
        return results;
    }

    /// <inheritdoc />
    public async Task DeleteByDocumentIdAsync(
        string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        // Every chunk of one document, found through the tag index rather than by scanning keys:
        // KEYS blocks the server, and SCAN over a shared Redis walks keys this store did not write.
        var query = new Query($"@{DocumentIdField}:{{{EscapeTag(documentId)}}}")
            .ReturnFields(DocumentIdField)
            .Dialect(2);
        query.Limit(0, 10_000);

        var response = await Database.FT().SearchAsync(_indexName, query).ConfigureAwait(false);
        var keys = new RedisKey[response.Documents.Count];
        for (var i = 0; i < response.Documents.Count; i++)
        {
            keys[i] = response.Documents[i].Id;
        }

        if (keys.Length > 0)
        {
            _ = await Database.KeyDeleteAsync(keys).ConfigureAwait(false);
        }
    }

    /// <summary>Cosine distance as the similarity every threshold in this library assumes.</summary>
    /// <param name="distance">RediSearch's <c>vector_score</c>, a cosine distance in [0, 2].</param>
    /// <returns>Cosine similarity in [-1, 1].</returns>
    internal static double ToSimilarity(RedisValue distance) =>
        1 - (double)distance;

    /// <summary>The little-endian FLOAT32 buffer RediSearch expects for a vector.</summary>
    /// <param name="vector">The embedding.</param>
    /// <returns>Its bytes.</returns>
    internal static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        System.Buffers.Binary.BinaryPrimitives.TryWriteInt32LittleEndian(bytes, 0);
        for (var i = 0; i < vector.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>The key one chunk is stored under.</summary>
    /// <param name="documentId">Its document.</param>
    /// <param name="chunkIndex">Its index within that document.</param>
    /// <returns>The Redis key, inside the index's prefix so RediSearch sees it.</returns>
    internal string KeyFor(string documentId, int chunkIndex) =>
        _keyPrefix + documentId + ":" + chunkIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Escapes the characters RediSearch treats as syntax inside a TAG filter, so a document id
    /// containing a hyphen or a colon matches itself rather than parsing as a query.
    /// </summary>
    /// <param name="value">The tag value.</param>
    /// <returns>The escaped value.</returns>
    internal static string EscapeTag(string value)
    {
        var escaped = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                _ = escaped.Append('\\');
            }

            _ = escaped.Append(character);
        }

        return escaped.ToString();
    }

    public void Dispose()
    {
        if (_ownsConnection)
        {
            _redis.Dispose();
        }
    }
}
