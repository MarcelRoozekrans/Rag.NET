using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using Rag.NET.VectorStores;
using StackExchange.Redis;
using SearchResult = Rag.NET.Models.SearchResult;

namespace Rag.NET.VectorStores.Redis;

/// <summary>
/// Redis-backed <see cref="IVectorStore"/> using RediSearch's vector similarity, for teams already
/// operating Redis — the same argument that justifies PgVector: reuse the datastore you run.
/// <para>
/// Chunks are stored as hashes under <c>{prefix}{documentId}:{chunkIndex}</c> and queried with
/// <c>&lt;filter&gt;=&gt;[KNN k @embedding $vec AS vector_score]</c> over an HNSW index, where
/// <c>&lt;filter&gt;</c> is <c>*</c> for an unfiltered search or a TAG pre-filter built from
/// <c>MetadataFilter</c> by <see cref="BuildFilterPrefix"/>.
/// </para>
/// <para>
/// <b>Filtering requires declaring the keys up front, unlike every other store here.</b>
/// RediSearch matches only against attributes its schema names, so a metadata key must be passed
/// as <c>filterableMetadataKeys</c> to a constructor before the index is created — the one
/// configuration step this store alone requires. Every key is still stored and returned regardless
/// of declaration; only filtering on it needs the declaration. See
/// <see cref="VerifyFilterableKeysAreIndexedAsync"/> for what happens when a key is declared after
/// the index already exists.
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
public sealed class RedisVectorStore : IVectorStore, ICollectionManageable, IChunkLookup, IDisposable
{
    /// <summary>The field the KNN clause aliases its distance into.</summary>
    private const string ScoreField = "vector_score";

    private const string EmbeddingField = "embedding";
    private const string TextField = "text";
    private const string DocumentIdField = "document_id";
    private const string ChunkIndexField = "chunk_index";
    private const string MetadataField = "metadata";
    private const string MetadataFieldPrefix = "md_";

    private readonly VectorStoreInitialisationGate _initGate = new();
    private readonly IConnectionMultiplexer _redis;
    private readonly bool _ownsConnection;
    private readonly string _indexName;
    private readonly string _keyPrefix;
    private readonly int _vectorDimensions;
    private readonly IReadOnlyList<string> _filterableKeys;

    /// <summary>Creates a store against a Redis connection string.</summary>
    /// <param name="configuration">A StackExchange.Redis configuration string, e.g. <c>localhost:6379</c>.</param>
    /// <param name="indexName">The RediSearch index to create and query.</param>
    /// <param name="vectorDimensions">The embedding width; must match the generator's.</param>
    /// <param name="filterableMetadataKeys">
    /// Metadata keys that may be used in <c>MetadataFilter</c>. They become case-sensitive TAG
    /// attributes in the index, so they must be known when the index is created.
    /// <b>A filter naming a key that is not declared here throws</b> rather than returning an
    /// unfiltered page — Redis is the only backend in this library that requires the declaration,
    /// because RediSearch filters only on attributes the schema names.
    /// </param>
    public RedisVectorStore(
        string configuration,
        string indexName = "ragnet-idx",
        int vectorDimensions = 1536,
        IReadOnlyList<string>? filterableMetadataKeys = null)
        : this(
            ConnectionMultiplexer.Connect(configuration),
            indexName,
            vectorDimensions,
            ownsConnection: true,
            filterableMetadataKeys)
    {
    }

    /// <summary>Creates a store over a connection the caller owns and disposes.</summary>
    /// <param name="redis">An existing multiplexer — the common case when Redis is already used for caching.</param>
    /// <param name="indexName">The RediSearch index to create and query.</param>
    /// <param name="vectorDimensions">The embedding width; must match the generator's.</param>
    /// <param name="filterableMetadataKeys">
    /// Metadata keys that may be used in <c>MetadataFilter</c>. They become case-sensitive TAG
    /// attributes in the index, so they must be known when the index is created.
    /// <b>A filter naming a key that is not declared here throws</b> rather than returning an
    /// unfiltered page — Redis is the only backend in this library that requires the declaration,
    /// because RediSearch filters only on attributes the schema names.
    /// </param>
    public RedisVectorStore(
        IConnectionMultiplexer redis,
        string indexName = "ragnet-idx",
        int vectorDimensions = 1536,
        IReadOnlyList<string>? filterableMetadataKeys = null)
        : this(redis, indexName, vectorDimensions, ownsConnection: false, filterableMetadataKeys)
    {
    }

