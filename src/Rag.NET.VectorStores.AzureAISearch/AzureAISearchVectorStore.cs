using System.Globalization;
using System.Text;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;
using Rag.NET.VectorStores;
using RagSearchOptions = Rag.NET.Models.Options.SearchOptions;

namespace Rag.NET.AzureAISearch;

public sealed class AzureAISearchVectorStore : IVectorStore, IHybridSearchable, ICollectionManageable, IChunkLookup, IScoreScaleAware, IDisposable
{
    private const int SearchPageSize = 1000; // Azure AI Search maximum
    private const int DeleteBatchSize = 1000; // Azure AI Search maximum

    /// <summary>
    /// Name of the one semantic configuration this store ever builds. An implementation detail,
    /// not a knob: the store defines the fields, so it names the configuration built from them.
    /// </summary>
    private const string SemanticConfigurationName = "ragnet-semantic-configuration";

    private readonly VectorStoreInitialisationGate _initGate = new();
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly string _indexName;
    private readonly int _vectorDimensions;
    private readonly int? _kNearestNeighborsCount;
    private readonly bool _semanticRankingEnabled;

    public AzureAISearchVectorStore(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions = 1536)
        : this(endpoint, indexName, credential, vectorDimensions, clientOptions: null)
    {
    }

    public AzureAISearchVectorStore(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions,
        SearchClientOptions? clientOptions)
        : this(endpoint, indexName, credential, vectorDimensions, clientOptions, options: null)
    {
    }

    /// <summary>
    /// Creates the store with <see cref="AzureAISearchOptions"/>, the overload <c>UseAzureAISearch</c>
    /// calls once its <c>configure</c> callback has run.
    /// </summary>
    /// <param name="endpoint">The search service endpoint.</param>
    /// <param name="indexName">The index this store reads and writes.</param>
    /// <param name="credential">The admin or query key.</param>
    /// <param name="vectorDimensions">The embedding width the index is created with.</param>
    /// <param name="clientOptions">Optional SDK client options.</param>
    /// <param name="options">
    /// Store options, or <see langword="null"/> for their defaults. The other constructors pass
    /// <see langword="null"/>, so they keep Azure's own <c>k</c> rather than any value of ours.
    /// </param>
    public AzureAISearchVectorStore(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions,
        SearchClientOptions? clientOptions,
        AzureAISearchOptions? options)
    {
        _indexClient = clientOptions is null
            ? new SearchIndexClient(endpoint, credential)
            : new SearchIndexClient(endpoint, credential, clientOptions);
        _searchClient = clientOptions is null
            ? new SearchClient(endpoint, indexName, credential)
            : new SearchClient(endpoint, indexName, credential, clientOptions);
        _indexName = indexName;
        _vectorDimensions = vectorDimensions;
        _kNearestNeighborsCount = options?.KNearestNeighborsCount;
        _semanticRankingEnabled = options?.EnableSemanticRanking ?? false;
    }

    /// <summary>
    /// Creates or updates the bound index. Idempotent — <c>CreateOrUpdateIndex</c> is a PUT — and
    /// <b>optional</b>: every entry point below calls <see cref="EnsureInitialisedAsync"/> first,
    /// so a caller who never calls this still gets a working first ingest (#353). Deliberately
    /// ungated, so calling it explicitly always reaches the service — that is what makes it usable
    /// to re-create the index after <see cref="DeleteCollectionAsync"/>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _indexClient.CreateOrUpdateIndexAsync(
            BuildIndex(_indexName, _vectorDimensions, _semanticRankingEnabled),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <see cref="InitializeAsync"/> once, on the first operation that needs the index.
    /// </summary>
    private Task EnsureInitialisedAsync(CancellationToken cancellationToken) =>
        _initGate.EnsureInitialisedAsync(InitializeAsync, cancellationToken);

    public void Dispose() => _initGate.Dispose();

