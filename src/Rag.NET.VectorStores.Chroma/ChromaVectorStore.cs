using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.Chroma;

/// <summary>
/// Chroma-backed <see cref="IVectorStore"/> with collection lifecycle management
/// (<see cref="ICollectionManageable"/>) — deliberately the lightweight dense-only adapter
/// (no native hybrid/sparse search; use Qdrant or Weaviate for that). Transport is the
/// Chroma REST v2 API. Backend and transport errors throw — the pipeline layer owns
/// degradation (house store posture).
/// <para>
/// Record ids are <c>{documentId}:{chunkIndex}</c>, so re-storing a chunk upserts
/// (replaces) it. Collections are created with the cosine space; score semantics (pinned
/// 2026-07-26 against Chroma 1.0.0): the query endpoint returns cosine
/// <c>distance = 1 - cosine similarity</c> in [0, 2] where 0 is identical, mapped to
/// <c>Score = 1 - distance</c> (identical vector ⇒ 1, orthogonal ⇒ 0, opposite ⇒ -1).
/// <c>MinScore</c> is applied to the converted score, <c>TopK</c> becomes
/// <c>n_results</c>.
/// </para>
/// <para>
/// Chroma addresses records by collection UUID, so the configured collection name is
/// resolved (creating the collection when missing) and cached. A 404 during a record
/// operation means the collection was deleted or recreated behind the store's back: the
/// cache is invalidated and re-resolved once, then the operation is retried; a second
/// failure throws.
/// </para>
/// </summary>
public sealed class ChromaVectorStore : IVectorStore, ICollectionManageable, IDisposable
{
    private static readonly FrozenDictionary<string, string> CosineSpaceMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["hnsw:space"] = "cosine" }
            .ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] IncludeSections = ["documents", "metadatas", "distances"];

    private readonly IChromaApi _api;
    private readonly ChromaOptions _options;
    private readonly HttpClient? _ownedHttpClient;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private string? _collectionId;

    public ChromaVectorStore(ChromaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Endpoint is null)
            throw new ArgumentException("ChromaOptions.Endpoint must be set.", nameof(options));
        _options = options;
        _ownedHttpClient = CreateHttpClient(options);
        _api = new ChromaApiClient(_ownedHttpClient, new ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer());
    }

    /// <summary>Test seam: inject a fake <see cref="IChromaApi"/>.</summary>
    internal ChromaVectorStore(IChromaApi api, ChromaOptions options)
    {
        _api = api;
        _options = options;
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.CollectionName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        if (chunks.Count == 0)
            return;

        var request = BuildUpsertRequest(chunks);
        await ExecuteOnCollectionAsync(
            "upsert",
            collectionId => _api.UpsertRecordsAsync(
                _options.Tenant, _options.Database, collectionId, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.CollectionName);

        var request = new ChromaQueryRequest
        {
            QueryEmbeddings = [queryEmbedding.ToArray()],
            NResults = options.TopK,
            Where = BuildWhereFilter(options.MetadataFilter),
            Include = IncludeSections,
        };
        var response = await ExecuteOnCollectionAsync(
            "query",
            collectionId => _api.QueryAsync(
                _options.Tenant, _options.Database, collectionId, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var results = MapResults(response, options.MinScore);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.CollectionName);

        var request = new ChromaDeleteRecordsRequest
        {
            Where = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["document_id"] = EqualityClause(documentId),
            },
        };
        await ExecuteOnCollectionAsync(
            $"delete records of document '{documentId}'",
            collectionId => _api.DeleteRecordsAsync(
                _options.Tenant, _options.Database, collectionId, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a collection with the cosine space. <paramref name="vectorDimensions"/> is
    /// validated for interface-contract sanity but not sent: the Chroma v2 create-collection
    /// payload has no dimension field (verified against the 1.0.0 OpenAPI document) —
    /// Chroma infers dimensions from the first upsert.
    /// </summary>
    public async Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorDimensions);

        var result = await _api.CreateCollectionAsync(
            _options.Tenant,
            _options.Database,
            new ChromaCreateCollectionRequest { Name = name, Metadata = CosineSpaceMetadata },
            cancellationToken).ConfigureAwait(false);
        Unwrap(result, $"create collection '{name}'");
    }

    public async Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var result = await _api.DeleteCollectionAsync(
            _options.Tenant, _options.Database, name, cancellationToken).ConfigureAwait(false);
        // Delete-of-missing is a no-op (the ICollectionManageable contract, matching
        // Weaviate's server-idempotent delete and PgVector's DROP TABLE IF EXISTS) —
        // Chroma itself answers 404, so exactly that case is swallowed; other failures throw.
        if (result.IsFailure && result.Error.StatusCode != HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Chroma delete collection '{name}' failed with {FormatHttpError(result.Error)}.");
        }

        // Deleting the configured collection through this store makes any cached UUID stale.
        if (string.Equals(name, _options.CollectionName, StringComparison.Ordinal))
            Volatile.Write(ref _collectionId, null);
    }

    public async Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var result = await _api.GetCollectionAsync(
            _options.Tenant, _options.Database, name, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            return true;
        if (result.Error.StatusCode == HttpStatusCode.NotFound)
            return false;
        throw new InvalidOperationException(
            $"Chroma collection probe for '{name}' failed with {FormatHttpError(result.Error)}.");
    }

    public void Dispose()
    {
        _resolveLock.Dispose();
        _ownedHttpClient?.Dispose();
    }

    /// <summary>Record id per <c>(DocumentId, ChunkIndex)</c> — plain string, upsert = replace.</summary>
    internal static string RecordId(string documentId, int chunkIndex) =>
        FormattableString.Invariant($"{documentId}:{chunkIndex}");

    /// <summary>
    /// Runs a record operation against the resolved collection UUID, recovering ONCE from a
    /// stale cache: a 404 means the collection was deleted/recreated behind our back (the
    /// 404 names the UUID), so the cache is invalidated, re-resolved, and the operation is
    /// retried — a second failure throws.
    /// </summary>
    private async Task<T> ExecuteOnCollectionAsync<T>(
        string operation,
        Func<string, Task<Result<T, HttpError>>> send,
        CancellationToken cancellationToken)
    {
        var collectionId = await ResolveCollectionIdAsync(cancellationToken).ConfigureAwait(false);
        var result = await send(collectionId).ConfigureAwait(false);
        if (result.IsFailure && result.Error.StatusCode == HttpStatusCode.NotFound)
        {
            // Only clear the cache if it still holds the id we just found stale, so a
            // concurrent re-resolution is never thrown away.
            Interlocked.CompareExchange(ref _collectionId, null, collectionId);
            collectionId = await ResolveCollectionIdAsync(cancellationToken).ConfigureAwait(false);
            result = await send(collectionId).ConfigureAwait(false);
        }

        return Unwrap(result, $"{operation} against collection '{_options.CollectionName}'");
    }

    /// <summary>
    /// Resolves the configured collection name to its UUID and caches it. Uses a single
    /// <c>get_or_create: true</c> POST (returns the existing collection or creates it with
    /// the cosine space), so concurrent first writes — even across processes — can never
    /// race an exists-probe into a spurious 409.
    /// </summary>
    private async Task<string> ResolveCollectionIdAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _collectionId) is { } cached)
            return cached;

        await _resolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _collectionId) is { } resolved)
                return resolved;

            var result = await _api.CreateCollectionAsync(
                _options.Tenant,
                _options.Database,
                new ChromaCreateCollectionRequest
                {
                    Name = _options.CollectionName,
                    Metadata = CosineSpaceMetadata,
                    GetOrCreate = true,
                },
                cancellationToken).ConfigureAwait(false);
            var collection = Unwrap(result, $"resolve collection '{_options.CollectionName}'");
            ThrowIfNotCosine(collection);
            Volatile.Write(ref _collectionId, collection.Id);
            return collection.Id;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    /// <summary>
    /// Honest-capability guard: <c>get_or_create</c> returns a pre-existing collection
    /// unchanged, so a collection created elsewhere with another space (Chroma's default is
    /// squared L2) would silently put <c>Score = 1 - distance</c> on the wrong scale and
    /// break <c>MinScore</c>. Fail fast instead, naming the actual space and the fix
    /// (Qdrant sparse bootstrap precedent).
    /// </summary>
    private void ThrowIfNotCosine(ChromaCollectionInfo collection)
    {
        var space = collection.Configuration?.Hnsw?.Space;
        // A null space passes deliberately: lenient toward servers/versions that omit
        // configuration_json.hnsw.space. Trade-off: a future key rename would silently
        // disable this guard — the :latest container suite's non-cosine fail-fast test is
        // the drift alarm.
        if (space is null || string.Equals(space, "cosine", StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"Chroma collection '{_options.CollectionName}' uses the '{space}' distance space, " +
            "but ChromaVectorStore requires 'cosine' (its scores are computed as 1 - cosine " +
            "distance). Delete and recreate the collection (re-ingesting its documents) or " +
            "configure the store with a cosine collection.");
    }

    private static ChromaUpsertRequest BuildUpsertRequest(IReadOnlyList<EmbeddedChunk> chunks)
    {
        var ids = new string[chunks.Count];
        var embeddings = new float[chunks.Count][];
        var documents = new string[chunks.Count];
        var metadatas = new IDictionary<string, object?>[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i].Chunk;
            ids[i] = RecordId((string)chunk.DocumentId, chunk.ChunkIndex);
            embeddings[i] = chunks[i].Embedding.ToArray();
            documents[i] = chunk.Text;

            // Chroma metadata values are natively typed (str/int/float/bool) but exclude
            // objects, so dates ride as sentinel-prefixed ISO strings and are decoded on read.
            var metadata = new Dictionary<string, object?>(chunk.Metadata.Count + 2, StringComparer.Ordinal);
            foreach (var kvp in chunk.Metadata)
                metadata[kvp.Key] = ToChromaValue(kvp.Value);
            // Written last so the reserved keys always win over same-named chunk metadata.
            metadata["document_id"] = (string)chunk.DocumentId;
            metadata["chunk_index"] = chunk.ChunkIndex;
            metadatas[i] = metadata;
        }

        return new ChromaUpsertRequest
        {
            Ids = ids,
            Embeddings = embeddings,
            Documents = documents,
            Metadatas = metadatas,
        };
    }

    /// <summary>
    /// Chroma where filter: typed <c>$eq</c> per key, <c>$and</c>-composed for two or more keys.
    /// The <c>$eq</c> operand carries the same native type <see cref="ToChromaValue"/> stored, so
    /// a Number filter matches a stored number and never a same-looking string. Metadata stored
    /// before values carried types is all strings, so non-string filters do not match it until
    /// re-ingested.
    /// </summary>
    private static Dictionary<string, object?>? BuildWhereFilter(IDictionary<string, MetadataValue>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        if (metadataFilter.Count == 1)
        {
            foreach (var kvp in metadataFilter)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [kvp.Key] = EqualityClause(kvp.Value),
                };
            }
        }

        var operands = new List<object>(metadataFilter.Count);
        foreach (var kvp in metadataFilter)
        {
            operands.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [kvp.Key] = EqualityClause(kvp.Value),
            });
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["$and"] = operands };
    }

    private static Dictionary<string, object?> EqualityClause(MetadataValue value) =>
        new(StringComparer.Ordinal) { ["$eq"] = ToChromaValue(value) };

    /// <summary>The native Chroma metadata value for one metadata value, keeping its type.</summary>
    private static object? ToChromaValue(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.Number => value.NumberValue,
        MetadataValueKind.Boolean => value.BooleanValue,
        MetadataValueKind.DateTimeOffset => MetadataDateFormat.EncodeSentinel(value.DateTimeOffsetValue),
        _ => value.StringValue,
    };

    private static IReadOnlyList<SearchResult> MapResults(ChromaQueryResponse response, double minScore)
    {
        // Nested arrays: one inner row per query embedding; the store always sends one.
        var ids = FirstRow(response.Ids);
        if (ids is not { Count: > 0 })
            return [];

        var documents = FirstRow(response.Documents);
        var metadatas = FirstRow(response.Metadatas);
        var distances = FirstRow(response.Distances);
        if (distances is null || distances.Count != ids.Count)
        {
            throw new InvalidOperationException(
                "Chroma query response is missing distances for the returned records.");
        }

        if ((documents is not null && documents.Count != ids.Count)
            || (metadatas is not null && metadatas.Count != ids.Count))
        {
            throw new InvalidOperationException(
                "Chroma query response returned ragged documents/metadatas rows for the returned records.");
        }

        var results = new List<SearchResult>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            // Cosine distance (0 identical … 2 opposite) → score in [-1, 1].
            var score = 1.0 - distances[i];
            if (score < minScore)
                continue;

            results.Add(BuildResult(ids[i], documents?[i], metadatas?[i], score));
        }

        return results;
    }

    private static TRow? FirstRow<TRow>(IReadOnlyList<TRow?>? rows)
        where TRow : class =>
        rows is { Count: > 0 } ? rows[0] : null;

    /// <summary>
    /// Missing <c>document_id</c>/<c>chunk_index</c> metadata is backend corruption — the
    /// store posture is to throw naming the record, never to guess from the record id
    /// (document ids may themselves contain the <c>:</c> separator).
    /// </summary>
    private static SearchResult BuildResult(
        string recordId,
        string? document,
        Dictionary<string, JsonElement>? metadata,
        double score)
    {
        if (metadata is null
            || !metadata.TryGetValue("document_id", out var documentId)
            || documentId.ValueKind != JsonValueKind.String
            || !metadata.TryGetValue("chunk_index", out var chunkIndex)
            || chunkIndex.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException(
                $"Chroma record '{recordId}' is missing its document_id/chunk_index metadata.");
        }

        var chunkMetadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var kvp in metadata)
        {
            if (kvp.Key is "document_id" or "chunk_index")
                continue;
            chunkMetadata[kvp.Key] = FromChromaValue(kvp.Value);
        }

        return new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = document ?? string.Empty,
                DocumentId = new DocumentId(documentId.GetString()!),
                ChunkIndex = chunkIndex.GetInt32(),
                Metadata = chunkMetadata,
            },
            Score = score,
        };
    }

    /// <summary>
    /// Maps a Chroma metadata value back to a <see cref="MetadataValue"/> with its kind: JSON
    /// numbers and booleans directly, sentinel-encoded strings to dates, all other strings —
    /// including every value stored before metadata carried types — as strings.
    /// </summary>
    private static MetadataValue FromChromaValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when MetadataDateFormat.TryDecodeSentinel(value.GetString()!, out var date) => date,
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };

    /// <summary>
    /// <paramref name="operation"/> must name the collection it acted on (the collection
    /// lifecycle methods take arbitrary names, not just the configured collection).
    /// </summary>
    private static T Unwrap<T>(Result<T, HttpError> result, string operation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Chroma {operation} failed with {FormatHttpError(result.Error)}.");
        }

        return result.Value;
    }

    /// <summary>
    /// ZeroAlloc.Rest's failure path discards the response body, so <c>Error.Message</c> is
    /// frequently empty — omit the tail instead of ending the message in a dangling colon.
    /// </summary>
    private static string FormatHttpError(HttpError error) =>
        string.IsNullOrEmpty(error.Message)
            ? $"HTTP {(int)error.StatusCode}"
            : $"HTTP {(int)error.StatusCode}: {error.Message}";

    private static HttpClient CreateHttpClient(ChromaOptions options)
    {
        // Same transient-fault posture as the DI-registered ZeroAlloc.Rest providers
        // (AddStandardResilienceHandler): retry on 5xx/408/timeouts. Safe for our POSTs —
        // record ids are deterministic, so every write is an idempotent upsert.
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions())
            .Build();
        var handler = new ResilienceHandler(pipeline)
        {
            // Finite pooled-connection lifetime so this singleton's long-lived HttpClient
            // still observes DNS changes (the IHttpClientFactory rotation equivalent).
            InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
        };
        var client = new HttpClient(handler) { BaseAddress = options.Endpoint };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(options.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return client;
    }
}