    private RedisVectorStore(
        IConnectionMultiplexer redis,
        string indexName,
        int vectorDimensions,
        bool ownsConnection,
        IReadOnlyList<string>? filterableMetadataKeys)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorDimensions, 1);

        _redis = redis;
        _indexName = indexName;
        _keyPrefix = indexName + ":";
        _vectorDimensions = vectorDimensions;
        _ownsConnection = ownsConnection;
        _filterableKeys = filterableMetadataKeys is null
            ? []
            : ValidateFilterableKeys(filterableMetadataKeys);
    }

    /// <summary>
    /// Rejects a declared filterable key that would break silently later instead of loudly now.
    /// </summary>
    /// <remarks>
    /// <c>MetadataFieldName</c> is raw concatenation and the field name is spliced into every
    /// filtered query <b>unescaped</b> (only the value is escaped). A key outside
    /// <c>[A-Za-z0-9_]</c> — <c>tenant-id</c>, say — becomes the attribute <c>md_tenant-id</c>:
    /// <c>FT.CREATE</c> accepts it and the <see cref="VerifyFilterableKeysAreIndexedAsync"/> guard
    /// finds the name and passes, and only then does every filtered query break, because DIALECT 2
    /// reads the hyphen inside <c>@md_tenant-id:{…}</c> as NOT. A duplicate would otherwise fail at
    /// <c>FT.CREATE</c> on first use rather than at construction, and a null or blank key would
    /// silently produce the field <c>md_</c>. Failing here, at construction, is the posture this
    /// design takes everywhere else a bad configuration is detectable up front.
    /// </remarks>
    /// <param name="filterableMetadataKeys">The keys as supplied to the constructor.</param>
    /// <returns>The validated keys, in order.</returns>
    /// <exception cref="ArgumentException">A key is null/whitespace, invalid, or a duplicate.</exception>
    private static string[] ValidateFilterableKeys(IReadOnlyList<string> filterableMetadataKeys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<string>(filterableMetadataKeys.Count);
        foreach (var key in filterableMetadataKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "A filterable metadata key cannot be null or whitespace: it becomes the " +
                    "RediSearch attribute name, and a blank one would produce the field 'md_'.",
                    nameof(filterableMetadataKeys));
            }

            foreach (var character in key)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '_')
                {
                    throw new ArgumentException(
                        $"Filterable metadata key '{key}' contains '{character}', which is " +
                        "outside [A-Za-z0-9_]. The key becomes the RediSearch attribute name " +
                        $"unescaped inside every filtered query — DIALECT 2 would read a " +
                        $"character like '-' as NOT rather than part of the field name.",
                        nameof(filterableMetadataKeys));
                }
            }

            if (!seen.Add(key))
            {
                throw new ArgumentException(
                    $"Filterable metadata key '{key}' is declared more than once.",
                    nameof(filterableMetadataKeys));
            }

            validated.Add(key);
        }

        return [.. validated];
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
            return;
        }

        await VerifyFilterableKeysAreIndexedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Throws when the live index does not declare every configured filterable key.
    /// </summary>
    /// <remarks>
    /// An existing index is never altered or dropped here, so a key added to the configuration
    /// after the index was built would otherwise be silently unfilterable. Failing at
    /// initialisation turns a wrong query result into a startup error naming the key.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A declared key is not an index attribute.</exception>
    private async Task VerifyFilterableKeysAreIndexedAsync()
    {
        if (_filterableKeys.Count == 0)
            return;

        var info = await Database.FT().InfoAsync(_indexName).ConfigureAwait(false);
        // Flattening every value of every attribute map into one set and asking whether md_<key>
        // is present is only safe because no RediSearch type or flag token (TAG, TEXT, SORTABLE,
        // CASESENSITIVE, ...) starts with the "md_" prefix. If MetadataFieldPrefix ever changed to
        // something a schema token could collide with, this check would need to look only at the
        // attribute's name (typically its first value), not its whole value set.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in info.Attributes)
        {
            foreach (var value in attribute.Values)
                _ = declared.Add(value.ToString());
        }

        foreach (var key in _filterableKeys)
        {
            var field = MetadataFieldName(key);
            if (!declared.Contains(field))
            {
                throw new InvalidOperationException(
                    $"Redis index '{_indexName}' does not declare the attribute '{field}', so " +
                    $"filtering on metadata key '{key}' cannot work. The index predates this " +
                    $"configuration; recreate it and re-ingest.");
            }
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
            .AddTextField(TextField);

        foreach (var key in _filterableKeys)
        {
            // caseSensitive: the value is Base64Url-encoded before it is stored as a tag, and
            // Base64Url's alphabet uses both letter cases, so two different values can encode to
            // tokens that are themselves case-variants of one another; a case-folding TAG field
            // would match one against the other. See
            // RedisMetadataFilterTests.ABase64UrlCaseVariantOfAStoredTokenDoesNotMatchIt.
            _ = schema.AddTagField(MetadataFieldName(key), caseSensitive: true);
        }

        _ = schema.AddVectorField(
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

        // Dropping the bound index makes "already initialised" false again.
        if (string.Equals(collectionName, _indexName, StringComparison.Ordinal))
            _initGate.Reset();
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

    /// <summary>Runs <see cref="InitializeAsync"/> once, on the first operation that needs the index (#353).</summary>
    private Task EnsureInitialisedAsync(CancellationToken cancellationToken) =>
        _initGate.EnsureInitialisedAsync(InitializeAsync, cancellationToken);

    /// <inheritdoc />
    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", nameof(RedisVectorStore));
        activity?.SetTag("chunk.count", chunks.Count);

        var database = Database;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var entries = new List<HashEntry>(5 + _filterableKeys.Count)
            {
                new(DocumentIdField, chunk.Chunk.DocumentId.Value),
                new(ChunkIndexField, chunk.Chunk.ChunkIndex),
                new(TextField, chunk.Chunk.Text),
                new(MetadataField, MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata)),
                new(EmbeddingField, ToBytes(chunk.Embedding.Span)),
            };

            List<RedisValue>? stale = null;
            foreach (var key in _filterableKeys)
            {
                if (chunk.Chunk.Metadata.TryGetValue(key, out var value))
                {
                    entries.Add(new HashEntry(MetadataFieldName(key), MetadataToken(value)));
                }
                else
                {
                    stale ??= [];
                    stale.Add(MetadataFieldName(key));
                }
            }

            var hashKey = KeyFor(chunk.Chunk.DocumentId.Value, chunk.Chunk.ChunkIndex);
            await database.HashSetAsync(hashKey, [.. entries]).ConfigureAwait(false);

            // HSET merges rather than replaces: a declared key this chunk does not carry must be
            // deleted explicitly, or a value an earlier write left behind would still match a
            // filter the metadata blob no longer does (#513's re-ingest defect, reintroduced here).
            if (stale is not null)
                _ = await database.HashDeleteAsync(hashKey, [.. stale]).ConfigureAwait(false);
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
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        if (options.TopK <= 0)
        {
            return [];
        }

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", nameof(RedisVectorStore));
        activity?.SetTag("top.k", options.TopK);

        var filterPrefix = BuildFilterPrefix(options.MetadataFilter);
        var query = new Query($"{filterPrefix}=>[KNN {options.TopK.ToString(CultureInfo.InvariantCulture)} @{EmbeddingField} $vec AS {ScoreField}]")
            .AddParam("vec", ToBytes(queryEmbedding.Span))
            .SetSortBy(ScoreField)
            .ReturnFields(DocumentIdField, ChunkIndexField, TextField, MetadataField, ScoreField)
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

            var documentId = document[DocumentIdField].ToString();
            var chunkIndex = (int)document[ChunkIndexField];

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = document[TextField].ToString(),
                    DocumentId = new DocumentId(documentId),
                    ChunkIndex = chunkIndex,
                    Metadata = DecodeMetadata(document[MetadataField], documentId, chunkIndex),
                },
                Score = score,
            });
        }

        activity?.SetTag("result.count", results.Count);
        return results;
    }

    /// <summary>
    /// The RediSearch pre-filter for a metadata filter, or <c>*</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Conditions are AND-ed by juxtaposition, matching <c>SearchOptions.MetadataFilter</c>'s
    /// "matches every key/value pair". Each value is tokenised (kind + Base64Url) and then escaped
    /// for TAG syntax — the token's own colon is syntax inside a query even though nothing else in
    /// it is.
    /// </remarks>
    /// <param name="filter">The requested filter; null or empty means no filtering.</param>
    /// <returns>The query prefix.</returns>
    /// <exception cref="InvalidOperationException">A key was not declared filterable.</exception>
    private string BuildFilterPrefix(IDictionary<string, MetadataValue>? filter)
    {
        if (filter is not { Count: > 0 })
            return "*";

        var clause = new StringBuilder("(");
        var first = true;
        foreach (var pair in filter)
        {
            if (!_filterableKeys.Contains(pair.Key, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Metadata key '{pair.Key}' is not filterable on this Redis index. RediSearch " +
                    $"filters only on attributes the schema declares, so filterable keys are fixed " +
                    $"when the index is created. Declared: " +
                    $"[{string.Join(", ", _filterableKeys)}]. Add '{pair.Key}' to " +
                    $"filterableMetadataKeys and recreate the index.");
            }

            if (!first)
                _ = clause.Append(' ');

            first = false;
            _ = clause.Append('@')
                .Append(MetadataFieldName(pair.Key))
                .Append(":{")
                .Append(EscapeTag(MetadataToken(pair.Value)))
                .Append('}');
        }

        return clause.Append(')').ToString();
    }

    /// <summary>
    /// Returns the chunks for the given keys, read straight from their hashes. Missing keys are
    /// simply absent (#318).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Redis needs no query for this, which makes it the opposite of Qdrant.</b> A chunk's key
    /// <em>is</em> its identity — <see cref="KeyFor"/> composes <c>prefix + documentId + ":" +
    /// chunkIndex</c> — so the lookup is a direct hash read per key and never touches RediSearch.
    /// That also sidesteps the TAG escaping the index path needs: a document id containing a hyphen
    /// or colon is only syntax inside a query, and there is no query here.
    /// </para>
    /// <para>
    /// <b>The reads are issued together and awaited together.</b> StackExchange.Redis pipelines
    /// concurrently-issued commands on one connection, so this is one round trip's worth of latency
    /// for the few-dozen-key batches local search sends, rather than one per key.
    /// </para>
    /// <para>
    /// <b>A missing key returns an empty hash rather than an error</b>, which is the contract: a
    /// document deleted since extraction leaves the graph naming chunks that no longer exist.
    /// </para>
    /// </remarks>
    /// <param name="keys">Chunk identities to fetch.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The chunks that exist, in unspecified order.</returns>
    public async Task<IReadOnlyList<TextChunk>> GetChunksAsync(
        IReadOnlyList<ChunkKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
            return [];

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var database = Database;
        var reads = new Task<HashEntry[]>[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            reads[i] = database.HashGetAllAsync(KeyFor(keys[i].DocumentId, keys[i].ChunkIndex));

        var hashes = await Task.WhenAll(reads).ConfigureAwait(false);

        var chunks = new List<TextChunk>(keys.Count);
        foreach (var hash in hashes)
        {
            if (hash.Length == 0)
                continue;

            chunks.Add(MapChunk(hash));
        }

        return chunks;
    }

    /// <summary>Materialises a chunk from the hash <see cref="StoreAsync"/> wrote.</summary>
    /// <param name="hash">The hash entries for one key.</param>
    /// <returns>The chunk it encodes, including its metadata.</returns>
    private static TextChunk MapChunk(HashEntry[] hash)
    {
        string text = string.Empty;
        string documentId = string.Empty;
        var chunkIndex = 0;
        RedisValue metadata = RedisValue.Null;

        // Plain iteration: HashEntry is not a readonly struct, so a ref-readonly loop copies it
        // on every member access anyway (EPS06).
        foreach (var entry in hash)
        {
            var name = entry.Name.ToString();
            if (string.Equals(name, TextField, StringComparison.Ordinal))
                text = entry.Value.ToString();
            else if (string.Equals(name, DocumentIdField, StringComparison.Ordinal))
                documentId = entry.Value.ToString();
            else if (string.Equals(name, ChunkIndexField, StringComparison.Ordinal))
                chunkIndex = (int)entry.Value;
            else if (string.Equals(name, MetadataField, StringComparison.Ordinal))
                metadata = entry.Value;
        }

        return new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = DecodeMetadata(metadata, documentId, chunkIndex),
        };
    }

    /// <summary>
    /// Decodes the <c>metadata</c> hash field. A <b>missing</b> field is a hash written before this
    /// store persisted metadata and reads as empty; a field that is <b>present and corrupt</b>
    /// throws, because on this store a chunk that reads as having no metadata is indistinguishable
    /// from the defect #513 fixed. Matches <c>WeaviateVectorStore</c>'s reviewed posture (#521).
    /// </summary>
    /// <param name="raw">The raw field value, or a null <see cref="RedisValue"/> when absent.</param>
    /// <param name="documentId">Named in the exception, so a corrupt chunk is findable.</param>
    /// <param name="chunkIndex">Named in the exception.</param>
    /// <returns>The decoded metadata; empty when the field is absent.</returns>
    private static IDictionary<string, MetadataValue> DecodeMetadata(
        RedisValue raw, string documentId, int chunkIndex)
    {
        if (raw.IsNullOrEmpty)
            return new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

        var result = MetadataSerializer.DeserializeMetadata(raw.ToString());
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Redis hash '{documentId}:{chunkIndex.ToString(CultureInfo.InvariantCulture)}' " +
                $"has a corrupt {MetadataField} field.");
        }

        return result.Value;
    }

    /// <inheritdoc />
    public async Task DeleteByDocumentIdAsync(
        string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();
        // Deliberately no first-use initialisation here: a delete has nothing to delete from a
        // collection that does not exist, and provisioning one to satisfy it would be pure waste —
        // on pgvector an inline HNSW build under a write-blocking lock, triggered by a delete (#353).

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
        var escaped = new StringBuilder(value.Length);
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

    /// <summary>The hash field and index attribute a filterable metadata key is stored under.</summary>
    /// <param name="key">The metadata key.</param>
    /// <returns>The namespaced field name, e.g. <c>md_tenant</c>.</returns>
    internal static string MetadataFieldName(string key) => MetadataFieldPrefix + key;

    /// <summary>
    /// The TAG token one filterable metadata value is stored and queried as: its kind, a colon,
    /// then the Base64Url of its canonical text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The kind prefix is the typed half of the contract.</b> <c>SearchOptions.MetadataFilter</c>
    /// matches typed — a filter of <c>3</c> must not match the string <c>"3"</c> — and without the
    /// prefix both render as <c>3</c>.
    /// </para>
    /// <para>
    /// <b>The value is encoded, not escaped, because a TAG field splits on a separator</b>
    /// (<c>,</c> by default). No separator character is safe when a value can contain any
    /// character, and Base64Url's alphabet contains none of them. Dropping the encoding is the
    /// simplification that passes every test whose values are well-behaved words.
    /// </para>
    /// <para>
    /// <b>The text comes from <see cref="MetadataValue.ToString"/></b>, which already emits
    /// invariant numbers, <c>true</c>/<c>false</c>, and the shared date format — so the write path
    /// and the filter path are one accessor and cannot drift.
    /// </para>
    /// </remarks>
    /// <param name="value">The metadata value.</param>
    /// <returns>The token.</returns>
    internal static string MetadataToken(MetadataValue value)
    {
        var kind = value.Kind switch
        {
            MetadataValueKind.Number => 'n',
            MetadataValueKind.Boolean => 'b',
            MetadataValueKind.DateTimeOffset => 'd',
            _ => 's',
        };

        return $"{kind}:{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value.ToString()))}";
    }

    public void Dispose()
    {
        _initGate.Dispose();
        if (_ownsConnection)
        {
            _redis.Dispose();
        }
    }
}