    /// <inheritdoc />
    /// <remarks>
    /// Fixed at construction, as the interface requires. With semantic ranking on, this store
    /// returns Azure's <c>RerankerScore</c>, which is a relevance rank rather than a similarity;
    /// with it off, the dense path's cosine similarity is exactly what the default assumes.
    /// </remarks>
    public ScoreScale ScoreScale =>
        _semanticRankingEnabled ? ScoreScale.OpaqueRanking : ScoreScale.Similarity;

    /// <summary>
    /// The index schema. Metadata lives in two fields:
    /// <list type="bullet">
    /// <item><description><c>metadata_entries</c> — a <c>Collection(Edm.ComplexType)</c> of
    /// <c>{key, stringValue, numberValue, boolValue, dateValue}</c> rows, one per metadata key,
    /// with the sub-field matching the value's <see cref="MetadataValueKind"/> populated. This is
    /// what makes every metadata key filterable with its type and no per-key schema:
    /// <c>metadata_entries/any(m: m/key eq 'page' and m/numberValue eq 3)</c>.</description></item>
    /// <item><description><c>metadata</c> — the legacy single-string JSON blob. Still declared
    /// and written because Azure AI Search forbids removing or re-typing an existing field:
    /// an index created before <c>metadata_entries</c> existed can only be updated
    /// <i>additively</i>, so the schema must keep carrying the old field for
    /// <c>CreateOrUpdateIndexAsync</c> to succeed against it. It doubles as the read fallback
    /// for documents ingested before the complex collection existed.</description></item>
    /// </list>
    /// Sub-fields of a complex collection cannot be marked <c>sortable</c> (service constraint:
    /// they are multi-valued per document), so none are.
    /// </summary>
    private static SearchIndex BuildIndex(string name, int vectorDimensions, bool semanticRankingEnabled)
    {
        var metadataEntries = new ComplexField("metadata_entries", collection: true);
        metadataEntries.Fields.Add(new SimpleField("key", SearchFieldDataType.String) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("stringValue", SearchFieldDataType.String) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("numberValue", SearchFieldDataType.Double) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("boolValue", SearchFieldDataType.Boolean) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("dateValue", SearchFieldDataType.DateTimeOffset) { IsFilterable = true });

        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunk_index", SearchFieldDataType.Int32),
            new SearchableField("text"),
            new SimpleField("metadata", SearchFieldDataType.String),
            metadataEntries,
            new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = vectorDimensions,
                VectorSearchProfileName = "default-profile",
            },
        };

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));

        var index = new SearchIndex(name)
        {
            Fields = fields,
            VectorSearch = vectorSearch,
        };

        // Added only when the ranker is on: a configuration nothing uses is clutter, and adding
        // it unconditionally would rewrite every existing index on the next initialisation for no
        // benefit (Azure has no stale-index hazard — CreateOrUpdateIndexAsync always reconciles).
        if (semanticRankingEnabled)
        {
            var semanticSearch = new SemanticSearch();
            semanticSearch.Configurations.Add(new SemanticConfiguration(
                SemanticConfigurationName,
                new SemanticPrioritizedFields
                {
                    ContentFields = { new SemanticField("text") },
                }));
            index.SemanticSearch = semanticSearch;
        }

        return index;
    }

    /// <summary>Uploads the chunks, and returns as soon as the service has accepted them.</summary>
    /// <remarks>
    /// <b>This does not wait for the documents to become searchable.</b> It used to: every call
    /// ended with an unconditional one-second sleep, commented "Azure AI Search indexing is near
    /// real-time; brief wait for consistency". That sleep bought nothing. Azure gives no
    /// read-after-write guarantee at any fixed delay, so one second was a guess that could be too
    /// short and was usually pure waste — and because <c>StorageBehavior</c> calls this once per
    /// document, a 500-page ingest spent over eight minutes asleep.
    /// <para>
    /// Read-after-write is a <i>read</i> concern and cannot be fixed on the write path. A caller
    /// that must observe what it just wrote should poll for the condition it actually needs, the
    /// way <c>PineconeVectorStore.WaitUntilReadyAsync</c> does — bounded, and failing loudly on
    /// expiry rather than continuing on the assumption that a sleep was long enough.
    /// </para>
    /// </remarks>
    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var documents = chunks.Select(chunk => new SearchDocument(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = DocumentKey((string)chunk.Chunk.DocumentId, chunk.Chunk.ChunkIndex),
            ["document_id"] = chunk.Chunk.DocumentId,
            ["chunk_index"] = chunk.Chunk.ChunkIndex,
            ["text"] = chunk.Chunk.Text,
            // Both metadata representations are written: the typed complex collection is what
            // filters run against; the JSON blob keeps documents readable by pre-complex-schema
            // readers and is the read fallback for pre-migration documents.
            ["metadata"] = MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata),
            ["metadata_entries"] = BuildMetadataEntries(chunk.Chunk.Metadata),
            ["embedding"] = chunk.Embedding.ToArray(),
        })).ToList();

        var batch = IndexDocumentsBatch.Upload(documents);
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The document key for a chunk: <c>(documentId, chunkIndex)</c> encoded so that equal chunks
    /// produce equal keys and different chunks never collide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This key used to be <c>Guid.NewGuid()</c>, which made every write an insert (#517).</b>
    /// Azure AI Search's <c>Upload</c> action replaces the document with the given key, so a random
    /// key meant re-ingesting a document added a second copy of every chunk rather than replacing
    /// it — measured: storing one chunk twice left two searchable. Deriving the key makes
    /// <c>Upload</c> the upsert every other backend in this repository already performs.
    /// </para>
    /// <para>
    /// <b>Base64Url, not <c>documentId + ":" + chunkIndex</c>.</b> Azure AI Search accepts only
    /// letters, digits, <c>_</c>, <c>-</c> and <c>=</c> in a key, so a document id containing a
    /// slash, a colon, a space or any non-ASCII character cannot be used raw — the service rejects
    /// the write. Base64Url's alphabet is exactly the permitted set, so encoding the whole composite
    /// is uniformly safe rather than safe-for-the-ids-someone-happened-to-test. The identity is
    /// still stored in the <c>document_id</c> and <c>chunk_index</c> fields and read back from
    /// there, so nothing decodes this.
    /// </para>
    /// <para>
    /// <b>The separator is inside the encoding, and the pairing is injective.</b> A chunk index has
    /// no <c>\n</c> in its decimal form, so the last newline in the composite always separates the
    /// two parts — meaning distinct <c>(documentId, chunkIndex)</c> pairs always produce distinct
    /// bytes, even when a document id itself contains a newline. Getting this wrong would silently
    /// merge two chunks into one document, which no test that stores one chunk can see.
    /// </para>
    /// </remarks>
    /// <param name="documentId">The owning document.</param>
    /// <param name="chunkIndex">Position within that document; may be negative.</param>
    /// <returns>A key valid for Azure AI Search.</returns>
    internal static string DocumentKey(string documentId, int chunkIndex)
    {
        var composite = documentId + "\n" + chunkIndex.ToString(CultureInfo.InvariantCulture);
        return System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(composite));
    }

    /// <summary>
    /// Returns the chunks for the given keys, fetched by document key. Missing keys are simply
    /// absent (#318).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No OData filter, which is what makes this testable at all.</b> Before #517 the document
    /// key was random, so a keyed read here would have had to filter on <c>document_id</c> and
    /// <c>chunk_index</c> — and <c>chunk_index</c> is declared without <c>IsFilterable</c>, so the
    /// service would reject it. Deriving the key turns the lookup into a direct
    /// <c>GetDocument</c>, which also sidesteps the local simulator's incomplete filter support.
    /// </para>
    /// <para>
    /// <b>A missing key is a 404, and a 404 is not an error here.</b> A document deleted since
    /// extraction leaves the graph naming chunks that no longer exist, and that is not a reason to
    /// fail a query. Every other status still throws.
    /// </para>
    /// <para>
    /// <b>The reads are issued together.</b> One request per key, awaited as a batch, rather than a
    /// serial loop over the few dozen keys local search sends.
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

        var reads = new Task<SearchDocument?>[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            reads[i] = GetDocumentOrNullAsync(DocumentKey(keys[i].DocumentId, keys[i].ChunkIndex), cancellationToken);

        var documents = await Task.WhenAll(reads).ConfigureAwait(false);

        var chunks = new List<TextChunk>(keys.Count);
        foreach (var document in documents)
        {
            if (document is null)
                continue;

            chunks.Add(MapChunk(document));
        }

        return chunks;
    }

    /// <summary>Fetches one document, answering <see langword="null"/> when it does not exist.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document, or <see langword="null"/> on 404.</returns>
    private async Task<SearchDocument?> GetDocumentOrNullAsync(
        string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _searchClient
                .GetDocumentAsync<SearchDocument>(key, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        RagSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = options.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    BuildVectorQuery(queryEmbedding, _kNearestNeighborsCount),
                },
            },
        };

        searchOptions.Filter = BuildMetadataFilter(options.MetadataFilter);

        if (_semanticRankingEnabled)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = SemanticConfigurationName,
            };
        }

        var results = await ExecuteSearchAsync(
                null, searchOptions, options.MinScore, expectRerankerScore: _semanticRankingEnabled, cancellationToken)
            .ConfigureAwait(false);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    /// <summary>
    /// Runs Azure AI Search's own hybrid query: BM25 over <paramref name="textQuery"/> fused with
    /// vector search over <paramref name="queryEmbedding"/>, ranked service-side.
    /// </summary>
    /// <remarks>
    /// <b><see cref="RagSearchOptions.MinScore"/> is not applied on this path.</b> The fused score
    /// Azure returns here is a rank produced by combining the BM25 and vector rankings server-side
    /// (<see cref="IHybridSearchable.HybridScoreScale"/> is <see cref="ScoreScale.OpaqueRanking"/>
    /// for exactly this reason) — its magnitude carries no similarity meaning, so thresholding it
    /// like a cosine score would drop or keep results arbitrarily. <see cref="SearchAsync"/> above
    /// still applies <c>MinScore</c>, because there the score is a genuine cosine similarity. The
    /// retrieval pipeline already avoids this trap for pipeline callers — it refuses the native
    /// hybrid dispatch whenever a <c>MinScore</c> is set — so the caller who can still see this is
    /// one reaching <see cref="HybridSearchAsync"/> directly.
    /// </remarks>
    public async Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        RagSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);
        activity?.SetTag("vectorstore.hybrid", true);

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = options.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    BuildVectorQuery(queryEmbedding, _kNearestNeighborsCount),
                },
            },
            QueryType = SearchQueryType.Simple,
            SearchMode = SearchMode.Any,
        };

        searchOptions.Filter = BuildMetadataFilter(options.MetadataFilter);

        // MinScore is deliberately not forwarded: Azure fuses BM25 and vector rankings
        // service-side and the resulting score is ordinal (IHybridSearchable.HybridScoreScale),
        // so a similarity-shaped threshold would filter it arbitrarily. The dense path above
        // still applies it, because there the score is a real cosine similarity.
        var results = await ExecuteSearchAsync(
                textQuery, searchOptions, minScore: 0.0, expectRerankerScore: false, cancellationToken)
            .ConfigureAwait(false);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default) 
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);
        // Deliberately no first-use initialisation here: a delete has nothing to delete from a
        // collection that does not exist, and provisioning one to satisfy it would be pure waste —
        // on pgvector an inline HNSW build under a write-blocking lock, triggered by a delete (#353).

        var idsToDelete = new List<string>();
        var searchOptions = new SearchOptions
        {
            Filter = $"document_id eq '{EscapeODataString(documentId)}'",
            Select = { "id" },
            Size = SearchPageSize,
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchOptions, cancellationToken).ConfigureAwait(false);

        await foreach (var result in response.Value.GetResultsAsync()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            idsToDelete.Add(result.Document.GetString("id"));
        }

        foreach (var chunk in idsToDelete.Chunk(DeleteBatchSize))
        {
            var batch = IndexDocumentsBatch.Delete("id", chunk);
            await _searchClient.IndexDocumentsAsync(
                batch,
                new IndexDocumentsOptions { ThrowOnAnyError = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        await _indexClient.CreateOrUpdateIndexAsync(
            BuildIndex(name, vectorDimensions, _semanticRankingEnabled),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await _indexClient.DeleteIndexAsync(name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Delete-of-missing is a no-op (the ICollectionManageable contract). The service
            // answers 404 for an absent index, which the SDK raises. The local simulator used
            // by the tests returns success instead, so this guard is verified against the
            // documented service behaviour rather than by the container suite.
        }

        // Dropping the bound index makes "already initialised" false again; without this the
        // next write would target an index that no longer exists. Matches Chroma, which nulls
        // its cached collection id on exactly this condition.
        if (string.Equals(name, _indexName, StringComparison.Ordinal))
            _initGate.Reset();
    }

    public async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await _indexClient.GetIndexAsync(name, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Escapes a string for use in an OData string literal by doubling single quotes.</summary>
    internal static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// One <c>metadata_entries/any(...)</c> clause per filter pair, AND-composed. The clause
    /// compares the sub-field matching the filter value's <see cref="MetadataValueKind"/>, so a
    /// Number filter of <c>3</c> matches metadata stored as the number 3 and never the string
    /// <c>"3"</c>. Documents ingested before the complex collection existed have no
    /// <c>metadata_entries</c> rows and therefore do not match — re-ingest them to filter on them.
    /// </summary>
    /// <summary>
    /// Builds the vector arm's query, sending <c>k</c> only when the caller configured one.
    /// </summary>
    /// <remarks>
    /// <b>Leaving <c>KNearestNeighborsCount</c> unset is a decision, not an omission.</b> Microsoft
    /// documents the unspecified default as 50; this store previously sent <c>TopK</c>, which at a
    /// typical top-5 asked for a tenth of that and starved RRF fusion of candidates to fuse.
    /// Sending nothing restores the platform default and lets a caller widen it through
    /// <see cref="AzureAISearchOptions.KNearestNeighborsCount"/> — notably to the 50 that semantic
    /// ranking requires (#328).
    /// </remarks>
    /// <param name="queryEmbedding">The query vector.</param>
    /// <param name="kNearestNeighborsCount">The configured <c>k</c>, or <see langword="null"/>.</param>
    /// <returns>The query to add to <c>VectorSearchOptions.Queries</c>.</returns>
    internal static VectorizedQuery BuildVectorQuery(
        ReadOnlyMemory<float> queryEmbedding, int? kNearestNeighborsCount)
    {
        var query = new VectorizedQuery(queryEmbedding) { Fields = { "embedding" } };
        if (kNearestNeighborsCount is { } k)
        {
            query.KNearestNeighborsCount = k;
        }

        return query;
    }

    internal static string? BuildMetadataFilter(IDictionary<string, MetadataValue>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        var clauses = metadataFilter.Select(kvp =>
            "metadata_entries/any(m: m/key eq " +
            $"'{EscapeODataString(kvp.Key)}' and m/{SubFieldName(kvp.Value.Kind)} eq {FormatODataLiteral(kvp.Value)})");
        return string.Join(" and ", clauses);
    }

    private static string SubFieldName(MetadataValueKind kind) => kind switch
    {
        MetadataValueKind.Number => "numberValue",
        MetadataValueKind.Boolean => "boolValue",
        MetadataValueKind.DateTimeOffset => "dateValue",
        _ => "stringValue",
    };

    /// <summary>
    /// The filter value as an OData literal. Strings are quoted and escaped; numbers, booleans
    /// and dates use <see cref="MetadataValue.ToString"/>, whose invariant forms (<c>3</c>,
    /// <c>true</c>, <c>2026-01-01T00:00:00.0000000Z</c>) are already valid OData literals.
    /// </summary>
    private static string FormatODataLiteral(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.String => $"'{EscapeODataString(value.StringValue)}'",
        _ => value.ToString(),
    };

    /// <summary>One complex-collection row per metadata key, populating the sub-field for its kind.</summary>
    private static List<SearchDocument> BuildMetadataEntries(IDictionary<string, MetadataValue> metadata)
    {
        var entries = new List<SearchDocument>(metadata.Count);
        foreach (var (key, value) in metadata)
        {
            var entry = new SearchDocument { ["key"] = key };
            switch (value.Kind)
            {
                case MetadataValueKind.Number:
                    entry["numberValue"] = value.NumberValue;
                    break;
                case MetadataValueKind.Boolean:
                    entry["boolValue"] = value.BooleanValue;
                    break;
                case MetadataValueKind.DateTimeOffset:
                    entry["dateValue"] = value.DateTimeOffsetValue;
                    break;
                default:
                    entry["stringValue"] = value.StringValue;
                    break;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Runs the query and maps results, shared by <see cref="SearchAsync"/> and
    /// <see cref="HybridSearchAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="expectRerankerScore"/> is passed in, never read from
    /// <c>_semanticRankingEnabled</c> here.</b> The field configures the dense path; the hybrid
    /// path always passes <see langword="false"/> regardless of it, because stacking Azure's
    /// reranker on top of its own hybrid fusion raises a separate question — what score comes back
    /// from ranking an already-fused result — that this store does not yet answer. Reading the
    /// field in the shared method would let enabling the ranker silently change hybrid results too.
    /// </para>
    /// <para>
    /// <b>A service that cannot rank answers successfully rather than failing.</b> Azure returns
    /// HTTP 200 with ordinary results whenever the tier is below Basic, the region does not support
    /// semantic ranking, or the configuration name does not match — there is no error to catch, only
    /// a missing <c>RerankerScore</c> on results that look otherwise normal. When
    /// <paramref name="expectRerankerScore"/> is set and that score is absent, this throws rather
    /// than silently handing back ordinary scores dressed up as reranked ones: the absence is the
    /// only signal the defect leaves, so it has to be treated as an error, not a missing optional.
    /// </para>
    /// <para>
    /// <b><paramref name="minScore"/> is not applied when <paramref name="expectRerankerScore"/> is
    /// set.</b> The reranker score is Azure's ~0-4 ordinal relevance score, not a similarity — the
    /// same rule 6.2.33 established for the hybrid path's fused score — so thresholding it as if it
    /// were a cosine similarity would drop or keep results arbitrarily.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<SearchResult>> ExecuteSearchAsync(
        string? searchText,
        Azure.Search.Documents.SearchOptions searchOptions,
        double minScore,
        bool expectRerankerScore,
        CancellationToken cancellationToken)
    {
        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchText, searchOptions, cancellationToken).ConfigureAwait(false);

        var results = new List<SearchResult>();

        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            double score;
            if (expectRerankerScore)
            {
                score = result.SemanticSearch?.RerankerScore
                    ?? throw new InvalidOperationException(
                        $"Semantic ranking was requested for index '{_indexName}', but the service " +
                        "returned no reranker score. This means the service did not actually perform " +
                        "semantic ranking (the tier, region, or configuration does not support it), " +
                        "and results would be ordinary scores presented as reranked.");
            }
            else
            {
                score = result.Score ?? 0.0;
                if (score < minScore)
                {
                    continue;
                }
            }

            results.Add(new SearchResult
            {
                Chunk = MapChunk(result.Document),
                Score = score,
            });
        }

        return results;
    }

    /// <summary>
    /// Materialises a chunk from a stored document. Shared by search and by the keyed lookup so the
    /// two cannot drift on what a stored chunk is — the identity is read from the
    /// <c>document_id</c> and <c>chunk_index</c> fields, never decoded back out of the key.
    /// </summary>
    /// <param name="document">The stored document.</param>
    /// <returns>The chunk it encodes.</returns>
    private static TextChunk MapChunk(SearchDocument document) => new()
    {
        DocumentId = new DocumentId(document.GetString("document_id")),
        ChunkIndex = document.GetInt32("chunk_index") ?? 0,
        Text = document.GetString("text"),
        Metadata = ReadMetadata(document),
    };

    /// <summary>
    /// Metadata from the typed <c>metadata_entries</c> collection when the document has rows
    /// there; otherwise from the legacy <c>metadata</c> JSON blob — the path every document
    /// ingested before the complex collection existed takes, where all values read back as
    /// <see cref="MetadataValueKind.String"/> (exactly what was stored).
    /// </summary>
    private static Dictionary<string, MetadataValue> ReadMetadata(SearchDocument document)
    {
        if (TryReadMetadataEntries(document) is { } typed)
            return typed;

        var documentId = document.GetString("document_id");
        var chunkIndex = document.GetInt32("chunk_index") ?? 0;
        return MetadataSerializer.DeserializeMetadataOrThrow(
            document.GetString("metadata"),
            $"Azure AI Search index entry (document '{documentId}', chunk {chunkIndex}), legacy metadata field");
    }

    private static Dictionary<string, MetadataValue>? TryReadMetadataEntries(SearchDocument document)
    {
        if (!document.TryGetValue("metadata_entries", out var raw) || raw is not IEnumerable<object> items)
            return null;

        Dictionary<string, MetadataValue>? metadata = null;
        foreach (var item in items)
        {
            metadata ??= new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            if (item is not SearchDocument entry || entry.GetString("key") is not { } key)
                continue;
            metadata[key] = ReadEntryValue(entry);
        }

        // No rows at all means "written before the complex collection existed" — signal the
        // caller to fall back to the blob. (A new document with empty metadata also lands here;
        // its blob is "{}", so both paths agree on the empty dictionary.)
        return metadata;
    }

    private static MetadataValue ReadEntryValue(SearchDocument entry)
    {
        // The SDK's dynamic deserializer maps a JSON number to int/long/double by shape, and
        // Edm.Double serialises whole numbers without a fractional part — so 3.0 written to
        // numberValue can come back as an int 3. Accept every numeric shape, not just double.
        if (entry.TryGetValue("numberValue", out var number))
        {
            switch (number)
            {
                case double d: return d;
                case float f: return (double)f;
                case int i: return (double)i;
                case long l: return (double)l;
            }
        }
        if (entry.TryGetValue("boolValue", out var boolean) && boolean is bool b)
            return b;
        // Edm.DateTimeOffset usually deserialises to DateTimeOffset, but a service (or the
        // local simulator) can hand the ISO-8601 text back as a plain string — same instant,
        // different shape. Parse it rather than letting a real date degrade to a string kind.
        if (entry.TryGetValue("dateValue", out var date))
        {
            if (date is DateTimeOffset dto)
                return dto;
            if (date is string dateText && DateTimeOffset.TryParse(
                    dateText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
                return parsed;
        }
        // The SDK's dynamic deserializer turns ISO-8601-looking strings into DateTimeOffset even
        // in stringValue; normalise such a value back to its textual form rather than dropping it.
        if (entry.TryGetValue("stringValue", out var text) && text is not null)
        {
            return text switch
            {
                string s => s,
                DateTimeOffset dtoText => MetadataDateFormat.Format(dtoText),
                _ => text.ToString() ?? string.Empty,
            };
        }

        return string.Empty;
    }
}
